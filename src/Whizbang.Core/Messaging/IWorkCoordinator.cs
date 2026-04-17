using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Parameter object for ProcessWorkBatchAsync to reduce method complexity.
/// Groups related parameters for better maintainability and caller ergonomics.
/// </summary>
public sealed record ProcessWorkBatchRequest {
  /// <summary>
  /// Unique ID for the service instance (should be UUIDv7 for time-ordered IDs).
  /// Used for partition assignment and work claiming.
  /// </summary>
  public required Guid InstanceId { get; init; }

  /// <summary>
  /// Name of the service (e.g., "OrderService").
  /// Used for monitoring and instance identification.
  /// </summary>
  public required string ServiceName { get; init; }

  /// <summary>
  /// Host name where service is running (e.g., "web-server-01").
  /// Used for monitoring and debugging.
  /// </summary>
  public required string HostName { get; init; }

  /// <summary>
  /// Operating system process ID.
  /// Used for monitoring and debugging.
  /// </summary>
  public required int ProcessId { get; init; }

  /// <summary>
  /// Optional JSONB metadata dictionary to persist with the instance.
  /// Includes acknowledgement counts for completion tracking (outbox_completions_processed, inbox_completions_processed, etc.).
  /// Pass null for no metadata.
  /// </summary>
  public Dictionary<string, JsonElement>? Metadata { get; init; }

  /// <summary>
  /// Array of outbox message completions to report.
  /// Indicates which outbox messages were successfully published.
  /// Empty array if no completions.
  /// </summary>
  public required MessageCompletion[] OutboxCompletions { get; init; }

  /// <summary>
  /// Array of outbox message failures to report.
  /// Includes error details and partial completion tracking.
  /// Empty array if no failures.
  /// </summary>
  public required MessageFailure[] OutboxFailures { get; init; }

  /// <summary>
  /// Array of inbox message completions to report.
  /// Indicates which inbox messages were successfully processed.
  /// Empty array if no completions.
  /// </summary>
  public required MessageCompletion[] InboxCompletions { get; init; }

  /// <summary>
  /// Array of inbox message failures to report.
  /// Includes error details and partial completion tracking.
  /// Empty array if no failures.
  /// </summary>
  public required MessageFailure[] InboxFailures { get; init; }

  /// <summary>
  /// Array of receptor processing completions (event handler completions).
  /// Many receptors can process the same event.
  /// Empty array if no completions.
  /// </summary>
  public required ReceptorProcessingCompletion[] ReceptorCompletions { get; init; }

  /// <summary>
  /// Array of receptor processing failures (event handler failures).
  /// Includes error details for debugging.
  /// Empty array if no failures.
  /// </summary>
  public required ReceptorProcessingFailure[] ReceptorFailures { get; init; }

  /// <summary>
  /// Array of perspective cursor completions (read model projection cursors).
  /// Tracks last processed event per stream.
  /// Empty array if no completions.
  /// </summary>
  public required PerspectiveCursorCompletion[] PerspectiveCompletions { get; init; }

  /// <summary>
  /// Array of perspective event completions (work IDs to delete from wh_perspective_events).
  /// Used to clean up ephemeral event tracking rows after processing.
  /// Empty array if no completions.
  /// </summary>
  public required PerspectiveEventCompletion[] PerspectiveEventCompletions { get; init; }

  /// <summary>
  /// Array of perspective cursor failures (read model projection failures).
  /// Includes error details and last attempted event.
  /// Empty array if no failures.
  /// </summary>
  public required PerspectiveCursorFailure[] PerspectiveFailures { get; init; }

  /// <summary>
  /// Array of new outbox messages to store.
  /// These will be immediately returned as work in the same call (immediate processing pattern).
  /// Empty array if no new messages.
  /// </summary>
  public required OutboxMessage[] NewOutboxMessages { get; init; }

  /// <summary>
  /// Array of new inbox messages to store.
  /// Includes atomic deduplication (ON CONFLICT DO NOTHING) and optional event store integration.
  /// Empty array if no new messages.
  /// </summary>
  public required InboxMessage[] NewInboxMessages { get; init; }

  /// <summary>
  /// Array of outbox message IDs to renew leases for.
  /// Extends the lease expiry time to prevent orphan detection.
  /// Empty array if no renewals needed.
  /// </summary>
  public required Guid[] RenewOutboxLeaseIds { get; init; }

