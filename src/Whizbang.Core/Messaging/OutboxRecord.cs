using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Database entity for outbox (transactional messaging) using 3-column JSON pattern.
/// Ensures at-least-once message delivery by persisting messages before publishing.
/// Database-agnostic schema - ORM-specific configuration (e.g., JSONB for PostgreSQL) applied separately.
/// </summary>
/// <docs>messaging/outbox-pattern</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_TwoInstances_DistributesPartitionsViaModuloAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ThreeInstances_DistributesPartitionsViaModuloAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CrossInstanceStreamOrdering_PreventsClaimingWhenEarlierMessagesHeldAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StreamBasedFailureCascade_ReleasesLaterMessagesInSameStreamAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinationDbContext.cs:WorkCoordinationDbContext</tests>
/// <tests>tests/Whizbang.Generators.Tests/EFCorePerspectiveConfigurationGeneratorDiagnosticsTests.cs:Generator_CreatesConfigurationForPerspective_ContainsOutboxRecordMapping</tests>
public sealed class OutboxRecord {

  /// <summary>
  /// Unique message ID (idempotency key for downstream consumers).
  /// Indexed for fast lookups and deduplication.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  public required Guid MessageId { get; set; }

  /// <summary>
  /// The destination to publish to (topic, queue, etc.).
  /// Used by outbox processor to route messages.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  public required string Destination { get; set; }

  /// <summary>
  /// Fully-qualified message type name (e.g., "MyApp.Events.OrderCreated", "MyApp.Commands.CreateOrder").
  /// Used for routing and deserialization.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
  public required string MessageType { get; set; }

  /// <summary>
  /// Message payload stored as JSON.
  /// Contains the actual message data to be published.
  /// Schema matches the message type.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
  public required JsonDocument MessageData { get; set; }

  /// <summary>
  /// Message metadata stored as JSON.
  /// Contains correlation ID, causation ID, timestamp, security context, etc.
  /// Schema: { "CorrelationId": "guid", "CausationId": "guid", "Timestamp": "ISO8601", "UserId": "...", "TenantId": "..." }
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
  public required JsonDocument Metadata { get; set; }

  /// <summary>
  /// Scope information for multi-tenancy stored as JSON.
  /// Contains tenant/user/partition information for query filtering.
  /// Schema: { "TenantId": "...", "UserId": "...", "PartitionKey": "..." }
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  public JsonDocument? Scope { get; set; }


  /// <summary>
  /// Number of publishing attempts (starts at 0).
  /// Used for retry logic and poison message detection.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
  public int Attempts { get; set; }

  /// <summary>
  /// Error message if publishing failed.
  /// Null if publishing succeeded or not yet attempted.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync</tests>
  public string? Error { get; set; }

  /// <summary>
  /// UTC timestamp when the message was first persisted to outbox.
  /// Automatically set by database on insert.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CrossInstanceStreamOrdering_PreventsClaimingWhenEarlierMessagesHeldAsync</tests>
  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>
  /// UTC timestamp when the message was last published (attempt made).
  /// Null if not yet published.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:ProcessWorkBatchAsync_PublishSuccessful_MarksMessageAsPublished</tests>
  public DateTime? PublishedAt { get; set; }

  /// <summary>
  /// UTC timestamp when the message processing was fully completed.
  /// Used to track completion time separate from published_at.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  public DateTime? ProcessedAt { get; set; }

  /// <summary>
  /// Service instance ID currently processing this message.
  /// Used for multi-instance coordination and tracking which instance owns the lease.
  /// Null if message is not currently being processed.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_TwoInstances_DistributesPartitionsViaModuloAsync</tests>
  public Guid? InstanceId { get; set; }

  /// <summary>
  /// UTC timestamp when the processing lease expires.
  /// Used for orphaned work recovery - messages with expired leases can be claimed by other instances.
  /// Null if message is not currently being processed or has been completed/failed.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  public DateTimeOffset? LeaseExpiry { get; set; }


  // ========================================
  // Work Coordinator Pattern (Phase 1-7)
  // ========================================

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// Used for partition-based work distribution via consistent hashing.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CrossInstanceStreamOrdering_PreventsClaimingWhenEarlierMessagesHeldAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StreamBasedFailureCascade_ReleasesLaterMessagesInSameStreamAsync</tests>
  public Guid? StreamId { get; set; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// Range: 0-9999 (10,000 partitions by default).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_TwoInstances_DistributesPartitionsViaModuloAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ThreeInstances_DistributesPartitionsViaModuloAsync</tests>
  public int? PartitionNumber { get; set; }

  /// <summary>
  /// Current processing status flags (bitwise).
  /// Indicates which stages have been completed (e.g., Stored, EventStored, Published).
  /// Uses MessageProcessingStatus enum.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletionWithStatusZero_DoesNotChangeStatusFlagsAsync</tests>
  public MessageProcessingStatus StatusFlags { get; set; }

  /// <summary>
  /// Classified failure reason (enum value).
  /// Enables typed filtering and handling of different failure scenarios.
  /// Default value is Unknown (99).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FailureReasonSchemaTests.cs:OutboxTable_ShouldHaveFailureReasonColumnAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FailureReasonSchemaTests.cs:FailureReasonColumn_CanStoreAllEnumValuesAsync</tests>
  public MessageFailureReason FailureReason { get; set; } = MessageFailureReason.Unknown;

  /// <summary>
  /// UTC timestamp when message should be processed.
  /// Used for retry scheduling with exponential backoff and scheduled message delivery.
  /// Null means process immediately.
  /// </summary>
  public DateTimeOffset? ScheduledFor { get; set; }

}
