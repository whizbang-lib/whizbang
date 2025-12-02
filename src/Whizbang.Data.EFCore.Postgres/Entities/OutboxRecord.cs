using System.Text.Json;

namespace Whizbang.Data.EFCore.Postgres.Entities;

/// <summary>
/// EF Core entity for outbox (transactional messaging) using 3-column JSONB pattern.
/// Ensures at-least-once message delivery by persisting messages before publishing.
/// Uses PostgreSQL JSONB columns for efficient querying and indexing.
/// </summary>
public sealed class OutboxRecord {
  /// <summary>
  /// Auto-incrementing primary key for database ordering and batch processing.
  /// </summary>
  public long Id { get; set; }

  /// <summary>
  /// Unique message ID (idempotency key for downstream consumers).
  /// Indexed for fast lookups and deduplication.
  /// </summary>
  public required string MessageId { get; set; }

  /// <summary>
  /// The destination to publish to (topic, queue, etc.).
  /// Used by outbox processor to route messages.
  /// </summary>
  public required string Destination { get; set; }

  /// <summary>
  /// Fully-qualified message type name (e.g., "MyApp.Events.OrderCreated", "MyApp.Commands.CreateOrder").
  /// Used for routing and deserialization.
  /// </summary>
  public required string MessageType { get; set; }

  /// <summary>
  /// Message payload stored as JSONB.
  /// Contains the actual message data to be published.
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
  /// Publishing status: "Pending", "Publishing", "Published", "Failed".
  /// Indexed for efficient status queries and batch selection.
  /// </summary>
  public required string Status { get; set; }

  /// <summary>
  /// Number of publishing attempts (starts at 0).
  /// Used for retry logic and poison message detection.
  /// </summary>
  public int Attempts { get; set; }

  /// <summary>
  /// Error message if publishing failed.
  /// Null if publishing succeeded or not yet attempted.
  /// </summary>
  public string? Error { get; set; }

  /// <summary>
  /// UTC timestamp when the message was first persisted to outbox.
  /// Automatically set by database on insert.
  /// </summary>
  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>
  /// UTC timestamp when the message was last published (attempt made).
  /// Null if not yet published.
  /// </summary>
  public DateTime? PublishedAt { get; set; }

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

  /// <summary>
  /// Topic/queue name for routing (e.g., "orders", "inventory").
  /// Used by outbox processor to route messages to correct destination.
  /// </summary>
  public string? Topic { get; set; }

  /// <summary>
  /// Partition key for ordered processing within a partition.
  /// Null for messages that don't require ordered processing.
  /// </summary>
  public string? PartitionKey { get; set; }
}