  /// <summary>
  /// Array of inbox message IDs to renew leases for.
  /// Extends the lease expiry time to prevent orphan detection.
  /// Empty array if no renewals needed.
  /// </summary>
  public required Guid[] RenewInboxLeaseIds { get; init; }

  /// <summary>
  /// Array of sync inquiries to check perspective event processing status.
  /// Results are returned in WorkBatch.SyncInquiryResults.
  /// Null if no sync inquiries.
  /// </summary>
  /// <docs>fundamentals/perspectives/sync</docs>
  public SyncInquiry[]? PerspectiveSyncInquiries { get; init; }

  /// <summary>
  /// Work batch flags for controlling behavior.
  /// Examples: SkipNewWork, ForceClaimAll.
  /// Defaults to None for normal operation.
  /// </summary>
  public WorkBatchOptions Flags { get; init; } = WorkBatchOptions.None;

  /// <summary>
  /// Total number of virtual partitions for consistent hashing (default: 10,000).
  /// Determines partition number range [0, PartitionCount-1].
  /// Higher values provide better distribution but increase computation.
  /// </summary>
  public int PartitionCount { get; init; } = 10_000;

  /// <summary>
  /// How long to hold work leases in seconds (default: 300 = 5 minutes).
  /// Messages with expired leases become orphaned and can be reclaimed by other instances.
  /// </summary>
  public int LeaseSeconds { get; init; } = 300;

  /// <summary>
  /// How long before an instance is considered stale in seconds (default: 600 = 10 minutes).
  /// Instances with expired heartbeats are cleaned up and their work redistributed.
  /// </summary>
  public int StaleThresholdSeconds { get; init; } = 30;

