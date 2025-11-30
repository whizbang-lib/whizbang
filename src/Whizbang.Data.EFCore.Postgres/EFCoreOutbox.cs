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
/// </summary>
public sealed class EFCoreOutbox<TDbContext> : IOutbox
  where TDbContext : DbContext {

  private readonly TDbContext _context;
  private static readonly JsonSerializerOptions _jsonOptions = EFCoreJsonContext.CreateCombinedOptions();

  public EFCoreOutbox(TDbContext context) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
  }

  /// <summary>
  /// Stores a message envelope in the outbox for later publication.
  /// Extracts envelope data into JSONB columns for efficient querying.
  /// </summary>
  public async Task StoreAsync<TMessage>(
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

    var record = new OutboxRecord {
      MessageId = envelope.MessageId.ToString(),
      Destination = destination,
      EventType = typeof(TMessage).FullName ?? throw new InvalidOperationException("Event type has no FullName"),
      EventData = eventData,
      Metadata = metadata,
      Scope = scope,
      Status = "Pending",
      Attempts = 0,
      Error = null,
      CreatedAt = DateTimeOffset.UtcNow,
      PublishedAt = null,
      Topic = destination,  // Legacy field, same as Destination
      PartitionKey = null   // Can be extracted from envelope if needed
    };

    await _context.Set<OutboxRecord>().AddAsync(record, cancellationToken);
    await _context.SaveChangesAsync(cancellationToken);
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
      r.EventType,
      r.EventData.RootElement.GetRawText(),
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
