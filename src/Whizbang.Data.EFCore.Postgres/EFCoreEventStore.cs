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
using Whizbang.Data.EFCore.Postgres.Entities;
using Whizbang.Data.EFCore.Postgres.Serialization;

namespace Whizbang.Data.EFCore.Postgres;

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

    // Serialize envelope data to JSON using JsonTypeInfo for AOT compatibility
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson);
    var metadata = SerializeMetadata(envelope);
    var scope = SerializeScope(envelope);

    var record = new EventStoreRecord {
      StreamId = streamId,
      Sequence = nextSequence,
      EventType = typeof(TMessage).FullName ?? "Unknown",
      EventData = eventData,
      Metadata = metadata,
      Scope = scope,
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
      // Deserialize the event data using JsonTypeInfo for AOT compatibility
      var eventDataJson = record.EventData.RootElement.GetRawText();
      var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
      var eventData = JsonSerializer.Deserialize(eventDataJson, typeInfo);
      if (eventData == null) {
        throw new InvalidOperationException($"Failed to deserialize event at sequence {record.Sequence}");
      }

      // Deserialize metadata to reconstruct envelope
      var metadataJson = record.Metadata.RootElement.GetRawText();
      var metadata = JsonSerializer.Deserialize(metadataJson, EFCoreJsonContext.Default.EnvelopeMetadataDto);
      if (metadata == null) {
        throw new InvalidOperationException($"Failed to deserialize metadata at sequence {record.Sequence}");
      }

      // Reconstruct message hops from metadata
      var hops = new List<MessageHop>();
      foreach (var hop in metadata.Hops) {
        // Parse HopType enum
        var hopType = Enum.TryParse<HopType>(hop.Type, out var parsedType) ? parsedType : HopType.Current;

        // Reconstruct security context if present
        SecurityContext? securityContext = null;
        if (hop.SecurityContext != null) {
          securityContext = new SecurityContext {
            UserId = hop.SecurityContext.UserId,
            TenantId = hop.SecurityContext.TenantId
          };
        }

        var messageHop = new MessageHop {
          Type = hopType,
          Topic = hop.Topic,
          StreamKey = hop.StreamKey,
          PartitionIndex = hop.PartitionIndex,
          SequenceNumber = hop.SequenceNumber,
          SecurityContext = securityContext,
          Metadata = hop.Metadata,
          CallerMemberName = hop.CallerMemberName,
          CallerFilePath = hop.CallerFilePath,
          CallerLineNumber = hop.CallerLineNumber,
          Timestamp = hop.Timestamp,
          Duration = hop.Duration ?? TimeSpan.Zero,
          ServiceName = "Unknown", // Not serialized in old data, default value
          ServiceInstanceId = Guid.Empty, // Not serialized in old data, use empty guid
          CorrelationId = metadata.CorrelationId != null ? CorrelationId.Parse(metadata.CorrelationId) : null,
          CausationId = metadata.CausationId != null ? MessageId.Parse(metadata.CausationId) : null
        };
        hops.Add(messageHop);
      }

      // Reconstruct the message envelope using the constructor
      var envelope = new MessageEnvelope<TMessage>(
        MessageId.Parse(record.StreamId.ToString()), // Use StreamId as message context
        eventData!,
        hops
      );

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
  /// Serializes envelope metadata (correlation, causation, hops) to JSON.
  /// </summary>
  private static JsonDocument SerializeMetadata(IMessageEnvelope envelope) {
    var metadata = new EnvelopeMetadataDto {
      CorrelationId = envelope.GetCorrelationId()?.ToString(),
      CausationId = envelope.GetCausationId()?.ToString(),
      Timestamp = envelope.GetMessageTimestamp(),
      Hops = envelope.Hops.Select(h => new HopMetadataDto {
        Type = h.Type.ToString(),
        Topic = h.Topic,
        StreamKey = h.StreamKey,
        PartitionIndex = h.PartitionIndex,
        SequenceNumber = h.SequenceNumber,
        SecurityContext = h.SecurityContext != null ? new SecurityContextDto {
          UserId = h.SecurityContext.UserId?.ToString(),
          TenantId = h.SecurityContext.TenantId?.ToString()
        } : null,
        Metadata = h.Metadata,
        CallerMemberName = h.CallerMemberName,
        CallerFilePath = h.CallerFilePath,
        CallerLineNumber = h.CallerLineNumber,
        Timestamp = h.Timestamp,
        Duration = h.Duration
      }).ToList()
    };

    var json = JsonSerializer.Serialize(metadata, EFCoreJsonContext.Default.EnvelopeMetadataDto);
    return JsonDocument.Parse(json);
  }

  /// <summary>
  /// Serializes security scope (tenant, user) to JSON if present.
  /// </summary>
  private static JsonDocument? SerializeScope(IMessageEnvelope envelope) {
    // Extract security context from first hop if available
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.SecurityContext == null) {
      return null;
    }

    var scope = new ScopeDto {
      UserId = firstHop.SecurityContext.UserId?.ToString(),
      TenantId = firstHop.SecurityContext.TenantId?.ToString()
    };

    var json = JsonSerializer.Serialize(scope, EFCoreJsonContext.Default.ScopeDto);
    return JsonDocument.Parse(json);
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
