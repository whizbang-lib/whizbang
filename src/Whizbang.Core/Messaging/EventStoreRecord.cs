using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Database entity for event store persistence using 3-column JSON pattern.
/// Stores events with universal metadata columns (correlation, causation, timestamps) in JSON.
/// Database-agnostic schema - ORM-specific configuration (e.g., JSONB for PostgreSQL) applied separately.
/// </summary>
public sealed class EventStoreRecord {
  /// <summary>
  /// UUID primary key for the event record.
  /// </summary>
  public Guid Id { get; set; }

  /// <summary>
  /// Stream identifier (aggregate ID as UUID).
  /// Indexed for fast stream retrieval.
  /// </summary>
  public required Guid StreamId { get; set; }

  /// <summary>
  /// Aggregate ID for backwards compatibility.
  /// Prefer using StreamId for new code.
  /// </summary>
  public required Guid AggregateId { get; set; }

  /// <summary>
  /// Aggregate type name for backwards compatibility.
  /// </summary>
  public required string AggregateType { get; set; }

  /// <summary>
  /// Sequence number within the stream (starts at 0, sequential).
  /// Combined with StreamId forms unique constraint for optimistic concurrency.
  /// </summary>
  public required long Sequence { get; set; }

  /// <summary>
  /// Version number within the stream for optimistic concurrency.
  /// </summary>
  public required int Version { get; set; }

  /// <summary>
  /// Fully-qualified event type name (e.g., "MyApp.Events.OrderCreated").
  /// Used for deserialization and type-based queries.
  /// </summary>
  public required string EventType { get; set; }

  /// <summary>
  /// Event payload stored as JSON.
  /// Contains the actual event data (e.g., { "OrderId": "123", "Total": 99.99 }).
  /// Serialized directly from MessageEnvelope.Payload.
  /// </summary>
  public required JsonDocument EventData { get; set; }

  /// <summary>
  /// Event metadata stored as JSON.
  /// Contains MessageId and complete Hops chain with all observability data.
  /// Serialized directly from MessageEnvelope using System.Text.Json (no DTO mapping).
  /// </summary>
  public required JsonDocument Metadata { get; set; }

  /// <summary>
  /// Scope information for multi-tenancy stored as JSON.
  /// Contains tenant/user/partition information for query filtering.
  /// Schema: { "TenantId": "...", "UserId": "...", "PartitionKey": "..." }
  /// </summary>
  public JsonDocument? Scope { get; set; }

  /// <summary>
  /// UTC timestamp when the event was persisted to the event store.
  /// Automatically set by database on insert.
  /// </summary>
  public DateTime CreatedAt { get; set; }
}
