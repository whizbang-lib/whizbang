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
/// EF Core implementation of IInbox using PostgreSQL with JSONB columns.
/// Provides exactly-once message processing through deduplication.
/// Stores incoming messages with their full envelope metadata for processing.
/// </summary>
public sealed class EFCoreInbox<TDbContext> : IInbox
  where TDbContext : DbContext {

  private readonly TDbContext _context;
  private readonly JsonSerializerOptions _jsonOptions;

  public EFCoreInbox(TDbContext context, JsonSerializerOptions? jsonOptions = null) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
    _jsonOptions = jsonOptions ?? EFCoreJsonContext.CreateCombinedOptions();
  }

  /// <summary>
  /// Stores an incoming message envelope in the inbox for later processing.
  /// Extracts envelope data into JSONB columns for efficient querying.
  /// </summary>
  public async Task StoreAsync<TMessage>(
      MessageEnvelope<TMessage> envelope,
      string handlerName,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);
    if (string.IsNullOrWhiteSpace(handlerName)) {
      throw new ArgumentException("Handler name cannot be null or whitespace.", nameof(handlerName));
    }

    // Serialize envelope data to JSON using JsonTypeInfo for AOT compatibility
    var typeInfo = (JsonTypeInfo<TMessage>)_jsonOptions.GetTypeInfo(typeof(TMessage));
    var eventDataJson = JsonSerializer.Serialize(envelope.Payload, typeInfo);
    var eventData = JsonDocument.Parse(eventDataJson);
    var metadata = SerializeMetadata(envelope);
    var scope = SerializeScope(envelope);

    var record = new InboxRecord {
      MessageId = envelope.MessageId.ToString(),
      HandlerName = handlerName,
      EventType = typeof(TMessage).FullName ?? "Unknown",
      EventData = eventData,
      Metadata = metadata,
      Scope = scope,
      Status = "Pending",
      Attempts = 0,
      Error = null,
      ReceivedAt = DateTimeOffset.UtcNow,
      ProcessedAt = null
    };

    await _context.Set<InboxRecord>().AddAsync(record, cancellationToken);
    await _context.SaveChangesAsync(cancellationToken);
  }

  /// <summary>
  /// Gets pending messages that have not yet been processed.
  /// Returns messages in FIFO order (oldest first).
  /// </summary>
  public async Task<IReadOnlyList<InboxMessage>> GetPendingAsync(
      int batchSize,
      CancellationToken cancellationToken = default) {

    if (batchSize <= 0) {
      throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
    }

    var records = await _context.Set<InboxRecord>()
      .Where(r => r.Status == "Pending")
      .OrderBy(r => r.ReceivedAt)
      .Take(batchSize)
      .ToListAsync(cancellationToken);

    return records.Select(r => new InboxMessage(
      MessageId.Parse(r.MessageId),
      r.HandlerName,
      r.EventType,
      r.EventData.RootElement.GetRawText(),
      r.Metadata.RootElement.GetRawText(),
      r.Scope?.RootElement.GetRawText(),
      r.ReceivedAt
    )).ToList();
  }

  /// <summary>
  /// Marks a message as successfully processed.
  /// Updates status and sets ProcessedAt timestamp.
  /// </summary>
  public async Task MarkProcessedAsync(
      MessageId messageId,
      CancellationToken cancellationToken = default) {

    var record = await _context.Set<InboxRecord>()
      .FirstOrDefaultAsync(r => r.MessageId == messageId.ToString(), cancellationToken);

    if (record == null) {
      throw new InvalidOperationException($"Inbox message not found: {messageId}");
    }

    record.Status = "Completed";
    record.ProcessedAt = DateTime.UtcNow;

    await _context.SaveChangesAsync(cancellationToken);
  }

  /// <summary>
  /// Checks if a message has already been processed (for deduplication).
  /// Returns true if the message exists in the inbox (regardless of status).
  /// </summary>
  public async Task<bool> HasProcessedAsync(
      MessageId messageId,
      CancellationToken cancellationToken = default) {

    return await _context.Set<InboxRecord>()
      .AnyAsync(r => r.MessageId == messageId.ToString(), cancellationToken);
  }

  /// <summary>
  /// Cleans up old processed message records to prevent unbounded growth.
  /// Deletes messages that were processed longer ago than the retention period.
  /// </summary>
  public async Task CleanupExpiredAsync(
      TimeSpan retention,
      CancellationToken cancellationToken = default) {

    var cutoff = DateTimeOffset.UtcNow - retention;

    await _context.Set<InboxRecord>()
      .Where(r => r.Status == "Completed" && r.ProcessedAt < cutoff)
      .ExecuteDeleteAsync(cancellationToken);
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
