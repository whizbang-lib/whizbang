using System.Text.Json;

namespace Whizbang.Data.EFCore.Postgres.Entities;

/// <summary>
/// EF Core entity for inbox (deduplication) using 3-column JSONB pattern.
/// Ensures exactly-once message processing by tracking processed messages.
/// Uses PostgreSQL JSONB columns for efficient querying and indexing.
/// </summary>
public sealed class InboxRecord {
  /// <summary>
  /// Unique message ID (idempotency key).
  /// Primary key for fast deduplication checks.
  /// </summary>
  public required Guid MessageId { get; set; }

  /// <summary>
  /// The name of the handler that will process this message.
  /// Used to route inbox messages to the correct handler.
  /// </summary>
  public required string HandlerName { get; set; }

  /// <summary>
  /// Fully-qualified message type name (e.g., "MyApp.Events.OrderCreated", "MyApp.Commands.CreateOrder").
  /// Used for debugging and monitoring.
  /// </summary>
  public required string MessageType { get; set; }

  /// <summary>
  /// Message payload stored as JSONB.
  /// Contains the actual message data for debugging/replay.
  /// Schema matches the message type.
  /// </summary>
  public required JsonDocument MessageData { get; set; }

  /// <summary>
  /// Message metadata stored as JSONB.
  /// Contains correlation ID, causation ID, timestamp, security context, etc.
  /// Schema: { "CorrelationId": "guid", "CausationId": "guid", "Timestamp": "ISO8601", "UserId": "...", "TenantId": "..." }
  /// </summary>
  public required JsonDocument Metadata { get; set; }

  /// <summary>
  /// Scope information for multi-tenancy stored as JSONB.
  /// Contains tenant/user/partition information for query filtering.
  /// Schema: { "TenantId": "...", "UserId": "...", "PartitionKey": "..." }
  /// </summary>
  public JsonDocument? Scope { get; set; }


  /// <summary>
  /// Number of processing attempts (starts at 0).
  /// Used for retry logic and poison message detection.
  /// </summary>
  public int Attempts { get; set; }

  /// <summary>
  /// Error message if processing failed.
  /// Null if processing succeeded or not yet attempted.
  /// </summary>
  public string? Error { get; set; }

  /// <summary>
  /// UTC timestamp when the message was first received.
  /// Automatically set by database on insert.
  /// </summary>
  public DateTimeOffset ReceivedAt { get; set; }

  /// <summary>
  /// UTC timestamp when the message was last processed (attempt made).
  /// Null if not yet processed.
  /// </summary>
  public DateTime? ProcessedAt { get; set; }

  /// <summary>
  /// Service instance ID currently processing this message.
  /// Used for multi-instance coordination and tracking which instance owns the lease.
  /// Null if message is not currently being processed.
  /// </summary>
  public Guid? InstanceId { get; set; }

  /// <summary>
  /// UTC timestamp when the processing lease expires.
  /// Used for orphaned work recovery - messages with expired leases can be claimed by other instances.
  /// Null if message is not currently being processed or has been completed/failed.
  /// </summary>
  public DateTimeOffset? LeaseExpiry { get; set; }

  // ========================================
  // Work Coordinator Pattern (Phase 1-7)
  // ========================================

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// Used for partition-based work distribution via consistent hashing.
  /// </summary>
  public Guid? StreamId { get; set; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// Range: 0-9999 (10,000 partitions by default).
  /// </summary>
  public int? PartitionNumber { get; set; }

  /// <summary>
  /// Current processing status flags (bitwise).
  /// Indicates which stages have been completed (e.g., Stored, ReceptorProcessed, PerspectiveProcessed).
  /// Uses MessageProcessingStatus enum.
  /// </summary>
  public int StatusFlags { get; set; }

}
