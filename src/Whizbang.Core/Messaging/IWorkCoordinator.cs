using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Coordinates work processing across multiple service instances using virtual partition assignment with consistent hashing.
/// Provides atomic operations for heartbeat updates, message completion tracking,
/// event store integration, and orphaned work recovery.
/// Uses hash-based distribution on UUIDv7 identifiers - no partition assignments table required.
/// </summary>
/// <docs>messaging/work-coordination</docs>
/// <remarks>
/// Virtual Partition Architecture:
/// - Partition numbers computed via: abs(hashtext(stream_id::TEXT)) % partition_count
/// - Instance ownership calculated via: hashtext(stream_id::TEXT) % active_instance_count = hashtext(instance_id::TEXT) % active_instance_count
/// - Self-contained: depends only on UUID properties, not database state
/// - Automatic rebalancing when instances join/leave
/// - Strong stream ordering guarantees via NOT EXISTS clauses
/// </remarks>
public interface IWorkCoordinator {
  /// <summary>
  /// Processes a batch of work in a single atomic operation:
  /// - Registers/updates instance with heartbeat
  /// - Cleans up stale instances (expired heartbeats)
  /// - Stores new outbox messages (immediate processing)
  /// - Stores new inbox messages (deduplication + event store)
  /// - Reports completions with granular status tracking (outbox, inbox, receptors, perspectives)
  /// - Reports failures with partial completion tracking (outbox, inbox, receptors, perspectives)
  /// - Claims work using hash-based virtual partition assignment and returns work for this instance
  ///
  /// Event store integration:
  /// - Receptors: Process individual events (many receptors can process the same event)
  /// - Perspectives: Checkpoint-based processing per stream (read model projections)
  ///
  /// This minimizes database round-trips and ensures consistency.
  /// </summary>
  /// <param name="instanceId">Service instance ID</param>
  /// <param name="serviceName">Service name (e.g., 'InventoryWorker')</param>
  /// <param name="hostName">Host machine name</param>
  /// <param name="processId">Operating system process ID</param>
  /// <param name="metadata">Optional instance metadata (e.g., version, environment). Supports any JSON value type via JsonElement.</param>
  /// <param name="outboxCompletions">Outbox message completions with granular status</param>
  /// <param name="outboxFailures">Outbox message failures with partial completion tracking</param>
  /// <param name="inboxCompletions">Inbox message completions with granular status</param>
  /// <param name="inboxFailures">Inbox message failures with partial completion tracking</param>
  /// <param name="receptorCompletions">Receptor processing completions (event processing by receptors)</param>
  /// <param name="receptorFailures">Receptor processing failures (failed event processing by receptors)</param>
  /// <param name="perspectiveCompletions">Perspective checkpoint completions (perspective catch-up progress)</param>
  /// <param name="perspectiveFailures">Perspective checkpoint failures (failed perspective updates)</param>
  /// <param name="newOutboxMessages">Outbox messages to store (for immediate processing)</param>
  /// <param name="newInboxMessages">Inbox messages to store (with deduplication)</param>
  /// <param name="renewOutboxLeaseIds">Message IDs to renew lease for (outbox) - for buffered messages awaiting transport</param>
  /// <param name="renewInboxLeaseIds">Message IDs to renew lease for (inbox) - for buffered messages awaiting processing</param>
  /// <param name="flags">Work batch flags (e.g., DebugMode to preserve completed messages)</param>
  /// <param name="partitionCount">Total number of partitions (default 10,000)</param>
  /// <param name="leaseSeconds">Lease duration in seconds (default 300 = 5 minutes)</param>
  /// <param name="staleThresholdSeconds">Stale instance threshold in seconds (default 600 = 10 minutes)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Work batch containing messages that need processing (including newly stored messages for immediate processing)</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithMetadata_StoresMetadataCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_TwoInstances_DistributesPartitionsViaModuloAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ThreeInstances_DistributesPartitionsViaModuloAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CrossInstanceStreamOrdering_PreventsClaimingWhenEarlierMessagesHeldAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletionWithStatusZero_DoesNotChangeStatusFlagsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StreamBasedFailureCascade_ReleasesLaterMessagesInSameStreamAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ClearedLeaseMessages_BecomeAvailableForOtherInstancesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_UnitOfWorkPattern_ProcessesCompletionsAndFailuresInSameCallAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_StoresAndReturnsImmediatelyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_StoresWithDeduplicationAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithStreamId_AssignsPartitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithStreamId_AssignsPartitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithEventOutbox_PersistsToEventStoreAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithEventInbox_PersistsToEventStoreAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_EventVersionConflict_HandlesOptimisticConcurrencyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MultipleEventsInStream_IncrementsVersionAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NonEvent_DoesNotPersistToEventStoreAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ConsistentHashing_SameStreamSamePartitionAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_PartitionAssignment_WithinRangeAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_LoadBalancing_DistributesAcrossInstancesAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_InstanceFailover_RedistributesPartitionsAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StatusFlags_AccumulateCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_PartialCompletion_TracksCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WorkBatchFlags_SetCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StaleInstances_CleanedUpAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ActiveInstances_NotCleanedAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorMessageProcessingTests.cs:MessagesStoredInOutbox_AreReturnedImmediately_InSameCallAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorMessageProcessingTests.cs:MessagesWithExpiredLease_AreReclaimed_InSubsequentCallAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorMessageProcessingTests.cs:MessagesWithValidLease_SameInstance_AreNotReturnedAgainAsync</tests>
  Task<WorkBatch> ProcessWorkBatchAsync(
    Guid instanceId,
    string serviceName,
    string hostName,
    int processId,
    Dictionary<string, JsonElement>? metadata,
    MessageCompletion[] outboxCompletions,
    MessageFailure[] outboxFailures,
    MessageCompletion[] inboxCompletions,
    MessageFailure[] inboxFailures,
    ReceptorProcessingCompletion[] receptorCompletions,
    ReceptorProcessingFailure[] receptorFailures,
    PerspectiveCheckpointCompletion[] perspectiveCompletions,
    PerspectiveCheckpointFailure[] perspectiveFailures,
    OutboxMessage[] newOutboxMessages,
    InboxMessage[] newInboxMessages,
    Guid[] renewOutboxLeaseIds,
    Guid[] renewInboxLeaseIds,
    WorkBatchFlags flags = WorkBatchFlags.None,
    int partitionCount = 10_000,
    int leaseSeconds = 300,
    int staleThresholdSeconds = 600,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Reports perspective checkpoint completion or failure directly (out-of-band).
  /// This lightweight method ONLY updates the perspective checkpoint without affecting
  /// heartbeats, work claiming, or other coordination operations.
  /// </summary>
  /// <param name="completion">Perspective checkpoint completion to report</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task representing the async operation</returns>
  /// <remarks>
  /// Use this method for instant perspective reporting strategies where completions
  /// should be persisted immediately without waiting for the next work batch cycle.
  /// This calls the complete_perspective_checkpoint_work SQL function directly.
  /// </remarks>
  /// <docs>workers/perspective-worker</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:InstantStrategy_ReportCompletionAsync_CallsCoordinatorImmediately_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStrategyTests.cs:PerspectiveWorker_WithInstantStrategy_ReportsImmediately_Async</tests>
  Task ReportPerspectiveCompletionAsync(
    PerspectiveCheckpointCompletion completion,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reports perspective checkpoint failure directly (out-of-band).
  /// This lightweight method ONLY updates the perspective checkpoint without affecting
  /// heartbeats, work claiming, or other coordination operations.
  /// </summary>
  /// <param name="failure">Perspective checkpoint failure to report</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task representing the async operation</returns>
  /// <remarks>
  /// Use this method for instant perspective reporting strategies where failures
  /// should be persisted immediately without waiting for the next work batch cycle.
  /// This calls the complete_perspective_checkpoint_work SQL function directly.
  /// </remarks>
  /// <docs>workers/perspective-worker</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:InstantStrategy_ReportFailureAsync_CallsCoordinatorImmediately_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStrategyTests.cs:PerspectiveWorker_OnFailure_UsesStrategyToReportFailure_Async</tests>
  Task ReportPerspectiveFailureAsync(
    PerspectiveCheckpointFailure failure,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets the current checkpoint for a perspective stream.
  /// Returns the last processed event ID for the perspective, or null if no checkpoint exists.
  /// </summary>
  /// <param name="streamId">Stream ID to query</param>
  /// <param name="perspectiveName">Perspective name to query</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Perspective checkpoint info, or null if no checkpoint exists</returns>
  /// <remarks>
  /// Used by PerspectiveWorker to determine where to start reading events when processing
  /// grouped work items for a stream/perspective pair.
  /// </remarks>
  Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a perspective checkpoint.
/// Used by PerspectiveWorker to determine where to start reading events.
/// </summary>
public record PerspectiveCheckpointInfo {
  /// <summary>
  /// Stream ID for the checkpoint.
  /// </summary>
  public required Guid StreamId { get; init; }

  /// <summary>
  /// Name of the perspective.
  /// </summary>
  public required string PerspectiveName { get; init; }

  /// <summary>
  /// Last event ID that was successfully processed.
  /// NULL if perspective has never processed this stream.
  /// </summary>
  public Guid? LastEventId { get; init; }

  /// <summary>
  /// Current processing status.
  /// </summary>
  public PerspectiveProcessingStatus Status { get; init; }
}

/// <summary>
/// Result of ProcessWorkBatchAsync containing work that needs processing.
/// </summary>
public record WorkBatch {
  /// <summary>
  /// Outbox work to publish (includes both new pending messages and orphaned messages with expired leases).
  /// </summary>
  public required List<OutboxWork> OutboxWork { get; init; }

  /// <summary>
  /// Inbox work to process (includes both new pending messages and orphaned messages with expired leases).
  /// From the application's perspective, these are the next messages to handle.
  /// </summary>
  public required List<InboxWork> InboxWork { get; init; }

  /// <summary>
  /// Perspective checkpoints to process (catch-up processing for perspectives).
  /// Each item represents a stream that needs perspective updates.
  /// </summary>
  public required List<PerspectiveWork> PerspectiveWork { get; init; }
}

/// <summary>
/// Represents an outbox message to be stored in process_work_batch.
/// Used for immediate processing pattern (store + immediately return for publishing).
/// Envelope is IMessageEnvelope&lt;JsonElement&gt; for AOT-compatible, type-safe serialization.
/// </summary>
public record OutboxMessage {
  /// <summary>
  /// Unique message ID (should be UUIDv7 for time-ordered, database-friendly IDs).
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Destination to publish to (topic name).
  /// </summary>
  public required string Destination { get; init; }

  /// <summary>
  /// Complete MessageEnvelope object (including payload as JsonElement, hops, metadata).
  /// JsonElement provides AOT-compatible serialization without runtime type resolution.
  /// </summary>
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }

  /// <summary>
  /// Envelope metadata extracted for storage in separate metadata column.
  /// Contains MessageId and Hops for observability and tracing.
  /// </summary>
  public required EnvelopeMetadata Metadata { get; init; }

  /// <summary>
  /// Assembly-qualified name of the envelope type (e.g., "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core").
  /// Required for proper deserialization of the envelope from the database.
  /// </summary>
  public required string EnvelopeType { get; init; }

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Whether this message is an event (implements IEvent).
  /// If true and stream_id is not null, it will be persisted to the event store.
  /// </summary>
  public bool IsEvent { get; init; }

  /// <summary>
  /// Assembly-qualified name of the message payload type (e.g., "MyApp.Commands.CreateProductCommand, MyApp").
  /// Used for deserialization and stored in the event_type database column.
  /// </summary>
  public required string MessageType { get; init; }
}

/// <summary>
/// Represents an inbox message to be stored in process_work_batch.
/// Includes atomic deduplication (ON CONFLICT DO NOTHING) and optional event store integration.
/// Envelope is IMessageEnvelope&lt;JsonElement&gt; for AOT-compatible, type-safe serialization.
/// </summary>
public record InboxMessage {
  /// <summary>
  /// Unique message ID (should be UUIDv7 for time-ordered, database-friendly IDs).
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Handler name (e.g., "ServiceBusConsumer").
  /// </summary>
  public required string HandlerName { get; init; }

  /// <summary>
  /// Complete MessageEnvelope object (including payload as JsonElement, hops, metadata).
  /// JsonElement provides AOT-compatible serialization without runtime type resolution.
  /// </summary>
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }

  /// <summary>
  /// Assembly-qualified name of the envelope type (e.g., "Whizbang.Core.Observability.MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core").
  /// Required for proper deserialization of the envelope from the database.
  /// </summary>
  public required string EnvelopeType { get; init; }

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Whether this message is an event (implements IEvent).
  /// If true and stream_id is not null, it will be persisted to the event store.
  /// </summary>
  public bool IsEvent { get; init; }

  /// <summary>
  /// Assembly-qualified name of the message payload type (e.g., "MyApp.Events.ProductCreatedEvent, MyApp").
  /// Used for deserialization and stored in the event_type database column.
  /// </summary>
  public required string MessageType { get; init; }
}

/// <summary>
/// Represents a message completion with granular status tracking.
/// Indicates which processing stages completed successfully.
/// </summary>
public record MessageCompletion {
  /// <summary>
  /// Message ID that completed.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Which stages of processing completed successfully.
  /// Use bitwise OR to combine multiple stages (e.g., Stored | EventStored).
  /// </summary>
  public required MessageProcessingStatus Status { get; init; }
}

/// <summary>
/// Represents a message failure with partial completion tracking.
/// Indicates which stages succeeded before the failure occurred.
/// </summary>
public record MessageFailure {
  /// <summary>
  /// Message ID that failed.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithReason_StoresReasonAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithoutReason_DefaultsToUnknownAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_AllReasonTypes_CanBeAssignedAsync</tests>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Which stages of processing completed successfully before failure.
  /// For example: (Stored | EventStored) indicates storage succeeded but next stage failed.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithReason_StoresReasonAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithoutReason_DefaultsToUnknownAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_AllReasonTypes_CanBeAssignedAsync</tests>
  public required MessageProcessingStatus CompletedStatus { get; init; }

  /// <summary>
  /// Error message or exception details.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithReason_StoresReasonAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithoutReason_DefaultsToUnknownAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_AllReasonTypes_CanBeAssignedAsync</tests>
  public required string Error { get; init; }

  /// <summary>
  /// Classified reason for the failure.
  /// Enables typed filtering and handling of different failure scenarios.
  /// Defaults to Unknown if not specified.
  /// </summary>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithReason_StoresReasonAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_WithoutReason_DefaultsToUnknownAsync</tests>
  /// <tests>Whizbang.Core.Tests/Messaging/MessageFailureTests.cs:MessageFailure_AllReasonTypes_CanBeAssignedAsync</tests>
  public MessageFailureReason Reason { get; init; } = MessageFailureReason.Unknown;
}

/// <summary>
/// Legacy: Represents a failed message with error information.
/// Deprecated - use MessageFailure instead for better status tracking.
/// </summary>
[Obsolete("Use MessageFailure instead for granular status tracking")]
public record FailedMessage {
  /// <summary>
  /// Message ID that failed.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Error message or exception details.
  /// </summary>
  public required string Error { get; init; }
}

/// <summary>
/// Represents outbox work that needs to be published.
/// Includes both new pending messages and messages with expired leases (orphaned).
/// Envelope is IMessageEnvelope&lt;JsonElement&gt; for AOT-compatible, type-safe serialization.
/// </summary>
public record OutboxWork {
  /// <summary>
  /// Unique message ID.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Destination to publish to (topic name).
  /// </summary>
  public required string Destination { get; init; }

  /// <summary>
  /// Complete MessageEnvelope object with JsonElement payload.
  /// Deserialized from database - ready to publish.
  /// JsonElement provides AOT-compatible serialization without runtime type resolution.
  /// </summary>
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }

  /// <summary>
  /// Assembly-qualified name of the envelope type (e.g., "Whizbang.Core.MessageEnvelope`1[[MyApp.CreateProductCommand, MyApp]], Whizbang.Core").
  /// Required for proper deserialization when publishing to transports.
  /// Stored in database but Envelope.GetType() at runtime returns MessageEnvelope&lt;JsonElement&gt; which loses the original payload type.
  /// </summary>
  public required string EnvelopeType { get; init; }

  /// <summary>
  /// Assembly-qualified name of the message payload type (e.g., "MyApp.Commands.CreateProductCommand, MyApp").
  /// Used for deserialization and stored in the event_type database column.
  /// </summary>
  public required string MessageType { get; init; }

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// </summary>
  public int? PartitionNumber { get; init; }

  /// <summary>
  /// Number of previous publishing attempts.
  /// </summary>
  public required int Attempts { get; init; }

  /// <summary>
  /// Current processing status flags.
  /// Indicates which stages have been completed (e.g., Stored, EventStored, Published).
  /// </summary>
  public MessageProcessingStatus Status { get; init; }

  /// <summary>
  /// Work batch flags indicating metadata about this work item.
  /// Examples: NewlyStored, Orphaned, FromEventStore, RetryAfterFailure.
  /// </summary>
  public WorkBatchFlags Flags { get; init; }

  /// <summary>
  /// Sequence order for maintaining ordering within a stream.
  /// Epoch milliseconds from created_at timestamp.
  /// </summary>
  public long SequenceOrder { get; init; }

  /// <summary>
  /// JSONB metadata from database.
  /// First row includes acknowledgement counts for completion tracking.
  /// Contains keys like outbox_completions_processed, outbox_failures_processed, etc.
  /// </summary>
  public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// Represents inbox work that needs to be processed.
/// Includes both new pending messages and messages with expired leases (orphaned).
/// From the application's perspective, these are the next messages to handle.
/// Envelope is IMessageEnvelope&lt;JsonElement&gt; for AOT-compatible, type-safe serialization.
/// </summary>
public record InboxWork {
  /// <summary>
  /// Unique message ID.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Complete MessageEnvelope object with JsonElement payload.
  /// Deserialized from database - ready to process.
  /// JsonElement provides AOT-compatible serialization without runtime type resolution.
  /// </summary>
  public required IMessageEnvelope<JsonElement> Envelope { get; init; }

  /// <summary>
  /// Assembly-qualified name of the message payload type (e.g., "MyApp.Events.ProductCreatedEvent, MyApp").
  /// Used for deserializing the JsonElement payload back to the actual event type.
  /// </summary>
  public required string MessageType { get; init; }

  /// <summary>
  /// Stream ID for ordering (aggregate ID or message ID).
  /// Events from the same stream must be processed in order.
  /// </summary>
  public Guid? StreamId { get; init; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// </summary>
  public int? PartitionNumber { get; init; }

  /// <summary>
  /// Current processing status flags.
  /// Indicates which stages have been completed (e.g., Stored, EventStored).
  /// </summary>
  public MessageProcessingStatus Status { get; init; }

  /// <summary>
  /// Work batch flags indicating metadata about this work item.
  /// Examples: NewlyStored, Orphaned, FromEventStore, RetryAfterFailure.
  /// </summary>
  public WorkBatchFlags Flags { get; init; }

  /// <summary>
  /// Sequence order for maintaining ordering within a stream.
  /// Epoch milliseconds from received_at timestamp.
  /// </summary>
  public long SequenceOrder { get; init; }

  /// <summary>
  /// JSONB metadata from database.
  /// First row includes acknowledgement counts if no outbox work exists.
  /// Contains keys like inbox_completions_processed, inbox_failures_processed, etc.
  /// </summary>
  public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// Represents a receptor processing completion.
/// Indicates successful processing of an event by a receptor (event handler).
/// </summary>
public record ReceptorProcessingCompletion {
  /// <summary>
  /// Event ID that was processed.
  /// </summary>
  public required Guid EventId { get; init; }

  /// <summary>
  /// Name of the receptor that processed the event.
  /// </summary>
  public required string ReceptorName { get; init; }

  /// <summary>
  /// Processing status (e.g., Completed).
  /// </summary>
  public required ReceptorProcessingStatus Status { get; init; }
}

/// <summary>
/// Represents a receptor processing failure.
/// Indicates failed processing of an event by a receptor (event handler).
/// </summary>
public record ReceptorProcessingFailure {
  /// <summary>
  /// Event ID that failed to process.
  /// </summary>
  public required Guid EventId { get; init; }

  /// <summary>
  /// Name of the receptor that failed to process the event.
  /// </summary>
  public required string ReceptorName { get; init; }

  /// <summary>
  /// Processing status (e.g., Failed).
  /// </summary>
  public required ReceptorProcessingStatus Status { get; init; }

  /// <summary>
  /// Error message or exception details.
  /// </summary>
  public required string Error { get; init; }
}

/// <summary>
/// Represents a perspective checkpoint completion.
/// Indicates successful processing of an event by a perspective (read model projection).
/// </summary>
public record PerspectiveCheckpointCompletion {
  /// <summary>
  /// Stream ID being processed.
  /// </summary>
  public required Guid StreamId { get; init; }

  /// <summary>
  /// Name of the perspective that processed the event.
  /// </summary>
  public required string PerspectiveName { get; init; }

  /// <summary>
  /// Type of the perspective that processed the event.
  /// Provides the actual <see cref="Type"/> of the perspective class for precise identification.
  /// Null in unit tests or when type information is unavailable.
  /// Runtime-only property - not serialized to database. Use PerspectiveName for database queries.
  /// </summary>
  [JsonIgnore]
  public Type? PerspectiveType { get; init; }

  /// <summary>
  /// Last event ID processed (checkpoint position).
  /// UUIDv7 - naturally ordered by time, doubles as sequence number.
  /// </summary>
  public required Guid LastEventId { get; init; }

  /// <summary>
  /// Processing status (e.g., Completed, CatchingUp).
  /// </summary>
  public required PerspectiveProcessingStatus Status { get; init; }
}

/// <summary>
/// Represents a perspective checkpoint failure.
/// Indicates failed processing of an event by a perspective (read model projection).
/// </summary>
public record PerspectiveCheckpointFailure {
  /// <summary>
  /// Stream ID being processed.
  /// </summary>
  public required Guid StreamId { get; init; }

  /// <summary>
  /// Name of the perspective that failed to process the event.
  /// </summary>
  public required string PerspectiveName { get; init; }

  /// <summary>
  /// Last event ID attempted (checkpoint position at failure).
  /// UUIDv7 - naturally ordered by time, doubles as sequence number.
  /// </summary>
  public required Guid LastEventId { get; init; }

  /// <summary>
  /// Processing status (e.g., Failed).
  /// </summary>
  public required PerspectiveProcessingStatus Status { get; init; }

  /// <summary>
  /// Error message or exception details.
  /// </summary>
  public required string Error { get; init; }
}

/// <summary>
/// Represents perspective checkpoint work that needs to be processed.
/// Each item indicates a stream that has new events for a specific perspective to process.
/// </summary>
public record PerspectiveWork {
  /// <summary>
  /// Stream ID to process.
  /// </summary>
  public required Guid StreamId { get; init; }

  /// <summary>
  /// Name of the perspective that needs to process events from this stream.
  /// </summary>
  public required string PerspectiveName { get; init; }

  /// <summary>
  /// Last event ID that was successfully processed by this perspective for this stream.
  /// NULL if perspective has never processed this stream (starting from beginning).
  /// UUIDv7 - naturally ordered by time, doubles as sequence number.
  /// </summary>
  public Guid? LastProcessedEventId { get; init; }

  /// <summary>
  /// Current processing status for this checkpoint.
  /// </summary>
  public PerspectiveProcessingStatus Status { get; init; }

  /// <summary>
  /// Partition number (computed from stream_id via consistent hashing).
  /// Used for load distribution and ensuring same stream goes to same instance.
  /// </summary>
  public int? PartitionNumber { get; init; }

  /// <summary>
  /// Work batch flags indicating metadata about this work item.
  /// Examples: NewCheckpoint (first time processing stream), CatchingUp, Orphaned.
  /// </summary>
  public WorkBatchFlags Flags { get; init; }

  /// <summary>
  /// JSONB metadata from database.
  /// First row includes acknowledgement counts if no outbox/inbox work exists.
  /// Contains keys like perspective_completions_processed, perspective_failures_processed, etc.
  /// </summary>
  public Dictionary<string, JsonElement>? Metadata { get; init; }
}
