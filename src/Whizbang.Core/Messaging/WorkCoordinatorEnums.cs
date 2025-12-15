namespace Whizbang.Core.Messaging;

/// <summary>
/// Flags indicating metadata about work items returned from ProcessWorkBatchAsync.
/// Multiple flags can be combined using bitwise OR.
/// </summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnsCorrectFlags_NewlyStoredAndOrphanedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs</tests>
[Flags]
public enum WorkBatchFlags {
  /// <summary>
  /// No special flags.
  /// </summary>
  /// <tests>No tests found</tests>
  None = 0,

  /// <summary>
  /// This work item was just stored in this call (new message).
  /// Indicates immediate processing path vs. orphaned work recovery.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnsCorrectFlags_NewlyStoredAndOrphanedAsync</tests>
  NewlyStored = 1 << 0,

  /// <summary>
  /// This work item was claimed from another failed instance (orphaned work).
  /// Indicates lease expired and work was reassigned.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnsCorrectFlags_NewlyStoredAndOrphanedAsync</tests>
  Orphaned = 1 << 1,

  /// <summary>
  /// Debug mode enabled - completed messages will be kept instead of deleted.
  /// Useful for development and troubleshooting.
  /// </summary>
  /// <tests>No tests found</tests>
  DebugMode = 1 << 2,

  /// <summary>
  /// This message was also written to the event store.
  /// Only applicable for events (implements IEvent).
  /// </summary>
  /// <tests>No tests found</tests>
  FromEventStore = 1 << 3,

  /// <summary>
  /// High priority message - should be processed before normal messages.
  /// Future enhancement for priority queuing.
  /// </summary>
  /// <tests>No tests found</tests>
  HighPriority = 1 << 4,

  /// <summary>
  /// This message is being retried after a previous failure.
  /// Indicates retry path vs. first-time processing.
  /// </summary>
  /// <tests>No tests found</tests>
  RetryAfterFailure = 1 << 5

  // Bits 6-31 reserved for future use
}

/// <summary>
/// Flags tracking which stages of message processing have been completed.
/// Multiple flags can be combined using bitwise OR to track partial completion.
/// This enables retrying only failed stages instead of the entire pipeline.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_DeletesMessagesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_DeletesMessagesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DiagnosticProcessWorkBatchTest.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/OrderedStreamProcessorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportPublishStrategyTests.cs</tests>
[Flags]
public enum MessageProcessingStatus {
  /// <summary>
  /// No processing stages completed.
  /// </summary>
  /// <tests>No tests found</tests>
  None = 0,

  /// <summary>
  /// Message has been persisted to inbox/outbox table.
  /// First stage of any message processing.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_DeletesMessagesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_DeletesMessagesAsync</tests>
  Stored = 1 << 0,

  /// <summary>
  /// Event has been written to the event store (events only).
  /// Occurs during outbox storage (before publishing) or inbox storage (on receipt).
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_DeletesMessagesAsync</tests>
  EventStored = 1 << 1,

  /// <summary>
  /// Message has been successfully published to transport (outbox only).
  /// Indicates message left this service via Service Bus/transport.
  /// For outbox messages, this means the message is fully completed and can be deleted.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_DeletesMessagesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_DeletesMessagesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:ProcessWorkBatchAsync_MarkAsPublished_SetsStatusFlagAsync</tests>
  Published = 1 << 2,

  // Bits 3-14 reserved for future pipeline stages

  /// <summary>
  /// Message processing failed at some stage.
  /// Combined with other flags to indicate which stages succeeded before failure.
  /// For example: (Stored | EventStored | Failed) means storage succeeded but publishing failed.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync</tests>
  Failed = 1 << 15

  // Note: Receptor and perspective processing are now tracked separately in
  // wh_receptor_processing and wh_perspective_checkpoints tables.
  // This allows for:
  // - Multiple receptors to process the same event independently
  // - Perspectives to catch up via time-travel (replay from event store)
  // - Better visibility into which handlers have processed which events
}
