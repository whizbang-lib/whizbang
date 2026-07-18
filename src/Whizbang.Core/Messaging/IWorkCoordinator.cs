using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Per-table count of rows whose <c>partition_number</c> was recomputed by
/// <see cref="IWorkCoordinator.RecomputePartitionNumbersAsync"/>.
/// </summary>
public sealed record PartitionRecomputeResult {
  /// <summary>Rows in <c>wh_inbox</c> whose <c>partition_number</c> was updated.</summary>
  public long InboxRowsRecomputed { get; init; }
  /// <summary>Rows in <c>wh_outbox</c> whose <c>partition_number</c> was updated.</summary>
  public long OutboxRowsRecomputed { get; init; }
  /// <summary>Rows in <c>wh_active_streams</c> whose <c>partition_number</c> was updated.</summary>
  public long ActiveStreamsRowsRecomputed { get; init; }

  /// <summary>True if any row in any table was recomputed (i.e. the database was previously inconsistent with the supplied PartitionCount).</summary>
  public bool AnyRecomputed => InboxRowsRecomputed > 0 || OutboxRowsRecomputed > 0 || ActiveStreamsRowsRecomputed > 0;
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
  /// Deregisters this instance on graceful shutdown.
  /// Releases all leases (outbox, inbox, perspective events, receptors, active streams),
  /// logs shutdown to wh_log, and removes the instance from wh_service_instances.
  /// Called by WhizbangShutdownService.StopAsync on SIGTERM.
  /// </summary>
  Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Records a heartbeat for this instance. Fired on its own cadence by the C# HeartbeatWorker can fire on its own cadence (5 s default) independent of polling.
  /// Sub-millisecond UPSERT against <c>wh_service_instances</c>. Default impl throws so existing
  /// non-Postgres backends (test fakes, in-memory) only opt in when ready.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="request">Instance identity + optional metadata.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/work-coordinator/configuration-reference</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreRecordHeartbeatTests.cs:RecordHeartbeatAsync_NewInstance_InsertsRowAsync</tests>
  Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement RecordHeartbeatAsync. Override in your IWorkCoordinator implementation.");

  /// <summary>
  /// Wakes the appropriate per-instance NOTIFY channels for any wh_outbox or wh_inbox rows
  /// whose <c>scheduled_for</c> retry timestamp has elapsed. Called periodically by
  /// <c>ScheduledRetryWorker</c> on a low-cadence (default 10 s) cycle so retry latency stays
  /// bounded without the 250 ms ClaimWorker baseline tax. Default impl throws — Postgres
  /// implementations override; in-memory fakes opt in when ready.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Total number of distinct streams woken across outbox + inbox. Zero when no
  /// scheduled retries are due.</returns>
  /// <docs>fundamentals/work-coordinator/scheduled-retries</docs>
  Task<int> NotifyScheduledRetryDueAsync(CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement NotifyScheduledRetryDueAsync. Override in your IWorkCoordinator implementation.");

  /// <summary>
  /// Marks the supplied outbox messages as processed (transport publish succeeded).
  /// Coalesced flush from the C# OutboxCompletionFlushWorker. Idempotent: unknown ids ignored.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="ids">Outbox message ids to mark as processed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows actually updated.</returns>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task<int> CompleteOutboxPublishedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    => CompleteOutboxPublishedAsync(ids, debugMode: false, cancellationToken);

  /// <summary>
  /// Marks outbox rows complete after successful transport publish.
  /// Production (<paramref name="debugMode"/>=false): DELETEs the rows so claim_work cannot re-issue them.
  /// Debug (<paramref name="debugMode"/>=true): retains rows with <c>published_at</c> + <c>processed_at</c>
  /// stamped — eligible_outbox filters <c>published_at IS NULL</c> so claim_work treats them as deleted.
  /// </summary>
  /// <param name="ids">Outbox message ids whose transport publish succeeded.</param>
  /// <param name="debugMode">From <see cref="IWorkCoordinatorStrategy.DebugMode"/> at the call site.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows actually affected.</returns>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task<int> CompleteOutboxPublishedAsync(
    IReadOnlyList<Guid> ids,
    bool debugMode,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement CompleteOutboxPublishedAsync.");

  /// <summary>
  /// Advances perspective cursors and deletes processed perspective_event rows in one round-trip.
  /// Coalesced flush from the C# PerspectiveCompletionFlushWorker.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="cursors">Cursor advancement specs (StreamId + PerspectiveName per entry).</param>
  /// <param name="eventWorkIds">wh_perspective_events.event_work_id rows to mark processed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task CompletePerspectiveAsync(
    IReadOnlyList<PerspectiveCursorCompletion> cursors,
    IReadOnlyList<Guid> eventWorkIds,
    CancellationToken cancellationToken = default)
    => CompletePerspectiveAsync(cursors, eventWorkIds, debugMode: false, cancellationToken);

  /// <summary>
  /// Same as the simpler <see cref="CompletePerspectiveAsync(IReadOnlyList{PerspectiveCursorCompletion}, IReadOnlyList{Guid}, CancellationToken)"/>
  /// but propagates <paramref name="debugMode"/> to SQL. Production: DELETEs perspective_event rows.
  /// Debug: retains rows with <c>processed_at</c> stamped.
  /// </summary>
  /// <param name="cursors">Cursor advancement specs.</param>
  /// <param name="eventWorkIds">Event-work ids to mark processed.</param>
  /// <param name="debugMode">From <see cref="IWorkCoordinatorStrategy.DebugMode"/> at the call site.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task CompletePerspectiveAsync(
    IReadOnlyList<PerspectiveCursorCompletion> cursors,
    IReadOnlyList<Guid> eventWorkIds,
    bool debugMode,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement CompletePerspectiveAsync.");

  /// <summary>
  /// Extends <c>lease_expiry</c> for the supplied ids in the chosen category.
  /// Called by C# LeaseRenewalWorker when in-flight items approach expiry.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="category">Which category table to update.</param>
  /// <param name="ids">Message / work ids to renew.</param>
  /// <param name="leaseSeconds">New lease duration from now (default 300).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows actually updated.</returns>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task<int> RenewLeasesAsync(
    WorkCategory category,
    IReadOnlyList<Guid> ids,
    int leaseSeconds = 300,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement RenewLeasesAsync.");

