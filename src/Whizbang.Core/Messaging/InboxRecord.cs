using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Database entity for inbox (deduplication) using 3-column JSON pattern.
/// Ensures exactly-once message processing by tracking processed messages.
/// Database-agnostic schema - ORM-specific configuration (e.g., JSONB for PostgreSQL) applied separately.
/// </summary>
/// <docs>messaging/inbox-pattern</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetInboxStatusFlagsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetInboxInstanceIdAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
public sealed class InboxRecord {
  /// <summary>
  /// Unique message ID (idempotency key).
  /// Primary key for fast deduplication checks.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetInboxStatusFlagsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetInboxInstanceIdAsync</tests>
  public required Guid MessageId { get; set; }

  /// <summary>
  /// The name of the handler that will process this message.
  /// Used to route inbox messages to the correct handler.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public required string HandlerName { get; set; }

  /// <summary>
  /// Fully-qualified message type name (e.g., "MyApp.Events.OrderCreated", "MyApp.Commands.CreateOrder").
  /// Used for debugging and monitoring.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public required string MessageType { get; set; }

  /// <summary>
  /// Message payload stored as JSON.
  /// Contains the actual message data for debugging/replay.
  /// Schema matches the message type.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public required JsonDocument MessageData { get; set; }

  /// <summary>
  /// Message metadata stored as JSON.
  /// Contains correlation ID, causation ID, timestamp, security context, etc.
  /// Schema: { "CorrelationId": "guid", "CausationId": "guid", "Timestamp": "ISO8601", "UserId": "...", "TenantId": "..." }
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public required JsonDocument Metadata { get; set; }

  /// <summary>
  /// Scope information for multi-tenancy stored as JSON.
  /// Contains tenant/user/partition information for query filtering.
  /// Schema: { "TenantId": "...", "UserId": "...", "PartitionKey": "..." }
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public JsonDocument? Scope { get; set; }


  /// <summary>
  /// Number of processing attempts (starts at 0).
  /// Used for retry logic and poison message detection.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public int Attempts { get; set; }

  /// <summary>
  /// Error message if processing failed.
  /// Null if processing succeeded or not yet attempted.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public string? Error { get; set; }

  /// <summary>
  /// UTC timestamp when the message was first received.
  /// Automatically set by database on insert.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public DateTimeOffset ReceivedAt { get; set; }

  /// <summary>
  /// UTC timestamp when the message was last processed (attempt made).
  /// Null if not yet processed.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public DateTime? ProcessedAt { get; set; }

  /// <summary>
  /// Service instance ID currently processing this message.
  /// Used for multi-instance coordination and tracking which instance owns the lease.
  /// Null if message is not currently being processed.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetInboxInstanceIdAsync</tests>
  public Guid? InstanceId { get; set; }

  /// <summary>
  /// UTC timestamp when the processing lease expires.
  /// Used for orphaned work recovery - messages with expired leases can be claimed by other instances.
  /// Null if message is not currently being processed or has been completed/failed.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public DateTimeOffset? LeaseExpiry { get; set; }

  // ========================================
  // Work Coordinator Pattern (Phase 1-7)
  // ========================================

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// Used for partition-based work distribution via consistent hashing.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public Guid? StreamId { get; set; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// Range: 0-9999 (10,000 partitions by default).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  public int? PartitionNumber { get; set; }

  /// <summary>
  /// Current processing status flags (bitwise).
  /// Indicates which stages have been completed (e.g., Stored, EventStored).
  /// Uses MessageProcessingStatus enum.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:InsertInboxMessageAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetInboxStatusFlagsAsync</tests>
  public MessageProcessingStatus StatusFlags { get; set; }

  /// <summary>
  /// Classified failure reason (enum value).
  /// Enables typed filtering and handling of different failure scenarios.
  /// Default value is Unknown (99).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FailureReasonSchemaTests.cs:InboxTable_ShouldHaveFailureReasonColumnAsync</tests>
  public MessageFailureReason FailureReason { get; set; } = MessageFailureReason.Unknown;

  /// <summary>
  /// UTC timestamp when message should be processed.
  /// Used for retry scheduling with exponential backoff and scheduled message delivery.
  /// Null means process immediately.
  /// </summary>
  public DateTimeOffset? ScheduledFor { get; set; }

}
