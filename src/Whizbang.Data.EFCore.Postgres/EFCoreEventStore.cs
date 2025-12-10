using System.Diagnostics.CodeAnalysis;
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
/// Metadata structure for serializing envelope metadata to JSONB.
/// Contains MessageId and Hops - serialized directly by System.Text.Json.
/// Public for AOT source generation, but not intended for external use.
/// </summary>
public sealed class EnvelopeMetadata {
  public required MessageId MessageId { get; init; }
  public required List<MessageHop> Hops { get; init; }
}

/// <summary>
/// EF Core implementation of IEventStore using PostgreSQL with JSONB columns.
/// Provides append-only event storage for event sourcing and streaming scenarios.
/// Stores events with stream-based organization using sequence numbers.
/// </summary>
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
  public async Task AppendAsync<TMessage>(
      Guid streamId,
      MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);

    // Get the next sequence number for this stream
    var lastSequence = await GetLastSequenceAsync(streamId, cancellationToken);
    var nextSequence = lastSequence + 1;

    // Serialize envelope.Payload to EventData using JsonTypeInfo for AOT compatibility
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson);

    // Serialize envelope metadata (MessageId + Hops) directly - no DTO mapping
    var metadata = new EnvelopeMetadata {
      MessageId = envelope.MessageId,
      Hops = envelope.Hops.ToList()
    };
    var metadataTypeInfo = (JsonTypeInfo<EnvelopeMetadata>)_jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata));
    var metadataJson = JsonSerializer.Serialize(metadata, metadataTypeInfo);
    var metadataDoc = JsonDocument.Parse(metadataJson);

    var record = new EventStoreRecord {
      StreamId = streamId,
      AggregateId = streamId,  // Backwards compatibility: AggregateId = StreamId
      AggregateType = typeof(TMessage).FullName ?? "Unknown",  // Aggregate type from event type
      Sequence = nextSequence,
      Version = (int)nextSequence,  // Backwards compatibility: Version = Sequence
      EventType = typeof(TMessage).FullName ?? "Unknown",
      EventData = eventData,
      Metadata = metadataDoc,
      CreatedAt = DateTime.UtcNow
    };

    await _context.Set<EventStoreRecord>().AddAsync(record, cancellationToken);

    try {
      await _context.SaveChangesAsync(cancellationToken);
    } catch (DbUpdateException ex) when (IsDuplicateKeyException(ex)) {
      // Concurrent append detected - optimistic concurrency failure
      throw new InvalidOperationException(
        $"Concurrent modification detected for stream {streamId} at sequence {nextSequence}. " +
        "Another process has already appended to this stream.",
        ex);
    }

    // Queue event for perspective invocation at scope disposal
    if (_perspectiveInvoker != null && envelope.Payload is IEvent @event) {
      _perspectiveInvoker.QueueEvent(@event);
    }
  }

  /// <summary>
  /// Reads events from a stream with strong typing.
  /// Returns events in sequence order starting from the specified sequence number.
  /// Uses IAsyncEnumerable for efficient streaming of large event sequences.
  /// </summary>
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
      var eventDataJson = record.EventData.RootElement.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo);
      if (eventData == null) {
        throw new InvalidOperationException($"Failed to deserialize event at sequence {record.Sequence}");
      }

      // Deserialize metadata (MessageId + Hops) directly - no DTO mapping
      var metadataJson = record.Metadata.RootElement.GetRawText();
      var metadataTypeInfo = (JsonTypeInfo<EnvelopeMetadata>)_jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata));
      var metadata = JsonSerializer.Deserialize(metadataJson, metadataTypeInfo);
      if (metadata == null) {
        throw new InvalidOperationException($"Failed to deserialize metadata at sequence {record.Sequence}");
      }

      // Reconstruct the message envelope - ServiceInstanceInfo is already in the hops
      var envelope = new MessageEnvelope<TMessage> {
        MessageId = metadata.MessageId,
        Payload = eventData,
        Hops = metadata.Hops
      };

      yield return envelope;
    }
  }

  /// <summary>
  /// Gets the last (highest) sequence number for a stream.
  /// Returns -1 if the stream doesn't exist or is empty.
  /// </summary>
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
  private static bool IsDuplicateKeyException(DbUpdateException ex) {
    // Check for PostgreSQL unique constraint violation
    // The error message typically contains "23505" or "duplicate key"
    return ex.InnerException?.Message.Contains("23505") == true ||
           ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
  }
}
