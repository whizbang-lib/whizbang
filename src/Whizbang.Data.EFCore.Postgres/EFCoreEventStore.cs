using Whizbang.Core.Dispatch;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Serialization;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IEventStore using PostgreSQL with JSONB columns.
/// Provides append-only event storage for event sourcing and streaming scenarios.
/// Stores events with stream-based organization using sequence numbers.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs</tests>
#pragma warning disable S2743 // Static diagnostic flag is intentionally per-generic-type (reads same env var)
public sealed class EFCoreEventStore<TDbContext> : IEventStore
  where TDbContext : DbContext {

  private readonly TDbContext _context;
  private readonly JsonSerializerOptions _jsonOptions;


  public EFCoreEventStore(
    TDbContext context,
    JsonSerializerOptions? jsonOptions = null) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _jsonOptions = jsonOptions ?? EFCoreJsonContext.CreateCombinedOptions();
  }

  /// <summary>
  /// Appends an event to the specified stream.
  /// Assigns the next sequence number automatically.
  /// Ensures optimistic concurrency through unique constraint on (StreamId, Sequence).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithValidEnvelope_AppendsEventToStreamAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:AppendAsync_WithMultipleEvents_AssignsSequentialSequenceNumbersAsync</tests>
  public Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);
    return _appendCoreAsync(streamId, envelope, cancellationToken);
  }

  /// <inheritdoc />
  public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);

    // Create a minimal envelope - registry-based lookup would require constructor injection
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = System.Diagnostics.Activity.Current?.Id
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    return AppendAsync(streamId, envelope, cancellationToken);
  }

  private async Task _appendCoreAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken) {
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
      Hops = envelope.Hops?.ToList() ?? []
    };

    var record = new EventStoreRecord {
      Id = envelope.MessageId.Value,  // Use MessageId from envelope as event_id (matches outbox message_id)
      StreamId = streamId,
      AggregateId = streamId,  // Backwards compatibility: AggregateId = StreamId
      AggregateType = typeof(TMessage).FullName ?? "Unknown",  // Aggregate type from event type
      Version = (int)nextSequence,  // Version for optimistic concurrency
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
      .AsNoTracking()
      .Where(e => e.StreamId == streamId && e.Version >= fromSequence)
      .OrderBy(e => e.Version)
      .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Deserialize the event payload using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event at version {record.Version}");

      var hops = _restoreScopeInHops(record);

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      // CRITICAL: Use record.Id (event_id column) as MessageId, NOT metadata.MessageId
      // This ensures the MessageId matches the event_id in wh_event_store for FK constraint integrity
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = MessageId.From(record.Id),
        Payload = eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
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
          .AsNoTracking()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable()
      : _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Deserialize the event payload using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id}");

      var hops = _restoreScopeInHops(record);

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      // CRITICAL: Use record.Id (event_id column) as MessageId, NOT metadata.MessageId
      // This ensures the MessageId matches the event_id in wh_event_store for FK constraint integrity
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = MessageId.From(record.Id),
        Payload = eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
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
          .AsNoTracking()
          .Where(e => e.StreamId == streamId)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable()
      : _context.Set<EventStoreRecord>()
          .AsNoTracking()
          .Where(e => e.StreamId == streamId && e.Id.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.Id)
          .AsAsyncEnumerable();

    await foreach (var record in query.WithCancellation(cancellationToken)) {
      // Look up the concrete type from the EventType column
      var concreteType = _resolveConcreteType(record.EventType, typeMap);
      if (concreteType == null) {
        continue;
      }

      // Deserialize the event payload to the concrete type
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(concreteType);
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");

      var hops = _restoreScopeInHops(record);

      // Reconstruct the message envelope with the polymorphic payload cast to IEvent
      var envelope = new MessageEnvelope<IEvent> {
        MessageId = record.Metadata.MessageId,
        Payload = (IEvent)eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Gets events between two checkpoint positions (exclusive start, inclusive end).
  /// Used by lifecycle receptors to load events that were just processed by a perspective.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_WithEventsInRange_ReturnsEventsBetweenCheckpointsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_NullAfterEventId_ReturnsFromStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_NoEventsInRange_ReturnsEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenAsync_MultipleEvents_ReturnsInUuidV7OrderAsync</tests>
  public async Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) {

    // Build query: after afterEventId (exclusive), up to upToEventId (inclusive)
    // Guid.Empty means "no upper bound" - read all events for the stream
    IQueryable<EventStoreRecord> query = _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId);

    // Apply upper bound only if upToEventId is not Guid.Empty
    if (upToEventId != Guid.Empty) {
      query = query.Where(e => e.Id <= upToEventId);
    }

    if (afterEventId != null) {
      query = query.Where(e => e.Id > afterEventId.Value);
    }

    // Order by UUID v7 (time-ordered)
    var records = await query
      .OrderBy(e => e.Id)
      .ToListAsync(cancellationToken);

    // Deserialize to message envelopes
    var envelopes = new List<MessageEnvelope<TMessage>>(records.Count);

    foreach (var record in records) {
      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");

      var hops = _restoreScopeInHops(record);

      envelopes.Add(new MessageEnvelope<TMessage> {
        MessageId = record.Metadata.MessageId,
        Payload = (TMessage)eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      });
    }

    return envelopes;
  }

  /// <summary>
  /// Gets events between two checkpoint positions, deserializing each event to its concrete type.
  /// Uses the EventType column to determine which concrete type to deserialize to.
  /// This is the polymorphic version of GetEventsBetweenAsync for perspectives that handle multiple event types.
  /// Used by lifecycle receptors to load events that were just processed by a perspective.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_WithMixedEventTypes_ReturnsAllEventsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_NullAfterEventId_ReturnsFromStartAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_NoEventsInRange_ReturnsEmptyListAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreEventStoreTests.cs:GetEventsBetweenPolymorphicAsync_UnknownEventType_SkipsUnknownEventsAsync</tests>
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(eventTypes);
    return _getEventsBetweenPolymorphicCoreAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
  }

  private async Task<List<MessageEnvelope<IEvent>>> _getEventsBetweenPolymorphicCoreAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken) {
    // Build query: after afterEventId (exclusive), up to upToEventId (inclusive)
    // Guid.Empty means "no upper bound" - read all events for the stream
    IQueryable<EventStoreRecord> query = _context.Set<EventStoreRecord>()
      .AsNoTracking()
      .Where(e => e.StreamId == streamId);

    // Apply upper bound only if upToEventId is not Guid.Empty
    if (upToEventId != Guid.Empty) {
      query = query.Where(e => e.Id <= upToEventId);
    }

    if (afterEventId != null) {
      query = query.Where(e => e.Id > afterEventId.Value);
    }

    // Order by UUID v7 (time-ordered)
    var records = await query
      .OrderBy(e => e.Id)
      .ToListAsync(cancellationToken);

    // Build type lookup dictionary for fast O(1) lookups (AOT-compatible)
    var typeLookup = new Dictionary<string, Type>(eventTypes.Count);
    foreach (var type in eventTypes) {
      typeLookup[type.FullName ?? type.Name] = type;
    }

    // Deserialize to message envelopes with polymorphic payloads
    var envelopes = new List<MessageEnvelope<IEvent>>(records.Count);

    foreach (var record in records) {
      // Normalize event type name (remove assembly version/culture/publickey if present)
      var storedTypeName = record.EventType;
      var commaIndex = storedTypeName.IndexOf(',');
      var normalizedTypeName = commaIndex > 0 ? storedTypeName[..commaIndex].Trim() : storedTypeName;

      // Skip events that aren't in the perspective's list
      if (!typeLookup.TryGetValue(normalizedTypeName, out var concreteType)) {
        continue;
      }

      var eventDataJson = record.EventData.GetRawText();
      var typeInfo = _jsonOptions.GetTypeInfo(concreteType);
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event ID {record.Id} of type {record.EventType}");

      var hops = _restoreScopeInHops(record);

      envelopes.Add(new MessageEnvelope<IEvent> {
        MessageId = record.Metadata.MessageId,
        Payload = (IEvent)eventData,
        Hops = hops,
        DispatchContext = record.Metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
      });
    }

    return envelopes;
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
      .AsNoTracking()
      .Where(e => e.StreamId == streamId)
      .MaxAsync(e => (long?)e.Version, cancellationToken);

    return lastSequence ?? -1;
  }

  /// <summary>
  /// Resolves a concrete type from the stored EventType string using the type map.
  /// Returns null if the type is not in the requested list (allows perspectives to skip irrelevant events).
  /// </summary>
  private static Type? _resolveConcreteType(string eventType, Dictionary<string, Type> typeMap) {
    if (typeMap.TryGetValue(eventType, out var concreteType)) {
      return concreteType;
    }

    // Try without version/culture/token (extract "TypeName, AssemblyName" from full qualified name)
    var typeAndAssembly = string.Join(", ", eventType.Split(',').Take(2).Select(s => s.Trim()));
    if (!string.IsNullOrEmpty(typeAndAssembly) && typeMap.TryGetValue(typeAndAssembly, out concreteType)) {
      return concreteType;
    }

    return null;
  }

  /// <summary>
  /// Restores scope from the dedicated scope column into the first hop's ScopeDelta.
  /// Returns the (possibly modified) hops list.
  /// </summary>
  private static List<MessageHop> _restoreScopeInHops(EventStoreRecord record) {
    var hops = record.Metadata.Hops.ToList();
    if (record.Scope == null || hops.Count == 0 || hops[0].Scope != null) {
      return hops;
    }

    var scopeDelta = ScopeDelta.FromPerspectiveScope(record.Scope);
    if (scopeDelta == null) {
      return hops;
    }

    hops[0] = hops[0] with { Scope = scopeDelta };
    return hops;
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
