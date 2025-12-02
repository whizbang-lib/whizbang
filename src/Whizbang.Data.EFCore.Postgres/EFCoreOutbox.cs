using System.Diagnostics.CodeAnalysis;
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
/// EF Core implementation of IOutbox using PostgreSQL with JSONB columns.
/// Provides transactional outbox pattern for reliable message publishing.
/// Stores messages to be published with their full envelope metadata.
/// Supports both immediate processing (with instanceId) and polling-based (without instanceId).
/// </summary>
public sealed class EFCoreOutbox<TDbContext> : IOutbox
  where TDbContext : DbContext {

  private readonly TDbContext _context;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly Guid? _instanceId;
  private readonly int _leaseSeconds;

  /// <summary>
  /// Creates a new EFCoreOutbox instance.
  /// </summary>
  /// <param name="context">The database context</param>
  /// <param name="jsonOptions">JSON serialization options (optional, uses default if null)</param>
  /// <param name="instanceId">Service instance ID for lease-based coordination (optional, enables immediate processing)</param>
  /// <param name="leaseSeconds">Lease duration in seconds (default 300 = 5 minutes)</param>
  public EFCoreOutbox(
    TDbContext context,
    JsonSerializerOptions? jsonOptions = null,
    Guid? instanceId = null,
    int leaseSeconds = 300
  ) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _jsonOptions = jsonOptions ?? EFCoreJsonContext.CreateCombinedOptions();
    _instanceId = instanceId;
    _leaseSeconds = leaseSeconds;
  }

  /// <summary>
  /// Stores a message envelope in the outbox for publication.
  /// Extracts envelope data into JSONB columns for efficient querying.
  /// When instanceId is configured, sets status to "Publishing" with lease for immediate processing.
  /// </summary>
  public async Task<OutboxMessage> StoreAsync<TMessage>(
      MessageEnvelope<TMessage> envelope,
      string destination,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);
    if (string.IsNullOrWhiteSpace(destination)) {
      throw new ArgumentException("Destination cannot be null or whitespace.", nameof(destination));
    }

    // Serialize envelope data to JSON using JsonTypeInfo for AOT compatibility
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson);
    var metadata = SerializeMetadata(envelope);
    var scope = SerializeScope(envelope);

    var now = DateTimeOffset.UtcNow;

    var record = new OutboxRecord {
      MessageId = envelope.MessageId.ToString(),
      Destination = destination,
      MessageType = typeof(TMessage).FullName ?? throw new InvalidOperationException("Message type has no FullName"),
      MessageData = eventData,
      Metadata = metadata,
      Scope = scope,
      Status = _instanceId.HasValue ? "Publishing" : "Pending",
      Attempts = 0,
      Error = null,
      CreatedAt = now,
      PublishedAt = null,
      Topic = destination,  // Legacy field, same as Destination
      PartitionKey = null,  // Can be extracted from envelope if needed
      InstanceId = _instanceId,
      LeaseExpiry = _instanceId.HasValue ? now.AddSeconds(_leaseSeconds) : null
    };

    await _context.Set<OutboxRecord>().AddAsync(record, cancellationToken);
    await _context.SaveChangesAsync(cancellationToken);

    return new OutboxMessage(
      envelope.MessageId,
      destination,
      record.MessageType,
      eventDataJson,
      metadata.RootElement.GetRawText(),
      scope?.RootElement.GetRawText(),
      now
    );
  }

  /// <summary>
  /// Stores a message envelope in the outbox for publication (non-generic overload).
  /// Extracts envelope data into JSONB columns for efficient querying.
  /// Uses runtime type information from the envelope. AOT-compatible - no reflection.
  /// When instanceId is configured, sets status to "Publishing" with lease for immediate processing.
  /// </summary>
  public async Task<OutboxMessage> StoreAsync(
      IMessageEnvelope envelope,
      string destination,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);
    if (string.IsNullOrWhiteSpace(destination)) {
      throw new ArgumentException("Destination cannot be null or whitespace.", nameof(destination));
    }

    // Get payload and its runtime type (AOT-compatible - no reflection)
    var payload = envelope.GetPayload();
    var payloadType = payload.GetType();

    // Serialize envelope data to JSON using runtime type info
    var typeInfo = _jsonOptions.GetTypeInfo(payloadType);
    var eventDataJson = JsonSerializer.Serialize(payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson);
    var metadata = SerializeMetadata(envelope);
    var scope = SerializeScope(envelope);

    var now = DateTimeOffset.UtcNow;

    var record = new OutboxRecord {
      MessageId = envelope.MessageId.ToString(),
      Destination = destination,
      MessageType = payloadType.FullName ?? throw new InvalidOperationException("Message type has no FullName"),
      MessageData = eventData,
      Metadata = metadata,
      Scope = scope,
      Status = _instanceId.HasValue ? "Publishing" : "Pending",
      Attempts = 0,
      Error = null,
      CreatedAt = now,
      PublishedAt = null,
      Topic = destination,  // Legacy field, same as Destination
      PartitionKey = null,  // Can be extracted from envelope if needed
      InstanceId = _instanceId,
      LeaseExpiry = _instanceId.HasValue ? now.AddSeconds(_leaseSeconds) : null
    };

    await _context.Set<OutboxRecord>().AddAsync(record, cancellationToken);
    await _context.SaveChangesAsync(cancellationToken);

    return new OutboxMessage(
      envelope.MessageId,
      destination,
      record.MessageType,
      eventDataJson,
      metadata.RootElement.GetRawText(),
      scope?.RootElement.GetRawText(),
      now
    );
  }

  /// <summary>
  /// Gets pending messages that have not yet been published.
  /// Returns messages in FIFO order (oldest first).
  /// </summary>
  public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
      int batchSize,
      CancellationToken cancellationToken = default) {

    if (batchSize <= 0) {
      throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
    }

    var records = await _context.Set<OutboxRecord>()
      .Where(r => r.Status == "Pending")
      .OrderBy(r => r.CreatedAt)
      .ThenBy(r => r.Id)  // Secondary sort by ID for deterministic ordering
      .Take(batchSize)
      .ToListAsync(cancellationToken);

    return records.Select(r => new OutboxMessage(
      MessageId.Parse(r.MessageId),
      r.Destination,
      r.MessageType,
      r.MessageData.RootElement.GetRawText(),
      r.Metadata.RootElement.GetRawText(),
      r.Scope?.RootElement.GetRawText(),
      r.CreatedAt
    )).ToList();
  }

  /// <summary>
  /// Marks a message as successfully published.
  /// Updates status and sets PublishedAt timestamp.
  /// </summary>
  public async Task MarkPublishedAsync(
      MessageId messageId,
      CancellationToken cancellationToken = default) {

    var record = await _context.Set<OutboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId.ToString(), cancellationToken);

    if (record == null) {
      throw new InvalidOperationException($"Outbox message not found: {messageId}");
    }

    record.Status = "Published";
    record.PublishedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync(cancellationToken);
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
}
