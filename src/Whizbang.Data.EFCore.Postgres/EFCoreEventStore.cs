using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Serialization;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IEventStore using PostgreSQL with JSONB columns.
/// Provides append-only event storage for event sourcing and streaming scenarios.
/// Stores events with stream-based organization using sequence numbers.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs</tests>
public sealed class EFCoreEventStore<TDbContext> : IEventStore
  where TDbContext : DbContext {

  private readonly TDbContext _context;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly Whizbang.Core.Perspectives.IPerspectiveInvoker? _perspectiveInvoker;

  public EFCoreEventStore(
    TDbContext context,
    JsonSerializerOptions? jsonOptions = null,
    Whizbang.Core.Perspectives.IPerspectiveInvoker? perspectiveInvoker = null) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _jsonOptions = jsonOptions ?? EFCoreJsonContext.CreateCombinedOptions();
    _perspectiveInvoker = perspectiveInvoker;
  }

  /// <summary>
  /// Appends an event to the specified stream.
  /// Assigns the next sequence number automatically.
  /// Ensures optimistic concurrency through unique constraint on (StreamId, Sequence).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithValidEnvelope_AppendsEventToStreamAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithMultipleEvents_AssignsSequentialSequenceNumbersAsync</tests>
  public async Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);

    // Get the next sequence number for this stream
    var lastSequence = await GetLastSequenceAsync(streamId, cancellationToken);
    var nextSequence = lastSequence + 1;

    // Serialize envelope.Payload to JsonElement for type-erased storage
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson).RootElement;

    // Create envelope metadata directly - EF Core will serialize via POCO mapping
    var metadata = new EnvelopeMetadata {
      MessageId = envelope.MessageId,
      Hops = envelope.Hops.ToList()
    };

    var record = new EventStoreRecord {
      Id = envelope.MessageId.Value,  // Use MessageId from envelope as event_id (matches outbox message_id)
      StreamId = streamId,
      AggregateId = streamId,  // Backwards compatibility: AggregateId = StreamId
      AggregateType = typeof(TMessage).FullName ?? "Unknown",  // Aggregate type from event type
      Sequence = nextSequence,
      Version = (int)nextSequence,  // Backwards compatibility: Version = Sequence
      // Use centralized formatter for consistent type name format across all event stores
      // Format: "TypeName, AssemblyName" (medium form)
      // This matches wh_message_associations format and enables auto-checkpoint creation
      // Fuzzy matching in migration 006 handles AssemblyQualifiedName (long form) differences
      EventType = TypeNameFormatter.Format(typeof(TMessage)),
      EventData = eventData,
      Metadata = metadata,
      CreatedAt = DateTime.UtcNow
    };

    await _context.Set<EventStoreRecord>().AddAsync(record, cancellationToken);

    try {
      await _context.SaveChangesAsync(cancellationToken);
    } catch (DbUpdateException ex) when (_isDuplicateKeyException(ex)) {
      // Concurrent append detected - optimistic concurrency failure
      throw new InvalidOperationException(
        $"Concurrent modification detected for stream {streamId} at sequence {nextSequence}. " +
        "Another process has already appended to this stream.",
        ex);
    }

    // NOTE: Inline perspective invocation removed - perspectives are now processed via PerspectiveWorker
    // using checkpoint-based processing for better reliability and scalability.
    // See: Stage 4 of perspective worker refactoring (2025-12-18)
  }

  /// <summary>
  /// Reads events from a stream with strong typing.
  /// Returns events in sequence order starting from the specified sequence number.
  /// Uses IAsyncEnumerable for efficient streaming of large event sequences.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:ReadAsync_WithExistingEvents_ReturnsEventsInSequenceOrderAsync</tests>
  public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      long fromSequence,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    // Query events from the specified sequence onwards
    var query = _context.Set<EventStoreRecord>()
      .Where(e => e.StreamId == streamId && e.Sequence >= fromSequence)
      .OrderBy(e => e.Sequence)
      .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Deserialize the event payload using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo);
      if (eventData == null) {
        throw new InvalidOperationException($"Failed to deserialize event at sequence {record.Sequence}");
      }

      // Metadata is already strongly-typed EnvelopeMetadata - use directly
      var metadata = record.Metadata;

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      // CRITICAL: Use record.Id (event_id column) as MessageId, NOT metadata.MessageId
      // This ensures the MessageId matches the event_id in wh_event_store for FK constraint integrity
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = MessageId.From(record.Id),
        Payload = eventData,
        Hops = metadata.Hops
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Reads events from a stream with strong typing starting after a specific event ID.
  /// Returns events in UUIDv7 order (time-ordered) - no sequence numbers needed.
  /// Supports perspective checkpoint processing where last processed event ID is tracked.
  /// Uses IAsyncEnumerable for efficient streaming of large event sequences.
  /// </summary>
  public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId,
      Guid? fromEventId,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    // Query events from the specified event ID onwards
    // UUIDv7 is time-ordered, so we can order by Id directly
    var query = fromEventId == null
      ? _context.Set<EventStoreRecord>()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable()
      : _context.Set<EventStoreRecord>()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Deserialize the event payload using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo);
      if (eventData == null) {
        throw new InvalidOperationException($"Failed to deserialize event ID {record.Id}");
      }

      // Metadata is already strongly-typed EnvelopeMetadata - use directly
      var metadata = record.Metadata;

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      // CRITICAL: Use record.Id (event_id column) as MessageId, NOT metadata.MessageId
      // This ensures the MessageId matches the event_id in wh_event_store for FK constraint integrity
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = MessageId.From(record.Id),
        Payload = eventData,
        Hops = metadata.Hops
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Reads events from a stream polymorphically, deserializing each event to its concrete type.
  /// Uses the EventType column to determine which concrete type to deserialize to.
  /// </summary>
  public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId,
      Guid? fromEventId,
      IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {

    // Build type lookup dictionary with multiple keys for flexible matching
    // Supports: TypeNameFormatter format (medium form), AssemblyQualifiedName (long form), FullName, and Name (short form)
    var typeMap = new Dictionary<string, Type>();
    foreach (var type in eventTypes) {
      // Add all possible keys for this type

      // PRIMARY KEY: TypeNameFormatter format ("TypeName, AssemblyName")
      // This is the format used by AppendAsync (line 90) and matches wh_message_associations
      var formattedName = TypeNameFormatter.Format(type);
      typeMap[formattedName] = type;

      // FALLBACK KEYS: Support other formats for compatibility
      if (type.AssemblyQualifiedName != null) {
        typeMap[type.AssemblyQualifiedName] = type;
      }
      if (type.FullName != null) {
        typeMap[type.FullName] = type;
      }
      typeMap[type.Name] = type;
    }

    // Query events from the specified event ID onwards
    var query = fromEventId == null
      ? _context.Set<EventStoreRecord>()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable()
      : _context.Set<EventStoreRecord>()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Look up the concrete type from the EventType column
      // Try exact match first, then fall back to comparing just the type+assembly part
      if (!typeMap.TryGetValue(record.EventType, out var concreteType)) {
        // Try without version/culture/token (extract "TypeName, AssemblyName" from full qualified name)
        var typeAndAssembly = string.Join(", ", record.EventType.Split(',').Take(2).Select(s => s.Trim()));
        if (!string.IsNullOrEmpty(typeAndAssembly) && typeMap.TryGetValue(typeAndAssembly, out concreteType)) {
          // Found via simplified name
        } else {
          // Event type not in the requested list - skip it
          // This allows perspectives to materialize subsets of events from shared streams
          // (e.g., ProductCatalogPerspective skips InventoryRestockedEvent in product streams)
          continue;
        }
      }

      // Deserialize the event payload to the concrete type
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(concreteType);
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo);
      if (eventData == null) {
        throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");
      }

      // Metadata is already strongly-typed EnvelopeMetadata - use directly
      var metadata = record.Metadata;

      // Reconstruct the message envelope with the polymorphic payload cast to IEvent
      var envelope = new MessageEnvelope<IEvent> {
        MessageId = metadata.MessageId,
        Payload = (IEvent)eventData,
        Hops = metadata.Hops
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Gets the last (highest) sequence number for a stream.
  /// Returns -1 if the stream doesn't exist or is empty.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetLastSequenceAsync_WithEmptyStream_ReturnsMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetLastSequenceAsync_WithExistingEvents_ReturnsHighestSequenceAsync</tests>
  public async Task<long> GetLastSequenceAsync(
      Guid streamId,
      CancellationToken cancellationToken = default) {

    var lastSequence = await _context.Set<EventStoreRecord>()
      .Where(e => e.StreamId == streamId)
      .MaxAsync(e => (long?)e.Sequence, cancellationToken);

    return lastSequence ?? -1;
  }

  /// <summary>
  /// Checks if the exception is due to a duplicate key constraint violation.
  /// PostgreSQL uses error code 23505 for unique constraint violations.
  /// </summary>
  private static bool _isDuplicateKeyException(DbUpdateException ex) {
    // Check for PostgreSQL unique constraint violation
    // The error message typically contains "23505" or "duplicate key"
    return ex.InnerException?.Message.Contains("23505") == true ||
           ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
  }
}