  /// <summary>
  /// Maximum number of streams to return per batch for perspective processing.
  /// Controls how many distinct streams the SQL returns per tick. Default: 300.
  /// </summary>
  public int MaxStreamsPerBatch { get; init; } = 300;
}

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
  /// <param name="request">Parameter object containing all work batch configuration and data</param>
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
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WorkBatchOptions_SetCorrectlyAsync</tests>
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
    ProcessWorkBatchRequest request,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Deregisters this instance on graceful shutdown.
  /// Releases all leases (outbox, inbox, perspective events, receptors, active streams),
  /// logs shutdown to wh_log, and removes the instance from wh_service_instances.
  /// Called by WhizbangShutdownService.StopAsync on SIGTERM.
  /// </summary>
  Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Gathers expensive statistics (COUNT queries) for observability gauges.
  /// Called periodically (~every 60 ticks), NOT on every tick. Single source of truth
  /// for queue depth metrics that are too expensive for the hot path.
  /// </summary>
  Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Stores inbox messages directly without running the full process_work_batch pipeline.
  /// This lightweight method ONLY inserts messages into wh_inbox with deduplication,
  /// bypassing completions, failures, claiming, and return query phases.
  /// Event storage and perspective creation happen on the next tick when the
  /// WorkCoordinatorPublisherWorker claims the messages (self-healing via Phase 5 → 4.5B).
  /// </summary>
  /// <param name="messages">Inbox messages to store</param>
  /// <param name="partitionCount">Number of partitions for load balancing</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <docs>operations/workers/transport-consumer</docs>
  Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount = 2,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reports perspective cursor completion or failure directly (out-of-band).
  /// This lightweight method ONLY updates the perspective cursor without affecting
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
  /// <docs>operations/workers/perspective-worker</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:InstantStrategy_ReportCompletionAsync_CallsCoordinatorImmediately_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStrategyTests.cs:PerspectiveWorker_WithInstantStrategy_ReportsImmediately_Async</tests>
  Task ReportPerspectiveCompletionAsync(
    PerspectiveCursorCompletion completion,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Reports perspective cursor failure directly (out-of-band).
  /// This lightweight method ONLY updates the perspective cursor without affecting
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
  /// <docs>operations/workers/perspective-worker</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:InstantStrategy_ReportFailureAsync_CallsCoordinatorImmediately_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStrategyTests.cs:PerspectiveWorker_OnFailure_UsesStrategyToReportFailure_Async</tests>
  Task ReportPerspectiveFailureAsync(
    PerspectiveCursorFailure failure,
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
  Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Batch-fetches perspective cursors for multiple streams in a single SQL call.
  /// Used by drain mode to prefetch all cursors before parallel processing starts,
  /// eliminating N individual GetPerspectiveCursorAsync calls during the hot loop.
  /// </summary>
  /// <param name="streamIds">Stream IDs to fetch cursors for</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of cursor info for all streams that have checkpoints</returns>
  /// <docs>fundamentals/perspectives/drain-mode#batch-cursor-fetch</docs>
  Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(
    Guid[] streamIds,
    CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());

  /// <summary>
  /// Records that PostLifecycle completed for an event.
  /// Used as a durable marker for crash recovery reconciliation.
  /// Idempotent — duplicate event IDs are silently ignored.
  /// </summary>
  /// <param name="eventId">The event ID that completed PostLifecycle.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
  Task RecordLifecycleCompletionAsync(
    Guid eventId,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Finds events where all perspective cursors are past the event but no lifecycle
  /// completion marker exists. These are events that need PostLifecycle replay after
  /// a process crash or stale-tracking cleanup race condition.
  /// </summary>
  /// <param name="perspectivesPerEventType">Registry map: event type key → expected perspective names.</param>
  /// <param name="lookbackWindow">How far back to scan for orphaned events.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Orphaned events with their envelopes for PostLifecycle replay.</returns>
  /// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
  Task<IReadOnlyList<OrphanedLifecycleEvent>> GetOrphanedLifecycleEventsAsync(
    Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType,
    TimeSpan lookbackWindow,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OrphanedLifecycleEvent>>([]);

  /// <summary>
  /// Deletes lifecycle completion markers older than the specified retention period.
  /// Called periodically to keep the table small.
  /// </summary>
  /// <param name="retentionPeriod">How long to keep completion markers.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of entries deleted.</returns>
  /// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
  Task<int> CleanupLifecycleCompletionsAsync(
    TimeSpan retentionPeriod,
    CancellationToken cancellationToken = default) => Task.FromResult(0);

  /// <summary>
  /// Gets all perspective cursors that have the RewindRequired flag set.
  /// Used by startup scan to identify streams needing rewind repair.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>List of cursors requiring rewind.</returns>
  /// <docs>fundamentals/perspectives/rewind#startup-scan</docs>
  Task<IReadOnlyList<RewindCursorInfo>> GetCursorsRequiringRewindAsync(
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RewindCursorInfo>>([]);

  /// <summary>
  /// Completes perspective events by deleting the specified work items from wh_perspective_events.
  /// Called per-stream immediately after processing (drain mode — no buffering).
  /// </summary>
  /// <param name="workItemIds">Array of event_work_id values to delete</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Number of rows deleted</returns>
  /// <docs>fundamentals/perspectives/drain-mode</docs>
  Task<int> CompletePerspectiveEventsAsync(
    Guid[] workItemIds,
    CancellationToken cancellationToken = default) => Task.FromResult(0);

  /// <summary>
  /// Batch-fetches events for multiple streams in a single call.
  /// Returns denormalized rows joining wh_perspective_events with wh_event_store.
  /// Only returns events leased to the requesting instance.
  /// C# determines which perspectives apply from EventType using its registry.
  /// </summary>
  /// <param name="instanceId">Instance ID to filter leased events</param>
  /// <param name="streamIds">Stream IDs to fetch events for</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of stream event data for processing</returns>
  /// <docs>fundamentals/perspectives/drain-mode</docs>
  Task<List<StreamEventData>> GetStreamEventsAsync(
    Guid instanceId,
    Guid[] streamIds,
    CancellationToken cancellationToken = default) => Task.FromResult(new List<StreamEventData>());

  /// <summary>
  /// Runs database maintenance tasks: purges completed messages, old deduplication entries,
  /// and stuck inbox messages. Called on startup and periodically by WorkCoordinatorPublisherWorker.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Results for each maintenance task with row counts and durations.</returns>
  /// <docs>operations/maintenance</docs>
  Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
}

/// <summary>
/// Result of a single maintenance task executed by <see cref="IWorkCoordinator.PerformMaintenanceAsync"/>.
/// </summary>
/// <param name="TaskName">Name of the maintenance task (e.g., "purge_completed_outbox").</param>
/// <param name="RowsAffected">Number of rows affected by the task.</param>
/// <param name="DurationMs">Duration of the task in milliseconds.</param>
/// <param name="Status">Status of the task (e.g., "ok").</param>
/// <docs>operations/maintenance</docs>
public sealed record MaintenanceResult(string TaskName, long RowsAffected, double DurationMs, string Status);

/// <summary>
/// <summary>
/// Information about a perspective cursor that requires rewind.
/// Returned by <see cref="IWorkCoordinator.GetCursorsRequiringRewindAsync"/>.
/// </summary>
/// <param name="StreamId">The stream requiring rewind.</param>
/// <param name="PerspectiveName">The perspective that needs rewind on this stream.</param>
/// <param name="LastEventId">Current cursor position.</param>
/// <param name="RewindTriggerEventId">The late-arriving event that triggered the rewind.</param>
/// <docs>fundamentals/perspectives/rewind</docs>
public record RewindCursorInfo(Guid StreamId, string PerspectiveName, Guid? LastEventId, Guid? RewindTriggerEventId);

/// <summary>
/// An event where all perspectives completed but PostLifecycle was never fired.
/// Returned by <see cref="IWorkCoordinator.GetOrphanedLifecycleEventsAsync"/> for replay.
/// </summary>
/// <param name="EventId">The event's unique identifier.</param>
/// <param name="StreamId">The stream the event belongs to.</param>
/// <param name="Envelope">The deserialized message envelope for receptor invocation.</param>
/// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
public sealed record OrphanedLifecycleEvent(Guid EventId, Guid StreamId, IMessageEnvelope Envelope);

/// <summary>
/// Information about a perspective cursor.
/// Used by PerspectiveWorker to determine where to start reading events.
/// </summary>
public record PerspectiveCursorInfo {
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

  /// <summary>
  /// The event ID that triggered a rewind (set when status has RewindRequired flag).
  /// NULL when no rewind is needed.
  /// </summary>
  public Guid? RewindTriggerEventId { get; init; }
}

/// <summary>
/// Result of ProcessWorkBatchAsync containing work that needs processing.
/// </summary>
/// <summary>
/// Statistics gathered periodically for observability gauges.
/// Contains expensive COUNT-based metrics that are too costly for every-tick measurement.
/// </summary>
public record WorkCoordinatorStatistics {
  /// <summary>Unprocessed perspective events awaiting projection.</summary>
  public long PendingPerspectiveEvents { get; init; }

  /// <summary>Unprocessed outbox messages awaiting publishing.</summary>
  public long PendingOutbox { get; init; }

  /// <summary>Unprocessed inbox messages awaiting handling.</summary>
  public long PendingInbox { get; init; }

  /// <summary>Active streams tracked in wh_active_streams.</summary>
  public long ActiveStreams { get; init; }
}

/// <summary>
/// Contains the results of a work batch poll including work items for this instance to process.
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

  /// <summary>
  /// Stream IDs that have leased perspective events for this instance.
  /// The worker determines which perspectives apply from event types using its C# registry.
  /// Replaces the per-event PerspectiveWork return for drain mode.
  /// </summary>
  public List<Guid> PerspectiveStreamIds { get; init; } = [];

  /// <summary>
  /// Results of sync inquiries from this batch call.
  /// Contains pending counts for each perspective/stream combination queried.
  /// Null if no sync inquiries were passed in the request.
  /// </summary>
  /// <docs>fundamentals/perspectives/sync</docs>
  public List<SyncInquiryResult>? SyncInquiryResults { get; init; }
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
  /// Null for local-only events that should be stored in event store but not transported.
  /// When null, event is persisted but transport publishing is skipped.
  /// </summary>
  public string? Destination { get; init; }

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
  /// Multi-tenancy and security scope extracted from the envelope.
  /// Stored in the dedicated scope JSONB column for query filtering.
  /// </summary>
  public PerspectiveScope? Scope { get; init; }

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
  /// Multi-tenancy and security scope extracted from the envelope.
  /// Stored in the dedicated scope JSONB column for query filtering.
  /// </summary>
  public PerspectiveScope? Scope { get; init; }

  /// <summary>
  /// Envelope metadata including MessageId, Hops, and DispatchContext.
  /// Stored in the inbox metadata JSONB column for query filtering and observability.
  /// </summary>
  public EnvelopeMetadata? Metadata { get; init; }

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
/// Shared constraint for work items that expose a MessageId and Status.
/// Used by <see cref="OrderedStreamProcessor"/> to generically process inbox and outbox messages.
/// </summary>
public interface IHasMessageIdAndStatus {
  /// <summary>
  /// Unique message ID.
  /// </summary>
  Guid MessageId { get; }

  /// <summary>
  /// Current processing status flags.
  /// </summary>
  MessageProcessingStatus Status { get; }
}

/// <summary>
/// Represents outbox work that needs to be published.
/// Includes both new pending messages and messages with expired leases (orphaned).
/// Envelope is IMessageEnvelope&lt;JsonElement&gt; for AOT-compatible, type-safe serialization.
/// </summary>
public record OutboxWork : IHasMessageIdAndStatus {
  /// <summary>
  /// Unique message ID.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Destination to publish to (topic name).
  /// Null for local-only events that were stored but should not be transported.
  /// Transport publishing should be skipped when destination is null.
  /// </summary>
  public string? Destination { get; init; }

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
  public WorkBatchOptions Flags { get; init; }

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
public record InboxWork : IHasMessageIdAndStatus {
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
  /// Number of previous processing attempts.
  /// Used for retry logic, poison message detection, and MaxInboxAttempts purge.
  /// </summary>
  public int Attempts { get; init; }

  /// <summary>
  /// Current processing status flags.
  /// Indicates which stages have been completed (e.g., Stored, EventStored).
  /// </summary>
  public MessageProcessingStatus Status { get; init; }

  /// <summary>
  /// Work batch flags indicating metadata about this work item.
  /// Examples: NewlyStored, Orphaned, FromEventStore, RetryAfterFailure.
  /// </summary>
  public WorkBatchOptions Flags { get; init; }

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
/// Represents a perspective cursor completion.
/// Indicates successful processing of an event by a perspective (read model projection).
/// </summary>
public record PerspectiveCursorCompletion {
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

  /// <summary>
  /// Number of events processed in this run.
  /// Used by rewind observability to populate PerspectiveRewindCompleted.EventsReplayed.
  /// </summary>
  /// <docs>fundamentals/perspectives/rewind#metrics</docs>
  public int EventsProcessed { get; init; }

  /// <summary>
  /// Event IDs actually processed by the runner in this batch.
  /// Used by complete_perspective_cursor_work to mark only these specific events
  /// as processed, preventing concurrent late-arriving events from being
  /// incorrectly marked as processed via range-based cursor advancement.
  /// </summary>
  public Guid[] ProcessedEventIds { get; init; } = [];
}

/// <summary>
/// Represents a perspective cursor failure.
/// Indicates failed processing of an event by a perspective (read model projection).
/// </summary>
public record PerspectiveCursorFailure {
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

  /// <summary>
  /// Event IDs actually processed by the runner before the failure occurred.
  /// Used by complete_perspective_cursor_work to mark only these specific events
  /// as processed, preventing concurrent late-arriving events from being
  /// incorrectly marked as processed via range-based cursor advancement.
  /// </summary>
  public Guid[] ProcessedEventIds { get; init; } = [];
}

/// <summary>
/// Represents perspective cursor work that needs to be processed.
/// Each item indicates a stream that has new events for a specific perspective to process.
/// </summary>
public record PerspectiveWork {
  /// <summary>
  /// Work ID from wh_perspective_events (event_work_id).
  /// Used to report completion and trigger deletion of the event row.
  /// </summary>
  public Guid WorkId { get; init; }

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
  public WorkBatchOptions Flags { get; init; }

  /// <summary>
  /// JSONB metadata from database.
  /// First row includes acknowledgement counts if no outbox/inbox work exists.
  /// Contains keys like perspective_completions_processed, perspective_failures_processed, etc.
  /// </summary>
  public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// Represents a single event fetched for stream processing via get_stream_events.
/// Denormalized row: one per (stream, event). C# groups by StreamId.
/// No perspective_name — C# determines applicable perspectives from EventType using registry.
/// </summary>
public record StreamEventData {
  /// <summary>Stream that this event belongs to.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>Event ID from wh_event_store (UUIDv7, naturally ordered).</summary>
  public required Guid EventId { get; init; }

  /// <summary>Event type (assembly-qualified name). Used to determine which perspectives apply.</summary>
  public required string EventType { get; init; }

  /// <summary>Serialized event data from wh_event_store.</summary>
  public required string EventData { get; init; }

  /// <summary>Serialized metadata JSONB from wh_event_store. Contains MessageId, Hops, DispatchContext.</summary>
  public string? Metadata { get; init; }

  /// <summary>Serialized scope JSONB from wh_event_store. Contains tenant context (TenantId, UserId, etc.).</summary>
  public string? Scope { get; init; }

  /// <summary>Work ID from wh_perspective_events. Used for completion reporting via CompletePerspectiveEventsAsync.</summary>
  public required Guid EventWorkId { get; init; }
}

/// <summary>
/// Represents a perspective event completion (used to delete processed wh_perspective_events rows).
/// Property names match the SQL function's expected JSONB format (EventWorkId, StatusFlags).
/// </summary>
public record PerspectiveEventCompletion {
  /// <summary>
  /// Work ID from wh_perspective_events (event_work_id).
  /// </summary>
  public required Guid EventWorkId { get; init; }

  /// <summary>
  /// Status flags to set on the event (e.g., Completed = 2).
  /// </summary>
  public int StatusFlags { get; init; } = (int)PerspectiveProcessingStatus.Completed;
}

/// <summary>
/// Groups the parameters for <see cref="WorkCoordinatorExtensions.ProcessWorkBatchAsync"/>
/// to avoid S107 (too many parameters). Maps directly to <see cref="ProcessWorkBatchRequest"/>.
/// </summary>
public readonly record struct ProcessWorkBatchContext(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  Dictionary<string, JsonElement>? Metadata,
  MessageCompletion[] OutboxCompletions,
  MessageFailure[] OutboxFailures,
  MessageCompletion[] InboxCompletions,
  MessageFailure[] InboxFailures,
  ReceptorProcessingCompletion[] ReceptorCompletions,
  ReceptorProcessingFailure[] ReceptorFailures,
  PerspectiveCursorCompletion[] PerspectiveCompletions,
  PerspectiveCursorFailure[] PerspectiveFailures,
  OutboxMessage[] NewOutboxMessages,
  InboxMessage[] NewInboxMessages,
  Guid[] RenewOutboxLeaseIds,
  Guid[] RenewInboxLeaseIds,
  WorkBatchOptions Flags = WorkBatchOptions.None,
  int PartitionCount = 10_000,
  int LeaseSeconds = 300,
  int StaleThresholdSeconds = 30);

/// <summary>
/// Extension methods for IWorkCoordinator providing backwards-compatible parameter styles.
/// </summary>
public static class WorkCoordinatorExtensions {
  /// <summary>
  /// Backwards-compatible overload using a context record.
  /// Converts to ProcessWorkBatchRequest internally.
  /// </summary>
  public static Task<WorkBatch> ProcessWorkBatchAsync(
    this IWorkCoordinator coordinator,
    ProcessWorkBatchContext context,
    CancellationToken cancellationToken = default
  ) {
    var request = new ProcessWorkBatchRequest {
      InstanceId = context.InstanceId,
      ServiceName = context.ServiceName,
      HostName = context.HostName,
      ProcessId = context.ProcessId,
      Metadata = context.Metadata,
      OutboxCompletions = context.OutboxCompletions,
      OutboxFailures = context.OutboxFailures,
      InboxCompletions = context.InboxCompletions,
      InboxFailures = context.InboxFailures,
      ReceptorCompletions = context.ReceptorCompletions,
      ReceptorFailures = context.ReceptorFailures,
      PerspectiveCompletions = context.PerspectiveCompletions,
      PerspectiveEventCompletions = [],
      PerspectiveFailures = context.PerspectiveFailures,
      NewOutboxMessages = context.NewOutboxMessages,
      NewInboxMessages = context.NewInboxMessages,
      RenewOutboxLeaseIds = context.RenewOutboxLeaseIds,
      RenewInboxLeaseIds = context.RenewInboxLeaseIds,
      Flags = context.Flags,
      PartitionCount = context.PartitionCount,
      LeaseSeconds = context.LeaseSeconds,
      StaleThresholdSeconds = context.StaleThresholdSeconds
    };
    return coordinator.ProcessWorkBatchAsync(request, cancellationToken);
  }
}
