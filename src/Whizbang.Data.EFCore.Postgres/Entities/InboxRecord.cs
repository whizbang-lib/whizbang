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
  public required string MessageId { get; set; }

  /// <summary>
  /// The name of the handler that will process this message.
  /// Used to route inbox messages to the correct handler.
  /// </summary>
  public required string HandlerName { get; set; }

  /// <summary>
  /// Fully-qualified event type name (e.g., "MyApp.Events.OrderCreated").
  /// Used for debugging and monitoring.
  /// </summary>
  public required string EventType { get; set; }

  /// <summary>
  /// Event payload stored as JSONB.
  /// Contains the actual event data for debugging/replay.
  /// Schema matches the event type.
  /// </summary>
  public required JsonDocument EventData { get; set; }

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
  /// Processing status: "Pending", "Processing", "Completed", "Failed".
  /// Indexed for efficient status queries.
  /// </summary>
  public required string Status { get; set; }

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
}