  /// <summary>
  /// Reports failures for the supplied category. Increments retry counters and sets
  /// error/failure_reason on the affected rows. Coalesced flush from C# FailureFlushWorker.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="category">Which category these failures belong to.</param>
  /// <param name="failures">Failure records (MessageId/EventWorkId + CompletedStatus + Error + FailureReason).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task ReportFailuresAsync(
    WorkCategory category,
    IReadOnlyList<MessageFailure> failures,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement ReportFailuresAsync.");

  /// <summary>
  /// Atomic transactional bundle for one handler's commit. Marks the inbox completion
  /// AND stores any new outbox/inbox messages emitted by the handler in one transaction.
  /// If any step fails the whole bundle rolls back. Emits pg_notify per category that
  /// received new rows. Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="request">The handler's complete result bundle.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/work-coordinator/handler-commit</docs>
  Task CommitHandlerResultAsync(
    HandlerCommitRequest request,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement CommitHandlerResultAsync.");

  /// <summary>
  /// SAVEPOINT-per-handler batched commit. The throughput multiplier: N handler results
  /// in one round-trip, single fsync at outer commit, with per-handler success/failure
  /// isolation. A failing handler rolls back only its own effects; siblings unaffected.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="requests">Handler bundles to commit.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Per-handler success/failure status, one row per request.</returns>
  /// <docs>fundamentals/work-coordinator/handler-commit</docs>
  Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
    IReadOnlyList<HandlerCommitRequest> requests,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement CommitHandlerBatchAsync.");

  /// <summary>
  /// Polls for work to claim. The only function the new ClaimWorker polls.
  /// Empty-call short-circuit drops the legacy ~17 ms idle floor toward ≤ 1 ms.
  /// Returns claimed outbox/inbox/perspective work; the C# layer distributes to channels.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="request">Claim parameters.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>WorkBatch with claimed work; empty if none available.</returns>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  Task<WorkBatch> ClaimWorkAsync(
    ClaimWorkRequest request,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement ClaimWorkAsync.");

  /// <summary>
  /// Composite single-round-trip flusher. Combines outbox completes, perspective completes,
  /// and per-category failures into one call. Single fsync at outer commit.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="request">Composite flush payload.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  Task FlushCompletionsAsync(
    FlushCompletionsRequest request,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement FlushCompletionsAsync.");

  /// <summary>
  /// PerspectiveSyncAwaiter read-only path. Returns pending vs processed event counts
  /// per (stream, perspective) inquiry.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="inquiries">Inquiry list.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/perspectives/sync</docs>
  Task<IReadOnlyList<SyncInquiryResult>> ResolveSyncInquiriesAsync(
    IReadOnlyList<Perspectives.Sync.SyncInquiry> inquiries,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement ResolveSyncInquiriesAsync.");

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
  /// <param name="partitionCount">Number of partitions for load balancing. Must match the PartitionCount used by the publisher worker
  /// in this service — otherwise wh_inbox.partition_number and wh_active_streams.partition_number
  /// disagree for the same stream and claim_orphaned_inbox deadlocks.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <docs>operations/workers/transport-consumer</docs>
  Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Stores new outbox messages directly. Lightweight alternative to <c>process_work_batch</c>
  /// that calls <c>store_outbox_messages</c> SQL function. Used by Dispatcher's
  /// <see cref="IWorkCoordinatorStrategy"/> implementations to insert queued outbox messages
  /// during flush. Default impl throws so non-Postgres backends opt in when ready.
  /// </summary>
  /// <param name="messages">Outbox messages to store.</param>
  /// <param name="partitionCount">Number of partitions for load balancing. Must match the
  /// service's configured <see cref="ClaimWorkerOptions.PartitionCount"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task StoreOutboxMessagesAsync(
    OutboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default)
    // Default no-op for test fakes. Production coordinators (EFCoreWorkCoordinator,
    // DapperWorkCoordinator) override this with the real INSERT. Tests that don't exercise
    // the store path silently no-op; tests that DO exercise it use a fake that overrides.
    => Task.CompletedTask;

  /// <summary>
  /// Evicts streams from <c>wh_active_streams</c> when their pending-work tables are empty,
  /// so the next event for an evicted stream rebinds via the
  /// <c>store_outbox_messages</c> / <c>store_inbox_messages</c> UPSERT path. Called from
  /// completion-flush workers after a batch lands.
  /// </summary>
  /// <remarks>
  /// Default no-op so non-Postgres backends and test fakes don't need to override.
  /// </remarks>
  /// <param name="streamIds">Stream IDs that just had work completed; candidates for eviction.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Count of streams evicted.</returns>
  Task<int> CleanupCompletedStreamsAsync(
    IReadOnlyList<Guid> streamIds,
    CancellationToken cancellationToken = default)
    => Task.FromResult(0);

  /// <summary>
  /// Recomputes <c>partition_number</c> on <c>wh_inbox</c>, <c>wh_outbox</c>, and
  /// <c>wh_active_streams</c> using the supplied <paramref name="partitionCount"/>,
  /// fixing any rows whose stored value disagrees with
  /// <c>compute_partition(stream_id, partition_count)</c>. Idempotent.
  /// </summary>
  /// <remarks>
  /// Called by <c>WorkCoordinatorPublisherWorker</c> on startup so a service that comes
  /// up under a new (or first-time-correct) <c>PartitionCount</c> immediately self-heals
  /// rows wedged by a partition-mismatch (the dev BFF incident of 2026-04-20). Returns
  /// the number of rows recomputed per table, which the worker logs at WARN if any are
  /// non-zero.
  /// </remarks>
  /// <param name="partitionCount">PartitionCount the service is currently configured to use</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// Has a default no-op implementation so test fakes/in-memory coordinators that don't
  /// own a real database don't need to override it. Production Postgres implementations
  /// override with the real <c>recompute_partition_numbers</c> SQL call.
  /// </remarks>
  Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
    int partitionCount,
    CancellationToken cancellationToken = default)
    => Task.FromResult(new PartitionRecomputeResult());

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
  /// Reclassifies a formerly-Sourced event type to Ephemeral across its stored history: stamps
  /// <c>EventFlags.Ephemeral</c> on the historical rows and offloads their inline bodies to
  /// <c>wh_event_body</c>, so the tier-1 reaper then reaps them consumption-gated. Pass the type's FULL
  /// name set (current CLR type name + former names) so a renamed type's history is matched under every
  /// name it was stored as. Streams that would become mixed (the type plus a Sourced event of another
  /// type) are skipped and reported, preserving the homogeneous-stream invariant. Deliberate,
  /// developer-invoked — never run implicitly. No-op on engines without the ephemeral body offload.
  /// </summary>
  /// <param name="eventTypeNames">The logical type's name set (current + former).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Counts of events reclassified, streams reclassified, and streams skipped as mixed.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<EphemeralReclassificationResult> ReclassifyEventsEphemeralAsync(
    IReadOnlyList<string> eventTypeNames,
    CancellationToken cancellationToken = default) => Task.FromResult(EphemeralReclassificationResult.Empty);

  /// <summary>
  /// Counts stored Sourced (not-yet-ephemeral) events for the given type name set. Used by the startup
  /// reconciler to DETECT historical drift — a type made <c>[Ephemeral]</c> that still has pre-existing
  /// Sourced events the reaper cannot see. Read-only; returns 0 on engines without the offload.
  /// </summary>
  /// <param name="eventTypeNames">The logical type's name set (current + former).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The number of stored Sourced events across those names.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<long> CountSourcedEventsForTypesAsync(
    IReadOnlyList<string> eventTypeNames,
    CancellationToken cancellationToken = default) => Task.FromResult(0L);

  /// <summary>
  /// Loads every stored type-definition fingerprint (a pre-register snapshot the reconciler diffs the
  /// code's current definitions against). Empty on engines without the fingerprint tables.
  /// </summary>
  /// <docs>fundamentals/events/type-definition-fingerprint</docs>
  Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TypeDefinitionInfo>>([]);

  /// <summary>
  /// Registers a type's current definition (content hashes) — idempotent by hash. Reports whether it was
  /// newly inserted plus the type's previous definition id, so the reconciler can record a lineage edge.
  /// No-op sentinel on engines without the fingerprint tables.
  /// </summary>
  /// <param name="eventTypeName">The type's (current) CLR name.</param>
  /// <param name="settingsHashHex">Lowercase-hex settings hash from the catalog entry.</param>
  /// <param name="schemaHashHex">Lowercase-hex schema hash from the catalog entry.</param>
  /// <param name="schemaVersion">Developer-declared schema version (0 until [SchemaVersion] exists).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/events/type-definition-fingerprint</docs>
  Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
    string eventTypeName,
    string settingsHashHex,
    string schemaHashHex,
    int schemaVersion,
    CancellationToken cancellationToken = default) => Task.FromResult(TypeDefinitionRegistration.None);

  /// <summary>
  /// Records a lineage edge between two definitions (how one superseded another + the migration that
  /// bridges them). No-op on engines without the fingerprint tables.
  /// </summary>
  /// <docs>fundamentals/events/type-definition-fingerprint</docs>
  Task RecordDefinitionLineageAsync(
    int fromDefinitionId,
    int toDefinitionId,
    DefinitionRelationship relationship,
    string? migrationRef,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Returns the subset of <paramref name="streamIds"/> that are EPHEMERAL — streams holding at least one
  /// event with <c>EventFlags.Ephemeral</c>. The rebuild/rewind guards use this to refuse ephemeral
  /// streams: their events self-destruct and their bodies are reaped, so an ephemeral stream is not a
  /// rebuildable source of truth and replaying it would corrupt the projection. Empty on engines without
  /// the ephemeral bit.
  /// </summary>
  /// <param name="streamIds">Candidate stream ids to classify.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The ids among <paramref name="streamIds"/> that are ephemeral.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<IReadOnlyCollection<Guid>> GetEphemeralStreamIdsAsync(
    IReadOnlyList<Guid> streamIds,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<Guid>>([]);

  /// <summary>
  /// Replaces the per-type rewind-grace overrides (from <c>[Ephemeral(RewindGraceSeconds = …)]</c>): upserts
  /// the declared set and prunes any override no longer declared. Called once at startup by the reconciler.
  /// The reaper resolves <c>COALESCE(type grace, global default)</c> per event. No-op on engines without the
  /// grace table.
  /// </summary>
  /// <param name="graceOverrides">Types that declare a per-type grace (seconds); empty clears all overrides.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task SyncEphemeralTypeGraceAsync(
    IReadOnlyList<EphemeralTypeGrace> graceOverrides,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Finds the <c>(stream, perspective)</c> pairs the maintenance cycle must snapshot BEFORE it reaps: an
  /// ephemeral body that is consumed and aged past its grace window, whose consuming perspective has NO
  /// snapshot at/past the event's <c>commit_sequence</c>. Snapshotting these (via the runner's bootstrap
  /// hook) is what makes the reaper's coverage gate safe on low-volume / idle streams that never hit an
  /// event-count snapshot threshold. Empty on engines without the ephemeral body offload.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Distinct pairs (with the perspective cursor's last event id to snapshot at).</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<EphemeralSnapshotTarget>>([]);

  /// <summary>
  /// Tier-2 deep maintenance (E1 #13b3): prunes ANCIENT ephemeral event-store pointers whose bodies the
  /// tier-1 reaper already deleted — keeping the NEWEST pointer per stream so the ephemeral rebuild guard
  /// and the perspective cursor's last-event target survive the prune. The backing implementation is
  /// OPT-IN (disabled by default) and self-gated to a long interval, so calling this every maintenance
  /// cycle is cheap; it only actually prunes when enabled AND due. Default: unsupported no-op (engines
  /// without the ephemeral body offload have nothing to prune).
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Rows pruned plus a status string (<c>disabled</c> | <c>not due</c> | <c>ok</c> | …).</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<EphemeralPointerPruneResult> PruneAncientEphemeralPointersAsync(
    CancellationToken cancellationToken = default) =>
    Task.FromResult(new EphemeralPointerPruneResult(0, "unsupported"));

  /// <summary>
  /// A1 (Archival &amp; Compaction) — "close the books" on a durable Sourced stream: truncate the detail at or
  /// below <paramref name="throughVersion"/> once the CONSUMPTION GATE holds (every perspective has processed
  /// every event at/below the close point) AND a CARRY-FORWARD event survives above it (the domain's closing
  /// event / new origin). Discard-only in increment 1 (cold-storage archive is a later increment). The domain
  /// appends its closing event BEFORE calling this. Default: unsupported no-op (engines without the primitive).
  /// </summary>
  /// <param name="streamId">The stream to close.</param>
  /// <param name="throughVersion">The inclusive per-stream version below which detail is truncated.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// A status (<c>closed</c> | <c>blocked</c> | <c>no_carry_forward</c> | <c>debug_skipped</c> |
  /// <c>unsupported</c>) plus the number of events truncated.
  /// </returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<StreamCloseResult> CloseStreamAsync(
    Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
    Task.FromResult(new StreamCloseResult("unsupported", 0));

  /// <summary>
  /// E2 destruction hooks: the ephemeral event bodies the tier-1 reaper is about to delete THIS cycle —
  /// the exact consumption-gated + aged-past-grace + snapshot-covered set of Task 8's <c>DELETE</c>, as a
  /// query. The maintenance worker fires a registered <c>IDestructionHook</c> for each before the reap, so
  /// a hook can preserve / compact / archive the body first. Default: empty (engines without the offload).
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The (event, stream, type) targets about to be reaped.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>([]);

  /// <summary>
  /// E2-3: hold the given ephemeral bodies from the reaper until <paramref name="holdUntil"/>, honoring a
  /// PreDestruction hook's decision — <c>Cancel</c> passes a far-future instant (keep the data; the
  /// developer's leak-risk call), <c>Defer(until)</c> passes that instant (after which the body is offered
  /// to the hook again). Task 8's reap skips any body with an active hold. Idempotent (upsert). Default
  /// no-op (engines without the ephemeral body offload).
  /// </summary>
  /// <param name="eventIds">The event bodies to hold.</param>
  /// <param name="holdUntil">The instant the hold lapses (e.g. <see cref="DateTimeOffset.MaxValue"/> for Cancel).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task HoldEphemeralDestructionAsync(
    IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// E2-5: records a destruction FAILURE for a batch (a throwing <c>PreDestruction</c> hook). Increments each
  /// event's attempt count and holds the batch until <paramref name="retryHoldUntil"/> so the next cycle
  /// re-offers it to the hook — UNLESS the attempt would exceed <paramref name="maxRetries"/>, in which case
  /// the hold is set in the past so the reaper FORCE-deletes the batch (a permanently-broken hook can never
  /// leak storage). Returns the highest attempt count in the batch. Default returns <see cref="int.MaxValue"/>
  /// (engines without the hold infra ⇒ forced delete ⇒ the prior fail-open behaviour).
  /// </summary>
  /// <param name="eventIds">The failed batch's event bodies.</param>
  /// <param name="retryHoldUntil">When the retry hold lapses (now + the configured backoff).</param>
  /// <param name="maxRetries">Attempts allowed before a forced delete.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<int> RecordDestructionFailureAsync(
    IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
    Lifecycle.OnDestroyFailure onFailure = Lifecycle.OnDestroyFailure.RetryThenForcedDelete,
    CancellationToken cancellationToken = default) => Task.FromResult(int.MaxValue);

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
    CancellationToken cancellationToken = default) => CompletePerspectiveEventsAsync(workItemIds, debugMode: false, cancellationToken);

  /// <summary>
  /// Same as the simpler <see cref="CompletePerspectiveEventsAsync(Guid[], CancellationToken)"/>
  /// overload but propagates <paramref name="debugMode"/> to the SQL. Production: DELETEs the rows.
  /// Debug: retains rows with <c>processed_at</c> stamped.
  /// </summary>
  /// <param name="workItemIds">Array of event_work_id values to complete.</param>
  /// <param name="debugMode">From <see cref="IWorkCoordinatorStrategy.DebugMode"/> at the call site.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows affected.</returns>
  Task<int> CompletePerspectiveEventsAsync(
    Guid[] workItemIds,
    bool debugMode,
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
  /// Slice 26.6b — returns the local service's stable identity from
  /// <c>wh_service_config</c>. Cached by callers (publish path) at startup; queried
  /// once per process. Default implementation returns <see cref="Guid.Empty"/> for
  /// legacy/in-memory coordinators that don't track service identity.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(Guid.Empty);

  /// <summary>
  /// Per-stream-id payload fetch for the OutboxDrainWorker. Given stream_ids that
  /// <see cref="ClaimWorkAsync"/> emitted as <see cref="WorkBatch.OutboxStreamIds"/>, returns the
  /// actual leased outbox rows for those streams in stream-FIFO order. Caps at
  /// <paramref name="maxPerStream"/> rows per stream so one busy stream cannot monopolize a fetch.
  /// </summary>
  /// <param name="streamIds">Stream ids to fetch leased outbox rows for.</param>
  /// <param name="instanceId">Calling instance — only its leased rows are returned.</param>
  /// <param name="maxPerStream">Per-stream cap. Default 100.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Leased outbox rows in (stream_id, created_at, message_id) order.</returns>
  /// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
  Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<OutboxBatchRow>>([]);

  /// <summary>
  /// Per-stream-id payload fetch for the InboxDrainWorker. Mirror of
  /// <see cref="FetchOutboxBatchAsync"/> for <c>wh_inbox</c>, ordered by received_at within each stream.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
  Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<InboxBatchRow>>([]);

  /// <summary>
  /// Cheap ID-only prefetch for the perspective drainer (Phase H step 7 slice 2). Returns
  /// (event_work_id, event_id) tuples for unprocessed <c>wh_perspective_events</c> rows leased
  /// to the caller, scoped to a single (stream_id, perspective_name), ordered by event_id ASC.
  /// The drainer uses this BEFORE pulling event bodies so it can filter against the in-memory
  /// cooldown cache and the cached cursor without paying the body-fetch + JSON-deserialize cost
  /// when no actual apply work is needed.
  /// </summary>
  /// <param name="streamId">Stream id to scope to.</param>
  /// <param name="perspectiveName">Perspective name to scope to.</param>
  /// <param name="instanceId">Calling instance id — only its leased rows are returned.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Pending event rows in event_id ASC order.</returns>
  /// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
  Task<IReadOnlyList<PendingPerspectiveEvent>> FetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<PendingPerspectiveEvent>>([]);

  /// <summary>
  /// Slice 25: atomic per-stream claim+fetch. Claims (or re-leases) ALL eligible pending
  /// rows for <paramref name="streamId"/> / <paramref name="perspectiveName"/> to
  /// <paramref name="instanceId"/> — including orphans, expired-lease rows, and the
  /// caller's own rows whose lease should be extended — then returns the post-claim set
  /// in event_id ASC order. Prevents the cursor-advances-past-orphaned-rows race that
  /// produced residual cursor inversions after slices 23 + 24c shipped: a row that
  /// existed in <c>wh_perspective_events</c> but wasn't yet claimed by anyone was
  /// invisible to <see cref="FetchPendingPerspectiveEventsAsync"/>, so the cursor
  /// could advance past it, only for it to surface later (now behind cursor) and trigger
  /// a rewind.
  /// </summary>
  /// <param name="streamId">Stream id to scope to.</param>
  /// <param name="perspectiveName">Perspective name to scope to.</param>
  /// <param name="instanceId">Calling instance id — rows are leased to this id.</param>
  /// <param name="leaseDuration">Lease duration applied to claimed rows.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Pending event rows now leased to <paramref name="instanceId"/>, in event_id ASC order.</returns>
  /// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
  Task<IReadOnlyList<PendingPerspectiveEvent>> ClaimAndFetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    TimeSpan leaseDuration,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<PendingPerspectiveEvent>>([]);

  /// <summary>
  /// Scoped event-body fetch from <c>wh_event_store</c> by event_id list (Phase H step 7 slice 4).
  /// Used by the perspective drainer AFTER its prefetch + filter pipeline narrows pending tuples
  /// to only those needing apply. Drainer pairs the result back to its prefetched
  /// <see cref="PendingPerspectiveEvent"/> tuples by event_id in C#, so the returned
  /// <see cref="StreamEventData.EventWorkId"/> is always <see cref="Guid.Empty"/>.
  /// </summary>
  /// <param name="eventIds">Event ids to fetch.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Matching event-store rows ordered by event_id ASC. Missing ids are silently dropped.</returns>
  /// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
  Task<IReadOnlyList<StreamEventData>> FetchEventsByIdsAsync(
    IReadOnlyList<Guid> eventIds,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<StreamEventData>>([]);

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

  /// <summary>
  /// Deletes <c>wh_inbox</c> rows whose <c>message_type</c> is NOT in the provided list of
  /// locally-handled types. Slice 3 of the resilient-transport plan — companion to slice 2's
  /// receptor-registry filter at receive. Slice 2 prevents new orphans from landing; this
  /// purges rows that accumulated before deploy or after a service stops consuming a type.
  /// </summary>
  /// <remarks>
  /// Pass the union of locally-handled assembly-qualified CLR type names (from receptor +
  /// perspective registries). Empty list = no-op (safe). Skips leased rows to avoid stomping
  /// on actively-dispatching work. Default impl returns empty list so non-Postgres backends
  /// and test fakes don't need to override.
  /// </remarks>
  /// <param name="handledTypeNames">Assembly-qualified type names handled locally.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Tuples of <c>(MessageId, MessageType, HandlerName)</c> that were deleted, for logging.</returns>
  Task<IReadOnlyList<PurgedOrphanInboxRow>> PurgeOrphanInboxAsync(
    IReadOnlyList<string> handledTypeNames,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<PurgedOrphanInboxRow>>([]);

  /// <summary>
  /// v0.657 slice 5: structural canary for the "row claimed but never drained"
  /// bug class. Returns <c>wh_outbox</c> rows whose <c>attempts</c> exceeds
  /// <paramref name="maxAttempts"/> AND have not been processed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// production forensic exposed a class of bug — silent stuck rows — that bypasses
  /// every downstream defense. This surface lets the maintenance worker log a
  /// Warning per row exhibiting the symptom, independent of root cause.
  /// </para>
  /// <para>
  /// Default impl returns empty list so non-Postgres backends and test fakes
  /// don't need to override. Postgres backend uses a partial index gated on
  /// <c>attempts &gt; 5</c> so query cost is O(log N) on a near-empty set.
  /// </para>
  /// </remarks>
  /// <param name="maxAttempts">Threshold — rows with attempts &gt; this are surfaced.</param>
  /// <param name="limit">Cap on returned rows so log emission is bounded under saturation.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  Task<IReadOnlyList<StuckRow>> FindStuckOutboxRowsAsync(
    int maxAttempts,
    int limit,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<StuckRow>>([]);

  /// <summary>Mirror of <see cref="FindStuckOutboxRowsAsync"/> for <c>wh_inbox</c>.</summary>
  /// <docs>operations/observability/stuck-row-sentinel</docs>
  Task<IReadOnlyList<StuckRow>> FindStuckInboxRowsAsync(
    int maxAttempts,
    int limit,
    CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<StuckRow>>([]);
}

/// <summary>
/// One inbox row deleted by <see cref="IWorkCoordinator.PurgeOrphanInboxAsync"/>. Returned for
/// structured logging and metrics — operators want to see which type names dominated a purge.
/// </summary>
public sealed record PurgedOrphanInboxRow(Guid MessageId, string MessageType, string HandlerName);

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
/// Outcome of <see cref="IWorkCoordinator.ReclassifyEventsEphemeralAsync"/>: how many historical events of
/// a type were reclassified Sourced→Ephemeral, across how many streams, and how many streams were skipped
/// because reclassifying there would have produced a mixed (part-Sourced, part-Ephemeral) stream.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record EphemeralReclassificationResult(
  long EventsReclassified,
  long StreamsReclassified,
  long StreamsBlocked) {
  /// <summary>Nothing reclassified — the default/no-op result.</summary>
  public static EphemeralReclassificationResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// A per-type rewind-grace override — the type's (current) CLR name and its
/// <c>[Ephemeral(RewindGraceSeconds)]</c> value in seconds.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record EphemeralTypeGrace(string EventTypeName, int GraceSeconds);

/// <summary>
/// A <c>(stream, perspective)</c> pair that must be snapshotted before the reaper deletes its consumed,
/// aged ephemeral bodies — carrying the perspective cursor's last processed event id to snapshot at (the
/// current authoritative model through that point becomes the rewind floor).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record EphemeralSnapshotTarget(Guid StreamId, string PerspectiveName, Guid LastEventId);

/// <summary>
/// Result of the tier-2 ancient-ephemeral-pointer prune
/// (<see cref="IWorkCoordinator.PruneAncientEphemeralPointersAsync"/>): how many pointers were deleted and
/// why/why-not (<c>disabled</c> | <c>skipped (debug_mode=true)</c> | <c>not due</c> | <c>ok</c> |
/// <c>unsupported</c>).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record EphemeralPointerPruneResult(long RowsPruned, string Status);

/// <summary>
/// Result of an A1 "close the books" gated truncate (<see cref="IWorkCoordinator.CloseStreamAsync"/>): the
/// <see cref="Status"/> (<c>closed</c> = detail truncated | <c>blocked</c> = a perspective has not consumed
/// past the close point | <c>no_carry_forward</c> = no surviving event above the close point | <c>debug_skipped</c>
/// | <c>unsupported</c>) and how many events were truncated (0 unless <c>closed</c>).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record StreamCloseResult(string Status, long EventsTruncated);

/// <summary>
/// An ephemeral event body the reaper is about to delete (E2), passed to a destruction hook so it can
/// preserve the body before the reap. Carries the identity + type needed to build a
/// <see cref="Whizbang.Core.Lifecycle.DestructionContext"/>.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record EphemeralDestructionTarget(Guid EventId, Guid StreamId, string EventType);

/// <summary>
/// A stored type-definition fingerprint row — one distinct type-definition-version keyed by its content
/// hashes (lowercase hex). Loaded on startup so the reconciler can diff the code's current definitions.
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public sealed record TypeDefinitionInfo(
  int DefinitionId,
  string EventType,
  string SettingsHashHex,
  string SchemaHashHex,
  int SchemaVersion);

/// <summary>
/// Result of <see cref="IWorkCoordinator.RegisterTypeDefinitionAsync"/>: the definition's id, whether it
/// was newly inserted, and the type's previous definition id (non-null only on a genuinely new insert).
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public sealed record TypeDefinitionRegistration(int DefinitionId, bool IsNew, int? PreviousDefinitionId) {
  /// <summary>No-op sentinel for engines without the fingerprint tables.</summary>
  public static TypeDefinitionRegistration None { get; } = new(0, false, null);
}

/// <summary>
/// How one type definition superseded another — labels a <c>wh_definition_lineage</c> edge so a stale
/// definition names the kind of migration that brings its events current.
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
public enum DefinitionRelationship {
  /// <summary>The payload schema changed — events need upcasting.</summary>
  SchemaUpgradedTo = 0,
  /// <summary>The storage classification changed (e.g. Sourced → Ephemeral) — events need reclassifying.</summary>
  ReclassifiedTo = 1,
  /// <summary>Behavioral settings changed without a storage-class or schema change.</summary>
  MetadataChangedTo = 2,
}

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
  /// Slice 26.13 — commit_sequence of <see cref="LastEventId"/> at the time of cursor
  /// advance. Hydrated by joining <c>wh_perspective_cursors.last_event_id</c> to
  /// <c>wh_event_store.commit_sequence</c>. NULL when the cursor has never advanced or when
  /// the underlying event hasn't been stamped yet. PerspectiveWorker prefetch uses this to
  /// warm <see cref="Whizbang.Core.Workers.PerspectiveCursorCache.SetCommitSequence"/> so the
  /// commit-sequence-based inversion detector runs on cold caches (process start, post-rewind),
  /// not the UUIDv7 event_id fallback path.
  /// </summary>
  public long? LastCommitSequence { get; init; }

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
/// Result of a claim/drain cycle containing work that needs processing.
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
  /// Distinct stream IDs that have leased outbox messages for this instance — the per-stream-id
  /// drain channel surface for the new <c>OutboxDrainWorker</c>. Restored from the archive plan's
  /// poller-vs-drainer split: the poller emits stream_ids only (small payload); the drainer
  /// fetches payloads on demand via <see cref="IWorkCoordinator.FetchOutboxBatchAsync"/>.
  /// During the Phase H step 5 transition this list is derived from <see cref="OutboxWork"/>;
  /// once <c>claim_work</c> SQL drops the body projection, this becomes the only outbox surface.
  /// </summary>
  public List<Guid> OutboxStreamIds { get; init; } = [];

  /// <summary>
  /// Distinct stream IDs that have leased inbox messages for this instance — the per-stream-id
  /// drain channel surface for the new <c>InboxDrainWorker</c>. Mirror of
  /// <see cref="OutboxStreamIds"/> for inbox dispatch.
  /// </summary>
  public List<Guid> InboxStreamIds { get; init; } = [];

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
  /// Whether this message is a composite event (implements
  /// <see cref="Whizbang.Core.Messaging.ICompositeEvent"/>) that the
  /// receiver fans out into N inner events. Set at producer-side dispatch
  /// time; surfaces on the wire via destination metadata and on the
  /// receiving inbox row so observability dashboards can distinguish
  /// composite vs. regular flow and so the receiver expansion path
  /// (slice 10) can detect work to expand without re-running the payload
  /// type check.
  /// </summary>
  /// <remarks>
  /// Categorization bitmask combining all event categories the framework
  /// understands at dispatch time:
  /// <list type="bullet">
  ///   <item><description><see cref="EventFlags.Composite"/> — wire-only fan-out bundle of inner events (W3 slice 9)</description></item>
  ///   <item><description><see cref="EventFlags.Collective"/> — first-class persistable scope-mutation event (Slice 3' of the collective-events feature)</description></item>
  /// </list>
  /// Replaces the two separate boolean columns (<c>is_composite</c>,
  /// <c>is_collective</c>) with one bitmask. New categories ship by
  /// adding a flag value to <see cref="EventFlags"/>; no schema
  /// migration required.
  /// </remarks>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsTransportTests.cs:OutboxMessage_Flags_DefaultsToNoneAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsTransportTests.cs:OutboxMessage_Flags_AcceptsCollectiveAndCompositeAsync</tests>
  public EventFlags Flags { get; init; }

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

  /// <summary>
  /// Optional UTC instant when the message becomes eligible for delivery. Carried from
  /// <see cref="Whizbang.Core.Dispatch.DispatchOptions.ScheduledFor"/> through the dispatcher
  /// chain into <c>store_outbox_messages</c>, which writes it to <c>wh_outbox.scheduled_for</c>.
  /// The pickup query (<c>FetchOutboxInboxBatch</c>, migration 040) and the NOTIFY-based
  /// wake (<c>NotifyScheduledRetryDue</c>, migration 049) gate visibility on
  /// <c>scheduled_for IS NULL OR scheduled_for &lt;= NOW()</c> already.
  /// </summary>
  /// <remarks>
  /// <para>Null (default) means immediate delivery — the row is publishable as soon as the
  /// outbox publisher next claims it. Non-null defers the row until the timestamp elapses,
  /// then the NOTIFY-based wake-up (mig 049) signals the owning instance to pick it up
  /// without paying the polling tax.</para>
  /// <para>This is the producer-side surface for the same column the retry-backoff SQL
  /// paths (migrations 017/018/019) write internally. The two are orthogonal: a failed
  /// scheduled-delivery retries with backoff on top of its original schedule.</para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/OutboxMessageScheduledForTests.cs:OutboxMessage_ScheduledFor_PropertyExistsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/OutboxMessageScheduledForTests.cs:OutboxMessage_ScheduledFor_DefaultIsNullAsync</tests>
  public DateTimeOffset? ScheduledFor { get; init; }
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
  /// Categorization bitmask preserved from the originating
  /// <see cref="OutboxMessage.Flags"/>. The transport consumer worker
  /// copies this value when storing the inbox row so the projection
  /// runner can branch on individual flag bits
  /// (<c>(Flags &amp; EventFlags.Collective) != 0</c>) without
  /// re-running the payload type check.
  /// </summary>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsTransportTests.cs:InboxMessage_Flags_DefaultsToNoneAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventFlagsTransportTests.cs:InboxMessage_Flags_AcceptsCollectiveAndCompositeAsync</tests>
  public EventFlags Flags { get; init; }

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

  /// <summary>
  /// Slice 26 — originating service's identity, copied from the envelope's
  /// <see cref="MessageEnvelope{T}.SourceServiceId"/>. Persisted into
  /// <c>wh_inbox.source_service_id</c>. When omitted (default <see cref="Guid.Empty"/>),
  /// the SQL trigger COALESCEs to the local <c>wh_service_config.service_id</c>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  public Guid SourceServiceId { get; init; }

  /// <summary>
  /// Slice 26 — source service's <c>commit_sequence</c> stamp, copied from the envelope's
  /// <see cref="MessageEnvelope{T}.SourceCommitSequence"/>. Persisted into
  /// <c>wh_inbox.source_commit_sequence</c>. Defaults to 0.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  public long SourceCommitSequence { get; init; }
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
  /// The message's raw <c>wh_outbox.metadata</c> JSON, as stored. The typed envelope metadata is a closed
  /// shape, so provider-stamped keys (e.g. the temporal engine's <c>scheduleId</c> /
  /// <c>deliveryGuarantee</c> / <c>authorityPrincipalId</c> on a schedule occurrence) only survive here.
  /// Consumed by <c>IOccurrencePublishGate</c> to recognise an occurrence before it is published.
  /// </summary>
  public string? MetadataJson { get; init; }

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

  /// <summary>
  /// Previous error text persisted on the inbox row (<c>wh_inbox.error</c>), populated
  /// by the most recent <c>process_inbox_failures</c> cycle. NULL when no prior failure
  /// has been recorded for this row.
  /// </summary>
  /// <remarks>
  /// Mirrors <see cref="OutboxBatchRow.Error"/>. Used by InboxDispatchWorker's
  /// pre-publish DLQ gate (release/v0.651.0-alpha.1, the inbox-side equivalent of the
  /// v0.648 outbox forensic-preservation slice): when attempts exceed
  /// <c>MaxInboxAttempts</c>, the worker prefers the real exception text from this
  /// field over the synthetic <c>"InboxDispatchWorker dead-lettered: attempts=N"</c>
  /// meta-message — restoring fingerprint cluster diversity in <c>wh_dead_letters</c>.
  /// </remarks>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  public string? Error { get; init; }
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

  /// <summary>
  /// Perspective name from wh_perspective_events.perspective_name. Required for the cooldown
  /// gate's per-perspective filter — without it, marking ANY perspective's work_id as
  /// recently-processed under the same event_id would prevent OTHER perspectives' Apply from
  /// running on subsequent drains for the same event (a consumer 2026-05-04 silent-skip bug).
  /// </summary>
  public string? PerspectiveName { get; init; }

  /// <summary>
  /// Slice 26 — <c>wh_event_store.commit_sequence</c>, populated post-commit by the stamper
  /// worker. NULL means the stamper hasn't caught up yet — downstream consumers should treat
  /// this row as "not stable for cursor comparison" and either defer or fall back to event_id
  /// ordering.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  public long? CommitSequence { get; init; }

  /// <summary>
  /// v0.502 slice C.4c: <c>wh_perspective_events.attempts</c> for this work row, one-based
  /// post-claim count. PerspectiveWorker uses this to dead-letter rows that exceed
  /// <see cref="Whizbang.Core.Workers.PerspectiveWorkerOptions.MaxPerspectiveEventAttempts"/>
  /// before deserialization + apply runs. Default 0 for legacy fakes / in-memory coordinators
  /// that don't track attempts.
  /// </summary>
  /// <docs>operations/dead-letter-queue/perspective-events</docs>
  public int Attempts { get; init; }
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
/// One pending perspective-event row returned from
/// <see cref="IWorkCoordinator.FetchPendingPerspectiveEventsAsync"/>. Carries only the IDs needed
/// for the drainer's cheap-first pipeline: cooldown filter, cursor-inversion check, and
/// scoped body fetch. Never carries event bodies — those are fetched separately for the subset
/// of rows that survive filtering.
/// </summary>
/// <remarks>
/// production G6: <see cref="CommitSequence"/> carries <c>wh_event_store.commit_sequence</c> via
/// a LEFT JOIN inside the SQL fn. The drainer's inversion detector compares against the
/// cached cursor's commit_sequence directly — no per-event GetCommitSequence round-trip,
/// no UUIDv7 lex-inversion edge case. Null when the stamper hasn't caught up yet; callers
/// fall back to event_id compare.
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed record PendingPerspectiveEvent(Guid EventWorkId, Guid EventId, long? CommitSequence = null);

/// <summary>
/// One leased outbox row returned from <see cref="IWorkCoordinator.FetchOutboxBatchAsync"/>.
/// The drainer worker deserializes <see cref="EventData"/> into a typed envelope before publishing.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed record OutboxBatchRow {
  /// <summary>Outbox message id (wh_outbox.message_id).</summary>
  public required Guid MessageId { get; init; }
  /// <summary>Stream id this message belongs to (may be null for unbound messages).</summary>
  public Guid? StreamId { get; init; }
  /// <summary>Transport destination (topic). Null for event-store-only messages.</summary>
  public string? Destination { get; init; }
  /// <summary>Assembly-qualified message payload type.</summary>
  public required string MessageType { get; init; }
  /// <summary>Assembly-qualified envelope type.</summary>
  public string? EnvelopeType { get; init; }
  /// <summary>Serialized envelope JSON (event_data column).</summary>
  public required string EventData { get; init; }
  /// <summary>Hop metadata JSON.</summary>
  public required string Metadata { get; init; }
  /// <summary>Scope JSON (may be null).</summary>
  public string? Scope { get; init; }
  /// <summary>Status bit flags.</summary>
  public int Status { get; init; }
  /// <summary>Number of previous publish attempts.</summary>
  public int Attempts { get; init; }
  /// <summary>Computed partition for this stream.</summary>
  public int? PartitionNumber { get; init; }
  /// <summary>True if this outbox message is also written to the event store.</summary>
  public bool IsEvent { get; init; }

  /// <summary>
  /// Slice 26.6b — JOINed <c>wh_event_store.commit_sequence</c>. Null until the stamper
  /// has caught up (publish should defer or fall back to publishing without the stamp
  /// when null). Used by the publisher to populate envelope <c>SourceCommitSequence</c>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  public long? CommitSequence { get; init; }

  /// <summary>
  /// Slice 26.6b — JOINed <c>wh_event_store.origin_service_id</c>. Non-null only for 1:1
  /// forwarded events; null for locally-originated. Publisher COALESCEs to the local
  /// <c>wh_service_config.service_id</c> when null.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  public Guid? OriginServiceId { get; init; }

  /// <summary>
  /// Slice 26.6b — JOINed <c>wh_event_store.origin_commit_sequence</c>. Companion to
  /// <see cref="OriginServiceId"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  public long? OriginCommitSequence { get; init; }

  /// <summary>
  /// Slice 1 of release/v0.648.0-alpha.1 — the row's existing <c>wh_outbox.error</c>
  /// column value from the last <c>process_outbox_failures</c> cycle. Used by the
  /// pre-publish DLQ gate so the DLQ row's <c>error_text</c> + fingerprint reflect
  /// the actual root cause (a real exception stack from a prior failure) instead
  /// of a meta-message like <c>"OutboxDrainWorker dead-lettered: attempts=N > max=10"</c>
  /// — the meta-message collapses every DLQ row to a single fingerprint, wiping
  /// out triage value (production Jun-2026: 38k+ rows collapsed to one fingerprint
  /// cluster). NULL when no failure has been recorded against the row yet (the
  /// gate falls back to the meta-message in that case).
  /// </summary>
  /// <docs>operations/dead-letter-queue/internal-dlq</docs>
  public string? Error { get; init; }
}

/// <summary>
/// One leased inbox row returned from <see cref="IWorkCoordinator.FetchInboxBatchAsync"/>.
/// The drainer worker deserializes <see cref="EventData"/> into a typed envelope before dispatching to its handler.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed record InboxBatchRow {
  /// <summary>Inbox message id (wh_inbox.message_id).</summary>
  public required Guid MessageId { get; init; }
  /// <summary>Stream id this message belongs to (may be null).</summary>
  public Guid? StreamId { get; init; }
  /// <summary>Handler name (e.g., the receptor type's full name).</summary>
  public required string HandlerName { get; init; }
  /// <summary>Assembly-qualified message payload type.</summary>
  public required string MessageType { get; init; }
  /// <summary>Serialized envelope JSON (event_data column).</summary>
  public required string EventData { get; init; }
  /// <summary>Hop metadata JSON.</summary>
  public required string Metadata { get; init; }
  /// <summary>Scope JSON (may be null).</summary>
  public string? Scope { get; init; }
  /// <summary>Status bit flags.</summary>
  public int Status { get; init; }
  /// <summary>Number of previous handler attempts.</summary>
  public int Attempts { get; init; }
  /// <summary>Computed partition for this stream.</summary>
  public int? PartitionNumber { get; init; }
  /// <summary>True if this inbox message is also written to the event store.</summary>
  public bool IsEvent { get; init; }
  /// <summary>
  /// Previous error text persisted on the inbox row (<c>wh_inbox.error</c>), populated
  /// by the most recent <c>process_inbox_failures</c> cycle. NULL when no prior failure
  /// has been recorded. Plumbed into <see cref="InboxWork.Error"/> for the v0.651
  /// inbox forensic-preservation slice.
  /// </summary>
  public string? Error { get; init; }
}

