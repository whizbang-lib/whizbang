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
/// Work outstanding across an entire service, used to decide whether the service has SETTLED.
/// </summary>
/// <remarks>
/// Distinct from <see cref="OutstandingWork"/>, which is scoped to one instance. An instance that
/// finished its own claimed streams reads zero locally while peers are still draining, so an
/// instance-scoped count cannot answer "has this service settled".
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
public sealed record ServiceBacklog {
  /// <summary>Unprocessed inbox rows across the whole service.</summary>
  public long UnprocessedInboxRows { get; init; }

  /// <summary>
  /// Rows currently leased by ANY instance. Non-zero means a peer is mid-dispatch, so rows counted
  /// as missing may simply be in that peer's hands.
  /// </summary>
  public long ActiveLeasedRows { get; init; }

  /// <summary>
  /// Age of the oldest unprocessed inbox row, or <see cref="TimeSpan.Zero"/> when nothing is queued.
  /// </summary>
  /// <remarks>
  /// The lag signal <see cref="IntegrityRepairPolicy"/> needs alongside depth and leases. Depth is a
  /// bounded count and a snapshot; an operator who raises the settled-depth threshold to tolerate a
  /// small queue still needs to see that something in that small queue has been sitting for an hour.
  /// </remarks>
  public TimeSpan OldestUnprocessedAge { get; init; }

  /// <summary>True when nothing is queued and no instance holds a live lease.</summary>
  public bool IsSettled => UnprocessedInboxRows == 0 && ActiveLeasedRows == 0;
}


/// <summary>
/// Work this instance currently holds a live lease on and has not finished, counted in the store
/// independently of any claim limit.
/// </summary>
/// <remarks>
/// <para>
/// The independence is the entire point. A claim response cannot report this figure, because the
/// claim applies <c>LIMIT p_max_streams</c> to its eligible set — so counting the rows it returns
/// measures the limit, not the backlog. A worker that sized its claim from its own last batch would
/// observe at most what it just asked for, conclude it had headroom, and claim again, holding more
/// and more work while the number it watched sat pinned at the limit.
/// </para>
/// <para>
/// It is also read from the store rather than accumulated in memory. A counter the worker maintains
/// itself can be stranded by a hung or cancelled task and then never recovers without a restart —
/// a failure mode this claim path has already produced in production once.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
public sealed record OutstandingWork {
  /// <summary>Leased, unprocessed rows in <c>wh_inbox</c>.</summary>
  public long InboxRows { get; init; }
  /// <summary>Leased, unprocessed rows in <c>wh_outbox</c>.</summary>
  public long OutboxRows { get; init; }
  /// <summary>Leased, unapplied rows in the perspective work table.</summary>
  public long PerspectiveRows { get; init; }

  /// <summary>
  /// Every leased row this instance holds. All three kinds count: each is leased and each charges
  /// an attempt, so bounding one column would leave the same arithmetic free to recur in another.
  /// </summary>
  public long Total => InboxRows + OutboxRows + PerspectiveRows;
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
  /// Counts the work this instance holds a live lease on and has not finished — the figure the
  /// claim-outstanding budget is sized against.
  /// </summary>
  /// <param name="instanceId">The instance whose held work is being counted.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// The current counts, or <see langword="null"/> when this backend cannot measure them.
  /// </returns>
  /// <remarks>
  /// <para>
  /// Defaulted to <see langword="null"/> rather than <c>0</c>, and the distinction is load-bearing.
  /// Zero is a measurement meaning "this instance holds nothing", which would license a full-size
  /// claim; <see langword="null"/> means "unmeasured", and the budget declines to engage rather
  /// than bound against a number it did not read. Returning zero from a backend that cannot count
  /// would disable the bound while looking exactly like a healthy idle worker.
  /// </para>
  /// <para>
  /// Defaulted at all — rather than added to the interface outright — because implementations are
  /// numerous and mostly test doubles that have no store to count. Forcing each to supply a body
  /// would produce a wave of <c>0</c> and <c>throw</c> stubs, and the <c>0</c>s are precisely the
  /// silent-disable this method exists to make impossible.
  /// </para>
  /// </remarks>
  ValueTask<OutstandingWork?> CountOutstandingWorkAsync(
      Guid instanceId, CancellationToken cancellationToken = default)
    => ValueTask.FromResult<OutstandingWork?>(null);

  /// <summary>
  /// Counts work outstanding across the WHOLE service, not just the calling instance.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Settledness is a service property. A service runs many instances against one shared store, and
  /// an instance that has finished its own claimed streams looks completely idle from the inside
  /// while peers are still draining. A control deciding from its LOCAL view acts on events its own
  /// siblings are actively processing.
  /// </para>
  /// <para>
  /// Returns null when the backend cannot answer. Callers must treat null as UNMEASURED — never as
  /// settled — because "nothing outstanding" and "nobody looked" are the same value and opposite
  /// facts, and defaulting to settled re-enables exactly the behavior the caller is gating.
  /// </para>
  /// </remarks>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Service-wide outstanding work, or null when unmeasurable.</returns>
  ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default)
    => ValueTask.FromResult<ServiceBacklog?>(null);

  /// <summary>
  /// Publishes the host's debug-retention setting to the store, where the maintenance sweep reads it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Debug retention is decided in two places. The completion path honors
  /// <c>WorkCoordinatorOptions.DebugMode</c> in process; the maintenance sweep reads a stored
  /// setting instead. Nothing wrote that setting, so enabling the documented option produced
  /// retention that the sweep silently undid within one interval — the counts it exists to enable
  /// fell while being read.
  /// </para>
  /// <para>
  /// Default is a no-op so engines without a settings table are unaffected. Implementations must
  /// write BOTH values: leaving a stale true behind would disable the purge permanently and grow
  /// the inbox without bound.
  /// </para>
  /// </remarks>
  /// <param name="debugMode">Whether completed rows should be retained.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the setting is stored.</returns>
  Task SyncDebugRetentionSettingAsync(bool debugMode, CancellationToken cancellationToken = default)
    => Task.CompletedTask;

  /// <summary>
  /// Records a heartbeat for this instance. Fired on its own cadence by the C# HeartbeatWorker can fire on its own cadence (5 s default) independent of polling.
  /// Sub-millisecond UPSERT against <c>wh_service_instances</c>. Default impl throws so existing
  /// non-Postgres backends (test fakes, in-memory) only opt in when ready.
  /// Phase B of work-pump decomposition.
  /// </summary>
  /// <param name="request">Instance identity + optional metadata.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> if the heartbeat was recorded; <see langword="false"/> if this
  /// instance has been evicted (reaped as stale, then tombstoned) and must not consider itself
  /// part of the fleet — callers must stop heartbeating rather than retry, since a tombstoned
  /// instance id never becomes valid again.
  /// </returns>
  /// <docs>fundamentals/work-coordinator/configuration-reference</docs>
  /// <docs>fundamentals/workers/instance-liveness#eviction-reaping-is-a-fence-not-just-a-deletion</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreRecordHeartbeatTests.cs:RecordHeartbeatAsync_NewInstance_InsertsRowAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceEvictionFencingSqlTests.cs</tests>
  Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default)
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
  /// Hands back inbox rows this instance claimed but never dispatched, refunding the attempt the
  /// claim optimistically charged and clearing the lease.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>claim_orphaned_inbox</c> charges an attempt on every claim, which must stay: it is the only
  /// fail-safe that survives a process vanishing mid-dispatch, because a dead process reports
  /// nothing. The cost is that a worker claiming more rows than it can dispatch inside the lease
  /// window pays an attempt for every untouched row, every cycle — so a backlog larger than one
  /// worker's throughput burns its own retry budget and dead-letters healthy messages as
  /// <see cref="MessageFailureReason.MaxAttemptsExceeded"/> having never reached a receptor, with no
  /// failure recorded anywhere because none occurred.
  /// </para>
  /// <para>
  /// The resolution is a refund rather than a smaller charge. A worker that ends a cycle still
  /// holding rows it never touched calls this; an UNGRACEFUL exit calls nothing, so its charge
  /// correctly stands. The store cannot distinguish "never dispatched" from "dispatched and died" —
  /// only the worker can, which is why this is an explicit call rather than store-side inference.
  /// </para>
  /// <para>
  /// Idempotent, and scoped to the caller's own claim: releasing a row held by another instance is a
  /// no-op, so a late or duplicated release can neither refund twice nor unlock work another worker
  /// is actively dispatching.
  /// </para>
  /// </remarks>
  /// <param name="instanceId">The instance that holds the claim. Rows held by anyone else are skipped.</param>
  /// <param name="messageIds">Inbox message ids being handed back undispatched.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Number of rows actually released.</returns>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs</tests>
  Task<int> ReleaseUnprocessedInboxAsync(
    Guid instanceId,
    IReadOnlyList<Guid> messageIds,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException(
      $"{GetType().Name} does not implement ReleaseUnprocessedInboxAsync.");

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
  /// <docs>messaging/transports/transport-consumer</docs>
  Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Stores inbox messages exactly as <see cref="StoreInboxMessagesAsync"/> does and additionally
  /// reports the store's durable REDELIVERY OBSERVATIONS — message ids the store had already seen,
  /// with the post-write count (topology arc phase 8.5, poison detection layer 2).
  /// <para>
  /// This is not a second query: the store-side idempotency record is written on every delivery
  /// anyway, so the count comes back as a by-product of work already paid for. It is the only
  /// bound available when the broker's own delivery counter cannot rise — a lock lost to
  /// connection death on a SESSION-enabled entity leaves that counter at 1, which is what makes
  /// MaxDeliveryCount structurally unreachable there.
  /// </para>
  /// Default implementation stores and reports NOTHING, so a coordinator that cannot supply the
  /// count (test fakes, non-Postgres backends) degrades to layer 1 rather than breaking.
  /// </summary>
  /// <param name="messages">Inbox messages to store.</param>
  /// <param name="partitionCount">Partition count; see <see cref="StoreInboxMessagesAsync"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One entry per message the store had already recorded, newest count first written.</returns>
  async Task<IReadOnlyList<InboxRedeliveryObservation>> StoreInboxMessagesWithObservationsAsync(
      InboxMessage[] messages,
      int partitionCount,
      CancellationToken cancellationToken = default) {
    await StoreInboxMessagesAsync(messages, partitionCount, cancellationToken).ConfigureAwait(false);
    return [];
  }

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
  /// rows wedged by a partition-mismatch (observed in production). Returns
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
  /// <param name="maxOrphans">GLOBAL cap on the returned batch (oldest first) — the reconcile is a
  /// bounded unit of work the caller loops, never an unbounded startup scan that can stall a host
  /// past its liveness budget on a large store.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Orphaned events with their envelopes for PostLifecycle replay.</returns>
  /// <docs>fundamentals/lifecycle/lifecycle-reconciliation</docs>
  Task<IReadOnlyList<OrphanedLifecycleEvent>> GetOrphanedLifecycleEventsAsync(
    Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType,
    TimeSpan lookbackWindow,
    int maxOrphans = 100,
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
  /// Returns the subset of <paramref name="streamIds"/> that are <strong>StateBased</strong> — streams holding
  /// at least one event flagged <c>EventFlags.Ephemeral</c> OR <c>EventFlags.Compacted</c> (see
  /// <see cref="EventFlagsExtensions.IsStateBased"/>). The rebuild/rewind guards use this to refuse them: a
  /// StateBased stream's current state, not its event log, is the source of truth (ephemeral bodies are reaped;
  /// a compacted stream replays only to its <c>Compacted</c> origin), so replaying it from events would corrupt
  /// the projection. Empty on engines without the flags. (Was <c>GetEphemeralStreamIdsAsync</c>; widened when
  /// <c>Compacted</c> — permanent StateBased — joined ephemeral under the StateBased base.)
  /// </summary>
  /// <param name="streamIds">Candidate stream ids to classify.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The ids among <paramref name="streamIds"/> that are StateBased.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<IReadOnlyCollection<Guid>> GetStateBasedStreamIdsAsync(
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
  /// Carries each perspective's row-retention declaration into the perspective registry, so the
  /// reaper resolves enrollment and windows from SQL rather than needing them threaded in per cycle.
  /// Called once at startup. No-op on engines without the registry columns.
  /// </summary>
  /// <param name="declarations">Every perspective's declaration; drives enrolment and un-enrolment.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/perspectives/row-retention</docs>
  Task SyncPerspectiveRetentionAsync(
    IReadOnlyList<PerspectiveRetentionDeclaration> declarations,
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
  /// Stream-integrity R1: selects persisted events from THIS service's event store for re-delivery
  /// to the wire, in original stored form, ordered by <c>(stream_id, version)</c> so per-stream
  /// bundles replay in append order. All <see cref="RedeliveryRequest"/> filters are conjunctive.
  /// The selection itself excludes at-most-once schedule occurrences (their delivery guarantee
  /// forbids re-delivery) and reaped ephemeral events (the body join — an absent body cannot be
  /// re-delivered). Default: empty (engines without an event store cannot re-deliver).
  /// </summary>
  /// <param name="request">Selection criteria and cap.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Selected events, capped at <see cref="RedeliveryRequest.MaxEvents"/>.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SelectRedeliveryEventsTests.cs</tests>
  Task<IReadOnlyList<RedeliveryEvent>> SelectRedeliveryEventsAsync(
    RedeliveryRequest request,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RedeliveryEvent>>([]);

  /// <summary>
  /// Stream-integrity Phase B: atomically advances this origin's checkpoint watermark to the
  /// highest STAMPED commit sequence and returns the per-(tenant, type) emission counts inside the
  /// advanced window — the payload of one <see cref="IntegrityCheckpoint"/>. Multi-instance safe:
  /// the watermark advance is an optimistic compare-and-swap, so exactly one instance wins each
  /// window (the others get null and skip the cycle). The FIRST call baselines (returns an empty
  /// window at the current watermark) so history is never counted retroactively. At-most-once
  /// schedule occurrences and checkpoints themselves are excluded from the counts. Default: null
  /// (engines without commit-sequence stamping cannot checkpoint).
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The advanced window, or null when unsupported / lost the advance race.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointAdvanceTests.cs</tests>
  Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(
    CancellationToken cancellationToken = default) => Task.FromResult<IntegrityCheckpointWindow?>(null);

  /// <summary>
  /// Stream-integrity Phase B, consumer side: counts the events THIS service has persisted from
  /// the given origin inside an origin commit-sequence window, per (tenant, type) — the local half
  /// of a checkpoint comparison. Keys on the origin identity every received event already persists
  /// (<c>origin_service_id</c> + <c>origin_commit_sequence</c>). Default: empty (engines without
  /// origin stamping cannot verify).
  /// </summary>
  /// <param name="originServiceId">The origin whose window is being verified.</param>
  /// <param name="fromCommitSequence">Exclusive window floor.</param>
  /// <param name="toCommitSequence">Inclusive window watermark.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Per-(tenant, type) receipt counts inside the window.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointAdvanceTests.cs</tests>
  Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
    Guid originServiceId,
    long fromCommitSequence,
    long toCommitSequence,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CheckpointBucket>>([]);

  /// <summary>
  /// Stream-integrity Phase S: the consumed-type registry — when each event type joined this
  /// service's consumed set, and its backfill status. Default: empty (engines without the
  /// registry table cannot track expansions).
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Every registered consumed type with its backfill status.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ConsumedTypeRegistryTests.cs</tests>
  Task<IReadOnlyList<ConsumedTypeRegistration>> GetConsumedTypeRegistrationsAsync(
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConsumedTypeRegistration>>([]);

  /// <summary>
  /// Stream-integrity Phase S: registers newly-consumed event types. First boot registers the
  /// whole catalog as <see cref="ConsumedTypeBackfillStatus.Baseline"/> (nothing existed to miss);
  /// later boots register additions as <see cref="ConsumedTypeBackfillStatus.Pending"/>
  /// (an expansion — history exists this service never received). Idempotent and multi-instance
  /// safe (<c>ON CONFLICT DO NOTHING</c> — the first instance to boot wins each row). Default: no-op.
  /// </summary>
  /// <param name="eventTypes">Stored event type names to register.</param>
  /// <param name="asBaseline">True = first-boot registration (no backfill); false = expansion (Pending).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ConsumedTypeRegistryTests.cs</tests>
  Task RegisterConsumedTypesAsync(
    IReadOnlyList<string> eventTypes,
    bool asBaseline,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Stream-integrity Phase S: marks pending expansions as
  /// <see cref="ConsumedTypeBackfillStatus.Requested"/> after the broadcast re-delivery request is
  /// sent. Only Pending rows transition (Baseline/Requested rows are untouched). Default: no-op.
  /// </summary>
  /// <param name="eventTypes">The requested types.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ConsumedTypeRegistryTests.cs</tests>
  Task MarkConsumedTypeBackfillRequestedAsync(
    IReadOnlyList<string> eventTypes,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Stream-integrity Phase A: computes the order-independent identity digests of this store's
  /// events per (tenant, type, stream). <paramref name="originServiceId"/> null = OWN emissions
  /// (locally-originated rows — what this service publishes as an origin); a value = events
  /// RECEIVED from that origin (the consumer's half of a manifest comparison). Ephemeral events
  /// (mode-excluded from the deep audit) and at-most-once occurrences are excluded, matching
  /// Phase B's counts. The computation is bounded to events older than
  /// <paramref name="settleWindow"/> so in-flight deliveries never read as divergence.
  /// Default: empty (engines without the store cannot audit).
  /// </summary>
  /// <param name="originServiceId">Null = own emissions; a value = received from that origin.</param>
  /// <param name="eventTypes">Optional type filter (the consumer restricts to subscribed types).</param>
  /// <param name="settleWindow">Only events older than this are folded (default 1 hour).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Digest rows ordered by (tenant, type, stream).</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTests.cs</tests>
  Task<IReadOnlyList<StreamDigest>> ComputeStreamDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    TimeSpan settleWindow,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StreamDigest>>([]);

  /// <summary>
  /// Stream-integrity Phase A: the TYPE-level recompute — the same fold as
  /// <see cref="ComputeStreamDigestsAsync"/> rolled up per (tenant, type), with the roll-up done
  /// AT THE STORE. A types-level answer materialized per-stream first holds one row per stream in
  /// memory (a large store's first full audit has memory-killed consumers doing exactly that);
  /// rolled up at the store, the result is bounded by #types × #tenants. The XOR of a type's
  /// stream buckets equals folding every event of the type, because the buckets partition them.
  /// Default: delegates to the per-stream compute + C# roll-up (providers without a set-based
  /// store keep the old behavior).
  /// </summary>
  /// <param name="originServiceId">Null = own emissions; a value = received from that origin.</param>
  /// <param name="eventTypes">Optional type filter (the consumer restricts to subscribed types).</param>
  /// <param name="settleWindow">Only events older than this are folded (default 1 hour).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Type-level digest rows (StreamId = <see cref="Guid.Empty"/>), ordered by (tenant, type).</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTests.cs</tests>
  async Task<IReadOnlyList<StreamDigest>> ComputeTypeDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    IntegrityDigestMath.RollUpToTypes(
      await ComputeStreamDigestsAsync(originServiceId, eventTypes, settleWindow, cancellationToken).ConfigureAwait(false));

  /// <summary>
  /// Stream-integrity Phase L: finds LOCAL coverage gaps — streams holding settled, non-ephemeral
  /// events that a registered perspective (message association) should fold, where that
  /// perspective has NO cursor on the stream and the events have no pending work items (typically:
  /// the perspective was born after the history). Repair is a LOCAL rebuild. Default: empty.
  /// </summary>
  /// <param name="settleWindow">Only events older than this count (in-flight work is not a gap).</param>
  /// <param name="maxGaps">Hard bound on returned gaps — the report cap belongs in the query
  /// (fetching thousands of rows to report a hundred is the same flood one layer down).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Distinct (stream, perspective) gaps with their settled event counts, at most
  /// <paramref name="maxGaps"/> rows.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTests.cs</tests>
  Task<IReadOnlyList<PerspectiveCoverageGap>> GetPerspectiveCoverageGapsAsync(
    TimeSpan settleWindow,
    int maxGaps,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PerspectiveCoverageGap>>([]);

  /// <summary>
  /// Claims the current integrity-audit cycle for the calling instance — the same
  /// first-instance-wins discipline the schema initializer (advisory lock) and the deep-prune
  /// watermark (settings CAS) already apply to their one-per-service work. True: this instance
  /// won and runs the cycle. False: a sibling instance ran one within <paramref name="claimWindow"/>
  /// — skip; the audit is per-SERVICE work, and every replica re-running the full-store digest
  /// recompute multiplies fleet-wide load for identical results. Default: always true
  /// (single-instance providers and engines without a settings store keep today's behavior).
  /// </summary>
  /// <param name="claimWindow">How recently a sibling's claim suppresses this one.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerTests.cs</tests>
  Task<bool> TryClaimIntegrityAuditCycleAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) => Task.FromResult(true);

  /// <summary>
  /// Claims the startup type-definition reconciliation for the calling instance — the same
  /// first-instance-wins discipline as <see cref="TryClaimIntegrityAuditCycleAsync"/>. True: this
  /// instance won and performs the walk. False: a sibling already reconciled within
  /// <paramref name="claimWindow"/> — skip entirely.
  /// <para>
  /// The walk is idempotent, so every instance running it was never incorrect — merely N times the
  /// cost for one instance's worth of result. At fleet scale that is the expensive part: a catalog
  /// walk plus a register round-trip per message type, from every replica of every service, all at
  /// once during a deploy. Measured taking a shared server from 29% CPU / 62 connections to 99% /
  /// 272, which killed pods on their liveness probes and restarted the same work.
  /// </para>
  /// Default: always true (single-instance providers and engines without a settings store keep
  /// today's behavior).
  /// </summary>
  /// <param name="claimWindow">How recently a sibling's claim suppresses this one.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeDefinitionReconcilerTests.cs:Reconcile_SecondInstanceInTheWindow_SkipsTheWholeWalkAsync</tests>
  Task<bool> TryClaimTypeDefinitionReconcileAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) => Task.FromResult(true);

  /// <summary>
  /// Records a transport body-offload claim in the ledger: the database's only record of an
  /// offloaded blob once the message completes (the claim envelope rides <c>wh_outbox</c>/
  /// <c>wh_inbox</c>, and those rows are deleted on completion). Written by the offload hook at
  /// upload time; consumed by the passive expiry sweep, which is a query over this ledger instead
  /// of a container listing. Idempotent — at-least-once dispatch may replay the upload path.
  /// Default no-op: providers without a durable ledger fall back to store-side lifecycle rules.
  /// </summary>
  /// <param name="storageKey">The provider-minted storage key from the upload's claim ticket.</param>
  /// <param name="providerName">The keyed <c>IMessageBodyStore</c> registration that holds the blob.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/messaging/body-offload</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OffloadClaimLedgerSqlTests.cs</tests>
  Task RecordOffloadClaimAsync(
    string storageKey,
    string providerName,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// The offload-claim ledger entries older than <paramref name="olderThan"/> — the passive
  /// sweep's work list. Age is evaluated against <c>uploaded_at</c> (DB clock) at query time, so a
  /// changed expiry window is retroactive over every existing blob by construction; nothing is
  /// stamped per blob. Default empty: no ledger, nothing to sweep.
  /// </summary>
  /// <param name="olderThan">The expiry window (<c>MessageBodyOffloadOptions.PassiveExpiry</c>).</param>
  /// <param name="batchSize">Upper bound per call; the sweep drains across cycles.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OffloadClaimLedgerSqlTests.cs</tests>
  Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
    TimeSpan olderThan,
    int batchSize,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<OffloadClaimRecord>>([]);

  /// <summary>
  /// Removes ledger rows whose blobs were successfully deleted (a missing blob counts —
  /// <c>IMessageBodyStore.DeleteAsync</c> is idempotent on not-found). The sweep passes ONLY the
  /// successes: a failed delete keeps its row and is retried next sweep, so the ledger row
  /// outlives the blob, never the reverse. Default no-op.
  /// </summary>
  /// <param name="storageKeys">Keys whose blob deletion succeeded.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OffloadClaimLedgerSqlTests.cs</tests>
  Task RemoveOffloadClaimsAsync(
    IReadOnlyCollection<string> storageKeys,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Claims one passive offload sweep for the calling instance — the same first-instance-wins
  /// CAS-watermark discipline as <see cref="TryClaimIntegrityAuditCycleAsync"/>, so N replicas do
  /// not issue N delete storms against the same container. Default true: a coordinator without
  /// the watermark substrate is single-instance by assumption and just runs.
  /// </summary>
  /// <param name="claimWindow">Minimum interval between sweeps service-wide.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OffloadClaimLedgerSqlTests.cs</tests>
  Task<bool> TryClaimOffloadSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) => Task.FromResult(true);

  /// <summary>
  /// The pre-destruction seam's COLLECT phase for perspective rows: the retention sweeps' DELETE
  /// predicates as a SELECT, with row payloads, limited to the given perspective model type names —
  /// exactly the set the next sweep would destroy (holds excluded, acknowledgement gate honored on
  /// the expiry side, cap overflow included regardless). Default: empty (no seam substrate).
  /// </summary>
  /// <param name="clrTypeNames">The guarded perspective models' CLR type names.</param>
  /// <param name="perTableLimit">Bound per perspective table per cycle.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
    IReadOnlyCollection<string> clrTypeNames,
    int perTableLimit = 500,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>([]);

  /// <summary>
  /// Postpones destruction of the given perspective rows until <paramref name="holdUntil"/> —
  /// a guard's Defer/Cancel made durable. Idempotent upsert; <see cref="DateTimeOffset.MaxValue"/>
  /// maps to keep-forever. Default: no-op.
  /// </summary>
  /// <param name="rows">The rows to hold.</param>
  /// <param name="holdUntil">When the hold lapses and the row is re-offered.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task HoldPerspectiveRowDestructionAsync(
    IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
    DateTimeOffset holdUntil,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Releases any holds on the given rows — a guard's Proceed after an earlier Defer. Default: no-op.
  /// </summary>
  /// <param name="rows">The rows to release.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task ReleasePerspectiveRowHoldsAsync(
    IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Records a guard failure for the given rows and applies the destruction retry ladder: under
  /// the retry cap the rows are held for the backoff and re-offered; past it the configured
  /// <see cref="Whizbang.Core.Lifecycle.OnDestroyFailure"/> policy decides (forced delete or keep).
  /// Returns the batch's highest failure count. Default: <see cref="int.MaxValue"/> (no ladder
  /// substrate ⇒ the caller treats the batch as exhausted).
  /// </summary>
  /// <param name="rows">The rows the failing guard was offered.</param>
  /// <param name="retryBackoff">Delay before the batch is re-offered.</param>
  /// <param name="maxRetries">Retry cap before the failure policy applies.</param>
  /// <param name="onDestroyFailure">The policy applied past the cap.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task<int> RecordPerspectiveRowDestructionFailureAsync(
    IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
    TimeSpan retryBackoff,
    int maxRetries,
    Whizbang.Core.Lifecycle.OnDestroyFailure onDestroyFailure,
    CancellationToken cancellationToken = default) => Task.FromResult(int.MaxValue);

  /// <summary>
  /// Runs the enrolled-perspective expiry sweep (the [RowTtl]/max-age ladder), batched. This is
  /// the sweep's production invocation — the SQL alone has no caller. Default: unsupported no-op.
  /// </summary>
  /// <param name="batchSize">Rows deleted per perspective per cycle.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(
    int batchSize = 5000,
    CancellationToken cancellationToken = default) =>
    Task.FromResult(new PerspectiveRowReapResult(0, "unsupported"));

  /// <summary>
  /// Runs the per-scope cap sweep ([RowCap] overflow eviction). Heavier than the expiry sweep
  /// (window function), intended for a slower, fleet-claimed cadence. Default: unsupported no-op.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(
    int batchSize = 5000,
    CancellationToken cancellationToken = default) =>
    Task.FromResult(new PerspectiveRowReapResult(0, "unsupported"));

  /// <summary>
  /// Claims one cap sweep for the calling instance — the same first-instance-wins CAS-watermark
  /// discipline as <see cref="TryClaimOffloadSweepAsync"/>. Default true.
  /// </summary>
  /// <param name="claimWindow">Minimum interval between cap sweeps service-wide.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task<bool> TryClaimRowCapSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) => Task.FromResult(true);

  /// <summary>
  /// Acknowledges retention enforcement for an enrolled perspective — the adoption gate's missing
  /// C# half. Until acknowledged, the expiry sweep reports what it would remove (see
  /// <see cref="CountPerspectiveRetentionBacklogAsync"/>) and removes nothing. Default: no-op.
  /// </summary>
  /// <param name="clrTypeName">The perspective model's CLR type name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task AcknowledgeRetentionEnforcementAsync(
    string clrTypeName,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// Counts what retention enforcement WOULD remove for an enrolled perspective, without removing
  /// it — the number an operator reads before acknowledging. Default: 0.
  /// </summary>
  /// <param name="clrTypeName">The perspective model's CLR type name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
  Task<long> CountPerspectiveRetentionBacklogAsync(
    string clrTypeName,
    CancellationToken cancellationToken = default) => Task.FromResult(0L);

  /// <summary>
  /// Atomically claims a batch of journaled origin evictions (what the row sweeps destroyed) for
  /// group-cascade processing — DELETE ... RETURNING, so N replicas never double-cascade.
  /// Default: empty.
  /// </summary>
  /// <param name="limit">Maximum journal entries claimed this cycle.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
  Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowRef>> DrainRowEvictionJournalAsync(
    int limit = 1000,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowRef>>([]);

  /// <summary>
  /// Re-queues origin evictions whose cascade was deferred (a guard held a cascaded row), so the
  /// next cycle recomputes the closure and re-offers. Idempotent. Default: no-op.
  /// </summary>
  /// <param name="rows">The origin (table, row) pairs to re-journal.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
  Task RequeueRowEvictionsAsync(
    IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  /// <summary>
  /// The (CLR type name, table name) pairs for the given perspective models, from the registry.
  /// The cascade uses it to map journal entries to group members and back. Default: empty.
  /// </summary>
  /// <param name="clrTypeNames">The perspective models to resolve.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
  Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
    IReadOnlyCollection<string> clrTypeNames,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<PerspectiveTableName>>([]);

  /// <summary>
  /// Loads specific perspective rows as destruction targets (reason <c>cascade</c>) so a guard can
  /// be offered cascaded rows with their payloads, exactly like sweep-selected ones. Default: empty.
  /// </summary>
  /// <param name="clrTypeName">The perspective model's CLR type name.</param>
  /// <param name="tableName">The perspective's physical table.</param>
  /// <param name="rowIds">The rows to load.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
  Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>> GetPerspectiveRowsByIdsAsync(
    string clrTypeName,
    string tableName,
    IReadOnlyCollection<Guid> rowIds,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>([]);

  /// <summary>
  /// Executes one member table's share of the group cascade: hold-aware DELETE of the given rows
  /// (a guard's Defer survives it). Returns the count destroyed. Default: 0.
  /// </summary>
  /// <param name="tableName">The member perspective's physical table.</param>
  /// <param name="rowIds">The rows the closure marked for eviction.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
  Task<int> CascadeDeletePerspectiveRowsAsync(
    string tableName,
    IReadOnlyCollection<Guid> rowIds,
    CancellationToken cancellationToken = default) => Task.FromResult(0);

  /// <summary>
  /// Folds the given streams' apply paths (version-ordered event-type sequences, run-length
  /// collapsed) into the persisted signature counts — call exactly once per stream, immediately
  /// before destroying its pointers. The stream dies; its shape survives. Default: 0.
  /// </summary>
  /// <param name="streamIds">The streams about to lose their pointers.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyPathFoldSqlTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamCloserFoldOrderTests.cs:Close_FoldsTheApplyPath_BeforeTheTruncateAsync</tests>
  Task<int> FoldStreamApplyPathsAsync(
    IReadOnlyCollection<Guid> streamIds,
    CancellationToken cancellationToken = default) => Task.FromResult(0);

  /// <summary>
  /// Folds SETTLED streams — idle past the window, not yet folded — into the persisted apply-path
  /// signatures. Non-destructive and bounded; the watermark makes it fold-exactly-once by
  /// mechanism. Default: 0.
  /// </summary>
  /// <param name="idleWindow">How long a stream must be idle to count as settled.</param>
  /// <param name="limit">Streams folded per invocation.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyPathFoldSqlTests.cs</tests>
  Task<int> FoldSettledApplyPathsAsync(
    TimeSpan idleWindow,
    int limit = 1000,
    CancellationToken cancellationToken = default) => Task.FromResult(0);

  /// <summary>
  /// Claims one settled-fold sweep for the calling instance — the same first-instance-wins
  /// CAS-watermark discipline as <see cref="TryClaimRowCapSweepAsync"/>. Default true.
  /// </summary>
  /// <param name="claimWindow">Minimum interval between settled folds service-wide.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyPathFoldSqlTests.cs</tests>
  Task<bool> TryClaimSettledFoldSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) => Task.FromResult(true);

  /// <summary>
  /// The staged rebuild's presence reconcile: deletes the follower table's rows whose id is absent
  /// from EVERY announcer table (the conservative all-absent rule — announcer live tables
  /// materialize past eviction decisions the journal can no longer re-fire). Hold-aware. Returns
  /// the count removed. Default: 0.
  /// </summary>
  /// <param name="followerTable">The rebuilt follower perspective's physical table.</param>
  /// <param name="announcerTables">Its groups' announcer tables.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>proposals/pre-destruction-seam</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
  Task<int> ReconcileFollowerPresenceAsync(
    string followerTable,
    IReadOnlyCollection<string> announcerTables,
    CancellationToken cancellationToken = default) => Task.FromResult(0);

  /// <summary>
  /// Stream-integrity Phase B: the DISTINCT event types this service has ever emitted into its own
  /// audited lane (the own-emissions digest rows). The checkpoint publisher fans its heartbeat out
  /// to these types' topics — the topics this origin's consumers already subscribe to — so a quiet
  /// period (no new emissions in the window) still heartbeats every historically-covered topic.
  /// Default: empty (engines without the digest table publish through the dispatcher fallback).
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Distinct stored event-type names from the own-emissions digest lane.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTests.cs</tests>
  Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<string>>([]);

  /// <summary>
  /// Stream-integrity A1c: reads the incrementally-maintained per-(tenant, type, stream) digests
  /// from <c>wh_stream_digests</c> — O(buckets) instead of a store-wide recompute. Same origin
  /// semantics as <see cref="ComputeStreamDigestsAsync"/> (null = own emissions; a value = events
  /// received from that origin). Rows carry <see cref="StreamDigest.UpdatedAt"/> so compare-time
  /// settle-skipping replaces the recompute's created-at settle filter. Default: empty (engines
  /// without the digest table cannot serve table-driven audits — callers fall back to recompute).
  /// </summary>
  /// <param name="originServiceId">Null = own emissions; a value = received from that origin.</param>
  /// <param name="eventTypes">Optional type filter.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Digest rows ordered by (tenant, type, stream), with bucket update times.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs</tests>
  Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StreamDigest>>([]);

  /// <summary>
  /// Stream-integrity A1c: per-(tenant, type) digest roll-ups from <c>wh_stream_digests</c> —
  /// XOR of the type's stream buckets (valid because they partition the type's events) with
  /// summed counts and the newest bucket update time. The wire unit of the hierarchical audit:
  /// O(types) instead of O(streams) per exchange; mismatched types drill down to stream level.
  /// Rows carry <see cref="StreamDigest.StreamId"/> = <see cref="Guid.Empty"/>. Default: empty.
  /// </summary>
  /// <param name="originServiceId">Null = own emissions; a value = received from that origin.</param>
  /// <param name="eventTypes">Optional type filter.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Type-level roll-ups ordered by (tenant, type).</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs</tests>
  Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StreamDigest>>([]);

  /// <summary>
  /// Stream-integrity A1c: the trust-but-verify sweep — reconciles the incrementally-maintained
  /// digest table against a full recompute and HEALS it: drifted buckets updated in place,
  /// phantom buckets removed, missing buckets added. Only settled state participates (buckets
  /// updated inside <paramref name="settleWindow"/> and events created inside it are ignored) so
  /// in-flight folds never read as drift. Non-zero drift means an unaccounted write path touched
  /// audited rows — the caller alarms. Default: zeros (nothing to verify).
  /// </summary>
  /// <param name="settleWindow">Buckets/events younger than this are ignored.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Checked/healed bucket counts.</returns>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDigestTableSqlTests.cs</tests>
  Task<DigestVerificationResult> VerifyDigestTableAsync(
    TimeSpan settleWindow,
    CancellationToken cancellationToken = default) => Task.FromResult(new DigestVerificationResult {
      BucketsChecked = 0,
      DriftUpdated = 0,
      DriftRemoved = 0,
      DriftAdded = 0,
    });
  /// <summary>
  /// Tables whose heap is disproportionate to their live rows, re-measured at call time.
  /// </summary>
  /// <remarks>
  /// Space a table holds but cannot use costs on every read — index heap-fetches pull emptier
  /// pages and the buffer cache holds fewer useful rows. Churn is the usual cause: autovacuum
  /// reclaims deleted rows to the free space map for reuse but never returns them to the OS, so
  /// the file stays large. A dropped column is the other, and autovacuum can never reclaim that
  /// at all. Only a table rewrite recovers either.
  /// </remarks>
  Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<TableRewriteCandidate>>([]);

  /// <summary>
  /// Rewrites a table to reclaim unusable space, returning the ratio afterwards so the caller can
  /// confirm it worked. Takes an ACCESS EXCLUSIVE lock for the duration.
  /// </summary>
  Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) =>
    Task.FromResult<double?>(null);

  /// <summary>Clears a table's recorded rewrite request. Call only after verifying success.</summary>
  Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// Records that a table owes a rewrite, exactly as a migration does. The runtime maintenance
  /// cycle calls this when it detects bloat instead of taking an ACCESS EXCLUSIVE lock
  /// mid-traffic — the post-ready <c>Rewrite</c> startup step performs the recorded rewrites in
  /// the window they should always have had. Idempotent per table.
  /// </summary>
  Task RequestTableRewriteAsync(string tableName, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// Records this instance's lifecycle phase (and, when known, library version) on its own
  /// instance row, so peers and the status surface can observe it — the standby handshake cannot
  /// wait for a state nobody can see. Returns false when the instance has no row yet (early
  /// startup transitions precede the first heartbeat — expected, not an error). Must not refresh
  /// liveness: state is not a heartbeat, and a standing-by zombie must still be reapable.
  /// </summary>
  Task<bool> RecordInstanceStateAsync(
      Guid instanceId, string lifecyclePhase, string? libraryVersion = null,
      CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>
  /// Records the single fleet-wide standby request: this instance asking live older peers to
  /// drain and stand by before a breaking migration. True when this instance now holds the
  /// active request (first claim or idempotent re-request); false when another instance's
  /// request is active or this instance is evicted.
  /// </summary>
  Task<bool> RequestStandbyAsync(Guid instanceId, string version, CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>Withdraws this instance's own standby request. Only the requester clears —
  /// a dead requester's request is voided by peers watching its liveness, never by deletion.</summary>
  Task<bool> ClearStandbyRequestAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>The active standby request, with the requester's last heartbeat so peers can bound
  /// their wait by the requester's liveness rather than its goodwill.</summary>
  Task<StandbyRequest?> GetStandbyRequestAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult<StandbyRequest?>(null);

  /// <summary>
  /// The deliberate fence: tombstones an instance so its heartbeats, capability acquisitions and
  /// work claims are refused. Taken by the migrator during a breaking handshake, or by an
  /// operator — never an automatic consequence of slowness. Records who issued it and why.
  /// </summary>
  Task EvictInstanceAsync(Guid instanceId, Guid evictedBy, string reason, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// Durable stream-integrity convergence state. Defaults keep the caller's prior behaviour when a
  /// provider cannot store it: reporting proceeds (over-reporting is recoverable) and repair does
  /// not (an unbounded repair request against real data is not).
  /// </summary>
  Task<bool> IntegrityTryBeginReportAsync(
      IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    Task.FromResult(true);

  /// <inheritdoc cref="IntegrityTryBeginReportAsync"/>
  Task<bool> IntegrityTryBeginRepairAsync(
      IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>Forgets a bucket that folded identical.</summary>
  Task IntegrityMarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// Batched <see cref="IntegrityTryBeginReportAsync"/> — one round trip for a manifest chunk's
  /// report decisions (element i answers observation i). Null = unsupported; the caller loops the
  /// singles. All observations share one origin (a manifest chunk is per-origin by construction).
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  Task<IReadOnlyList<bool>?> IntegrityTryBeginReportBatchAsync(
    Guid originServiceId, IReadOnlyList<IntegrityReportObservation> observations,
    DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<bool>?>(null);

  /// <summary>
  /// Batched <see cref="IntegrityTryBeginRepairAsync"/>, capped at <paramref name="maxGrants"/>
  /// in order — past the cap keys are not consulted (a discarded grant burns attempt budget).
  /// Null = unsupported.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  Task<IReadOnlyList<bool>?> IntegrityTryBeginRepairBatchAsync(
    Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
    DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<bool>?>(null);

  /// <summary>
  /// Stamps the compared window ([from, until] origin commit sequences) onto the keyed ledger
  /// rows — discovery-time context the paced repair drain later dispatches with, so a repair
  /// asks for exactly the slice that disagreed without the in-flight manifest. No-op = unsupported.
  /// </summary>
  /// <docs>proposals/paced-repair-drain</docs>
  Task IntegrityStampRepairWindowsAsync(
    Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
    long windowFrom, long windowUntil, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// Atomically claims up to <paramref name="limit"/> repair-eligible ledger rows for the paced
  /// drain: past their exponential backoff, under <paramref name="maxAttempts"/>, restricted to
  /// <paramref name="originIds"/> (origins whose request topic is learned — nothing else could be
  /// sent), least-recently-attempted first, never the synthetic bulk lane. A claim stamps the
  /// attempt exactly like a burst-path grant, and concurrent drainers skip each other's locked
  /// rows, so a bucket is never double-dispatched. Empty = nothing eligible or unsupported.
  /// </summary>
  /// <docs>proposals/paced-repair-drain</docs>
  Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
    IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
    int limit, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>([]);

  /// <summary>Batched <see cref="IntegrityMarkHealedAsync"/>. False = unsupported.</summary>
  /// <docs>resilience/stream-integrity</docs>
  Task<bool> IntegrityMarkHealedBatchAsync(
    Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
    CancellationToken cancellationToken = default) =>
    Task.FromResult(false);

  /// <summary>
  /// <see cref="IntegrityMarkHealedBatchAsync"/>, additionally returning each healed bucket's age
  /// in seconds (first sighting → heal) read from the rows the delete destroys — the per-stream
  /// time-to-reconcile at zero extra work. Null = unsupported (fall back to the ageless batch or
  /// singles); an empty list is a real answer (no tracked bucket matched).
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  Task<IReadOnlyList<double>?> IntegrityMarkHealedBatchWithAgesAsync(
    Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<double>?>(null);

  /// <summary>
  /// Reads the ledger as a gauge: unhealed buckets, how many have spent their repair budget, and
  /// the age of the oldest. Defaults to "nothing to report" for engines with no ledger.
  /// </summary>
  /// <param name="maxRepairAttempts">The repair budget, so the query can count who has spent it.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  Task<Observability.LedgerGaugeSnapshot> GetIntegrityLedgerSummaryAsync(
    int maxRepairAttempts, CancellationToken cancellationToken = default) =>
    Task.FromResult(Observability.LedgerGaugeSnapshot.Empty);

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
  /// Advances the digest-epoch closure frontier (migration 092): folds every closable epoch —
  /// settled max beyond it AND no unsettled event inside its range — into immutable
  /// <c>wh_digest_epochs</c> bucket rows, up to <paramref name="maxEpochs"/> across all lanes.
  /// Called from the maintenance cycle; the epoch substrate is inert without this.
  /// </summary>
  /// <param name="settleSeconds">The settle window — MUST equal the audit's
  /// (<see cref="StreamIntegrityOptions.AuditSettleWindowMinutes"/> in seconds), or a seal could
  /// disagree with a manifest folded over the same range.</param>
  /// <param name="maxEpochs">Cap on epochs closed this call, bounding the cycle's recompute work.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// Epochs closed, or <c>-1</c> for engines without the substrate. The sentinel is deliberate:
  /// this is a default interface method, and a default that returned a plausible <c>0</c> would
  /// make a missed override look exactly like a healthy idle system (the same silent-fallback
  /// hazard the byte-budget overload documented).
  /// </returns>
  /// <docs>resilience/stream-integrity</docs>

  Task<int> CloseDigestEpochsAsync(
    int settleSeconds, int maxEpochs, CancellationToken cancellationToken = default) =>
    Task.FromResult(-1);

  /// <summary>
  /// The lane's SETTLED maximum sequence — the watermark ceiling for negotiated-scope answers
  /// (#80-B). An answer must never claim coverage of an unsettled sequence, or the asker could
  /// seal over an in-flight delivery. Null = unsupported (engines without sequence lanes) or
  /// nothing settled; callers treat both as "cannot window".
  /// </summary>
  /// <param name="originServiceId">Null = the local lane (own emissions); a value = the received
  /// lane for that origin, measured on the ORIGIN's sequence.</param>
  /// <param name="settleWindow">Only events older than this count.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<long?> GetIntegritySettledMaxAsync(
    Guid? originServiceId, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
    Task.FromResult<long?>(null);

  /// <summary>
  /// Negotiated-scope type-level digest read (#80-B): folds only the window
  /// <c>[sinceSequence, untilSequence)</c>. Epochs fully inside the window contribute their
  /// SEALED fold (authoritative); partially covered epochs fold live over just the covered
  /// fringe — a seal is indivisible. <see cref="WindowedDigestResult.ComputedThrough"/> is the
  /// exclusive end actually covered, capped at the settled max. Null return = the engine cannot
  /// window (no substrate) — the caller answers unwindowed rather than silently wrong.
  /// </summary>
  /// <param name="originServiceId">Null = the local lane; a value = that origin's received lane.</param>
  /// <param name="eventTypes">Types to fold (null = all).</param>
  /// <param name="sinceSequence">Inclusive window start — the asker's current watermark.</param>
  /// <param name="untilSequence">Exclusive window end; null = through the settled max.</param>
  /// <param name="settleWindow">Only events older than this count.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<WindowedDigestResult?> ComputeTypeDigestsWindowedAsync(
    Guid? originServiceId, IReadOnlyList<string>? eventTypes,
    long sinceSequence, long? untilSequence, TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<WindowedDigestResult?>(null);

  /// <summary>
  /// Negotiated-scope stream-level digest read (#80-B): the drill-down granularity, bounded on
  /// BOTH cursor dimensions — the sequence window <c>[sinceSequence, untilSequence)</c> and a
  /// stream-id page (<paramref name="resumeAfterStreamId"/> + <paramref name="maxDigests"/>).
  /// A non-null <see cref="WindowedDigestResult.ResumeAfterStreamId"/> in the result means the
  /// window is NOT complete: ask again from there, and never advance a seal past a partial
  /// window. Null return = the engine cannot window.
  /// </summary>
  /// <param name="originServiceId">Null = the local lane; a value = that origin's received lane.</param>
  /// <param name="eventTypes">Types to fold (null = all).</param>
  /// <param name="sinceSequence">Inclusive window start — the asker's current watermark.</param>
  /// <param name="untilSequence">Exclusive window end; null = through the settled max.</param>
  /// <param name="resumeAfterStreamId">Page start: only streams ABOVE this id (null = from the first).</param>
  /// <param name="maxDigests">The asker's page bound — whole streams are paged, never split.</param>
  /// <param name="settleWindow">Only events older than this count.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<WindowedDigestResult?> ComputeStreamDigestsWindowedAsync(
    Guid? originServiceId, IReadOnlyList<string>? eventTypes,
    long sinceSequence, long? untilSequence, Guid? resumeAfterStreamId, int maxDigests,
    TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
    Task.FromResult<WindowedDigestResult?>(null);

  /// <summary>
  /// The CHUNK-BOUNDED local fold for stream-level manifest COMPARISON: digests for exactly
  /// <paramref name="streamIds"/> on the origin's received lane, optionally limited to the
  /// half-open window <c>[sinceSequence, untilSequence)</c> (both null = full history). The local
  /// side of a chunk comparison only ever needs the streams the chunk names — observed live,
  /// folding the whole lane (or the whole window) to check one 500-stream chunk OOM-crashlooped
  /// an entire fleet within seconds of each audit. Memory is bounded by the chunk size no matter
  /// how large the lane is. Null return = unsupported (callers fall back to the legacy paths).
  /// </summary>
  /// <param name="originServiceId">The origin whose received lane is compared.</param>
  /// <param name="streamIds">The manifest chunk's stream set (bounded by the origin's chunk size).</param>
  /// <param name="sinceSequence">Inclusive window start on the origin's sequence; null = no floor.</param>
  /// <param name="untilSequence">Exclusive window end; null = no ceiling.</param>
  /// <param name="settleWindow">Only events older than this count — matching the answer side.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
    Guid originServiceId, IReadOnlyList<Guid> streamIds,
    long? sinceSequence, long? untilSequence, TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<StreamDigest>?>(null);

  /// <summary>
  /// The consumer's verified watermark for an origin (#80-C): every sequence below it proved
  /// clean in a past complete-window audit, so steady-state audits start here instead of zero —
  /// what stops verified history from being re-shipped and re-verified forever. Default 0 =
  /// nothing verified (engines without the seal store audit from the beginning every time).
  /// </summary>
  /// <param name="originServiceId">The origin the seal is against.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<long> GetIntegritySealAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
    Task.FromResult(0L);

  /// <summary>
  /// Advances the seal after a window proved clean AND complete (every bucket matched, one chunk,
  /// no resume cursor). Monotonic — a late or replayed advance can only move it forward. Default
  /// no-op for engines without the seal store.
  /// </summary>
  /// <param name="originServiceId">The origin the seal is against.</param>
  /// <param name="through">The verified window's exclusive end — the next audit's start.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task AdvanceIntegritySealAsync(Guid originServiceId, long through, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <summary>
  /// #80-D: the sweep's SEAL backstop — recomputes each closed digest epoch from the store,
  /// compares bucket-for-bucket, and refolds on drift. Epochs holding an unsettled arrival are
  /// skipped whole (verifying now would fold an in-flight delivery into a seal). Default: nothing
  /// checked, for engines without the epoch substrate.
  /// </summary>
  /// <param name="settleWindow">The settle window — an arrival younger than this blocks its epoch.</param>
  /// <param name="maxEpochs">Cap on epochs recomputed this call.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<EpochVerificationResult> VerifyDigestEpochsAsync(
    TimeSpan settleWindow, int maxEpochs, CancellationToken cancellationToken = default) =>
    Task.FromResult(new EpochVerificationResult(0, 0));

  /// <summary>
  /// #80-F: this origin's history generation — bumped by the two legitimate fold-mutation sites
  /// (close-the-books truncation, reclassification) and stamped on every manifest answer so
  /// consumers can distinguish deliberate change from damage. Default 0 for engines without the
  /// generation store.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<long> GetIntegrityOriginGenerationAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(0L);

  /// <summary>
  /// #80-F: the consumer-side generation guard, one atomic call. True = the carried generation
  /// matches the stored one (or first contact) — compare away. False = the origin's history
  /// legitimately moved: the seal was reset to zero, the new generation recorded, and the caller
  /// must SKIP this comparison round (its windows were aligned to the old world). The reset
  /// happens once per generation change. Default true (no store — proceed as before).
  /// </summary>
  /// <param name="originServiceId">The origin whose generation the manifest carried.</param>
  /// <param name="generation">The carried generation.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity</docs>
  Task<bool> EnsureIntegritySealGenerationAsync(
    Guid originServiceId, long generation, CancellationToken cancellationToken = default) =>
    Task.FromResult(true);

  /// <summary>
  /// A1 (Archival &amp; Compaction) — "close the books" on a durable Sourced stream: truncate the detail at or
  /// below <paramref name="throughVersion"/> once the CONSUMPTION GATE holds (every perspective has processed
  /// every event at/below the close point) AND a CARRY-FORWARD event survives above it (the domain's closing
  /// event / new origin). Discard-only in increment 1 (cold-storage archive is a later increment). The domain
  /// appends its closing event BEFORE calling this. Default: unsupported no-op (engines without the primitive).
  /// </summary>
  /// <param name="streamId">The stream to close.</param>
  /// <param name="throughVersion">The inclusive per-stream version below which detail is truncated.</param>
  /// <param name="archive">
  /// When <see langword="true"/>, the detail is copied to the cold-storage archive (<c>wh_event_archive</c>,
  /// retrievable via <see cref="GetArchivedEventsAsync"/>) BEFORE the truncate, atomically. When
  /// <see langword="false"/> (default), the detail is discarded.
  /// </param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// A status (<c>closed</c> | <c>blocked</c> | <c>no_carry_forward</c> | <c>debug_skipped</c> |
  /// <c>unsupported</c>) plus the number of events truncated.
  /// </returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<StreamCloseResult> CloseStreamAsync(
    Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) =>
    Task.FromResult(new StreamCloseResult("unsupported", 0));

  /// <summary>
  /// A1 — read the archived detail of a closed stream from cold storage (<c>wh_event_archive</c>), ordered by
  /// version. This is the retrieval side of an archiving close (<see cref="CloseStreamAsync"/> with
  /// <c>archive: true</c>): the period's raw events, preserved out of the hot store for audit / full replay.
  /// Default: empty (engines without the archive store).
  /// </summary>
  /// <param name="streamId">The stream whose archived detail to read.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The archived events for the stream, ordered by version.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<IReadOnlyList<ArchivedEvent>> GetArchivedEventsAsync(
    Guid streamId, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<ArchivedEvent>>([]);

  /// <summary>
  /// A1 — the distinct perspective names that consume any event in <paramref name="streamId"/> at or below
  /// <paramref name="throughVersion"/> (via the message associations of those events' types). Used by
  /// <see cref="Whizbang.Core.Lifecycle.IStreamCloser"/> to decide whether a discard-close would strand a
  /// <see cref="Whizbang.Core.Attributes.FullHistoryAttribute"/> projection. Default: empty.
  /// </summary>
  /// <param name="streamId">The stream being closed.</param>
  /// <param name="throughVersion">The inclusive per-stream version below which detail would be truncated.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The distinct consuming perspective names.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<IReadOnlyList<string>> GetConsumingPerspectiveNamesAsync(
    Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<string>>([]);

  /// <summary>
  /// E3 — the per-stream version of the event with id <paramref name="eventId"/>, or <c>null</c> if unknown.
  /// A Tier-2 compaction closes an ephemeral stream through the version of the snapshot's anchor event, so this
  /// maps that anchor event id to the close point. Default: <c>null</c>.
  /// </summary>
  /// <param name="eventId">The event id to look up.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The event's per-stream version, or <c>null</c>.</returns>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  Task<long?> GetEventVersionAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    Task.FromResult<long?>(null);

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
  /// Gives a broker-dead-lettered message durable custody as a <c>wh_dead_letters</c> row
  /// (<c>source_table='broker'</c>, <see cref="MessageFailureReason.BrokerDeadLetter"/>), storing
  /// the RAW wire body verbatim — no deserialization. Idempotent on the wire message id:
  /// <c>true</c> = custody row created; <c>false</c> = duplicate (custody already exists — the
  /// caller may settle the broker message). A FAILED import throws instead of returning
  /// <c>false</c>, so callers can distinguish "safe to settle" from "leave it for the next pass".
  /// The default implementation THROWS <see cref="NotSupportedException"/> — a legacy/in-memory
  /// coordinator cannot give custody, and returning <c>false</c> here would read as "duplicate,
  /// settle at the broker" and silently lose the message.
  /// </summary>
  /// <docs>operations/dead-letter-queue/transport-recovery</docs>
  Task<bool> ImportBrokerDeadLetterAsync(
      Whizbang.Core.Transports.BrokerDeadLetterImport import,
      CancellationToken cancellationToken = default) =>
    throw new NotSupportedException(
      "This IWorkCoordinator does not support broker dead-letter import; messages stay on the broker DLQ.");

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
  /// Byte-budgeted outbox fetch: bounds the slice by payload bytes as well as row count — the
  /// outbox sibling of the byte-budgeted <c>FetchInboxBatchAsync</c> overload below, and shaped
  /// as a separate overload for the same reason (see that overload's remarks: changing a
  /// defaulted-interface-method signature silently un-implements it for every existing
  /// implementer). The default delegates to the count-only overload, so an engine that has not
  /// implemented this keeps its previous behavior (unbounded by bytes).
  /// </summary>
  /// <param name="streamIds">Streams to drain.</param>
  /// <param name="instanceId">The claiming instance.</param>
  /// <param name="maxPerStream">Row cap per stream.</param>
  /// <param name="maxBytes">Payload-byte cap per stream; null disables it.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
  Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream,
    long? maxBytes,
    CancellationToken cancellationToken = default)
    => FetchOutboxBatchAsync(streamIds, instanceId, maxPerStream, cancellationToken);

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
  /// Byte-budgeted fetch: bounds the slice by payload bytes as well as row count.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A separate overload rather than a parameter on the one above, deliberately. Changing the
  /// signature of a method that carries a default implementation SILENTLY un-implements it for
  /// every existing implementer — their method stops matching the interface, the class still
  /// compiles, and every call quietly falls through to the default. Doing exactly that here broke
  /// nineteen drain tests with empty results and no diagnostic, and would have done the same to
  /// any consumer's own coordinator.
  /// </para>
  /// <para>
  /// The default delegates to the count-only overload, so an engine that has not implemented this
  /// keeps its previous behavior (unbounded by bytes) instead of silently returning nothing.
  /// </para>
  /// </remarks>
  /// <param name="streamIds">Streams to drain.</param>
  /// <param name="instanceId">The claiming instance.</param>
  /// <param name="maxPerStream">Row cap per stream.</param>
  /// <param name="maxBytes">Payload-byte cap per stream; null disables it.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream,
    long? maxBytes,
    CancellationToken cancellationToken = default)
    => FetchInboxBatchAsync(streamIds, instanceId, maxPerStream, cancellationToken);

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
  /// A production forensic investigation exposed a class of bug — silent stuck rows — that bypasses
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

  /// <summary>
  /// Per-group statistics over coalesce-pending outbox rows
  /// (<c>coalesce_group IS NOT NULL AND processed_at IS NULL</c>) — the coalesce shipper's
  /// per-tick view for its quiet-window / max-delay firing decisions. Served by the
  /// worker index (<c>idx_outbox_coalesce_pending</c>); only pending singles ever live in it.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Stats per pending group; empty when nothing is pending (or for non-SQL fakes).</returns>
  /// <docs>fundamentals/messages/message-tags#coalescing</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoalesceFoldCoordinatorSqlTests.cs:GetPendingCoalesceGroupStats_ReturnsPerGroupCountsAndAgesAsync</tests>
  Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default)
    // Default no-op for test fakes and stores without coalescing support.
    => Task.FromResult<IReadOnlyList<CoalesceGroupStats>>([]);

  /// <summary>
  /// Fetches up to <paramref name="limit"/> pending singles for <paramref name="group"/>,
  /// oldest first, as full <see cref="OutboxMessage"/> rows. SQL implementations use
  /// <c>FOR UPDATE SKIP LOCKED</c> so two shippers folding the same group at the same instant
  /// partition the rows instead of colliding. The residual fetch→complete race window is
  /// tolerated by design: composites are identity-preserving, so a double-folded single
  /// dedups at the consumer's inbox rather than double-delivering.
  /// </summary>
  /// <param name="group">The coalesce group (tag string).</param>
  /// <param name="limit">Maximum rows to fetch (the binding's MaxBatchCount).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The fetched pending singles; empty when the group has drained.</returns>
  /// <docs>fundamentals/messages/message-tags#coalescing</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoalesceFoldCoordinatorSqlTests.cs:FetchPendingCoalesce_ReturnsOldestFirstUpToLimitAsync</tests>
  Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(string group, int limit, CancellationToken cancellationToken = default)
    // Default no-op for test fakes and stores without coalescing support.
    => Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

  /// <summary>
  /// Completes one fold IN ONE TRANSACTION: inserts <paramref name="compositeMessages"/> as
  /// immediately-shippable outbox rows (via the store seam, so the doorbell and partition
  /// stamping behave exactly as any store) and marks the <paramref name="foldedIds"/> singles
  /// processed. Crash-safety falls out of the transaction: a single is either still pending
  /// (floor intact) or folded (composite exists) — never both, never neither.
  /// </summary>
  /// <param name="foldedIds">Message ids of the singles this fold bundles.</param>
  /// <param name="compositeMessages">The built composite outbox message(s).</param>
  /// <param name="partitionCount">Partition count for the store seam.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>fundamentals/messages/message-tags#coalescing</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoalesceFoldCoordinatorSqlTests.cs:CompleteCoalesceFold_InsertsCompositeAndCompletesSinglesAtomicallyAsync</tests>
  Task CompleteCoalesceFoldAsync(
    IReadOnlyList<Guid> foldedIds,
    OutboxMessage[] compositeMessages,
    int partitionCount,
    CancellationToken cancellationToken = default)
    // Default no-op for test fakes and stores without coalescing support.
    => Task.CompletedTask;

  /// <summary>
  /// The deadline-degrade release: clears <c>coalesce_group</c> and <c>scheduled_for</c> on
  /// <paramref name="group"/>'s rows whose floor has matured
  /// (<c>scheduled_for &lt;= NOW() AND processed_at IS NULL</c>), moving them into the
  /// eligible-scan index so the normal pump ships them individually. Run once on shipper
  /// startup (recovery) and each tick as a backstop — degraded is slower, never lost, and the
  /// transition is explicit and counted, never a silent query union.
  /// </summary>
  /// <param name="group">The coalesce group (tag string).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The number of rows released.</returns>
  /// <docs>fundamentals/messages/message-tags#coalescing</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoalesceFoldCoordinatorSqlTests.cs:ReleaseMaturedCoalesce_ReleasesOnlyMaturedRows_AndClaimShipsThemAsync</tests>
  Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default)
    // Default no-op for test fakes and stores without coalescing support.
    => Task.FromResult(0);

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
/// <tests>tests/Whizbang.Core.Tests/Messaging/CoordinatorRecordSurfaceTests.cs:RewindCursorInfo_PositionalCtor_RoundTripsAllValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CoordinatorRecordSurfaceTests.cs:RewindCursorInfo_NullsAreAllowedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerStartupAndMaintenanceTests.cs:Startup_RewindScanBlockingMode_RepollsUntilNoRewindCursorsRemainAsync</tests>
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
/// One perspective's row-retention declaration, carried from the C# registry into the perspective
/// registry so the reaper can sweep only enrolled perspectives.
/// </summary>
/// <remarks>
/// Enrollment and duration are separate: an enrolled perspective with both windows null is swept
/// but has no default rule, so its rows expire only by an explicitly assigned expiry. Null stays
/// distinct from zero, which would mean expire-immediately.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
public sealed record PerspectiveRetentionDeclaration(
  string ClrTypeName,
  bool Enrolled,
  int? TtlSeconds,
  int? MaxAgeSeconds,
  int? CapPerScope = null,
  string? CapScopeKey = null);

/// <summary>
/// One offload-claim ledger entry: the storage key of a transport-offloaded blob and the keyed
/// store that holds it. The passive sweep resolves the provider per claim, deletes the blob, and
/// removes the row only on success.
/// </summary>
/// <docs>fundamentals/messaging/body-offload</docs>
public sealed record OffloadClaimRecord(string StorageKey, string ProviderName);

/// <summary>Outcome of one perspective-row retention sweep invocation.</summary>
/// <param name="RowsAffected">Rows destroyed by the sweep.</param>
/// <param name="Status">The sweep's status string (<c>ok</c>, <c>skipped (debug_mode=true)</c>, <c>unsupported</c>, …).</param>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowDestructionSeamSqlTests.cs</tests>
public sealed record PerspectiveRowReapResult(int RowsAffected, string Status);

/// <summary>A perspective model's registry identity: its CLR type name and physical table.</summary>
/// <param name="ClrTypeName">The model's CLR type name (the registry key).</param>
/// <param name="TableName">The perspective's physical table.</param>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamGroupCascadeSqlTests.cs</tests>
public sealed record PerspectiveTableName(string ClrTypeName, string TableName);

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
/// An event read back from the A1 cold-storage archive (<c>wh_event_archive</c>) via
/// <see cref="IWorkCoordinator.GetArchivedEventsAsync"/> — the preserved detail of a closed stream. Carries
/// identity + type + the raw body/metadata JSON (typed-envelope rehydration into a rebuild is a later phase).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record ArchivedEvent(
  Guid EventId, Guid StreamId, long Version, string EventType, string? EventDataJson, string? MetadataJson);

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
  /// <see cref="Whizbang.Core.Minting.ICompositeEvent"/>) that the
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

  /// <summary>
  /// The coalesce group this message is pending under, or null (default) for a normal
  /// immediately-shippable message. Stamped at the mint seams (see
  /// <see cref="Whizbang.Core.Tags.CoalesceGroupResolver"/>) when the message's type carries a
  /// tag with an enabled coalesce binding, always together with the
  /// <see cref="ScheduledFor"/> max-delay floor. Persisted to <c>wh_outbox.coalesce_group</c>;
  /// rows with a non-null group are excluded from the claim path's eligible scan by index
  /// predicate until a coalesce worker folds them into a composite (marking them processed) or
  /// releases them (group and floor cleared) at the deadline.
  /// </summary>
  /// <docs>fundamentals/messages/message-tags#coalescing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Tags/CoalesceGroupResolverTests.cs:Apply_BoundTag_StampsGroupAndMaxDelayFloorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CoalesceMintStampingTests.cs:AddOutboxMessage_BoundTag_StampsGroupAndFloorAsync</tests>
  public string? CoalesceGroup { get; init; }
}
/// <summary>
/// Per-group view over coalesce-pending outbox rows, returned by
/// <see cref="IWorkCoordinator.GetPendingCoalesceGroupStatsAsync"/>. The coalesce shipper
/// fires a fold when the group has gone quiet (<see cref="NewestCreatedAt"/> older than the
/// binding's SlideSeconds) or overdue (<see cref="OldestCreatedAt"/> older than
/// MaxDelaySeconds).
/// </summary>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/CoalesceShipWorkerTests.cs:RunOnce_GroupQuiet_FoldsAsync</tests>
public sealed record CoalesceGroupStats {
  /// <summary>The coalesce group (tag string).</summary>
  public required string Group { get; init; }

  /// <summary>Pending (unprocessed, still-grouped) singles in the group.</summary>
  public required long PendingCount { get; init; }

  /// <summary>Creation instant of the group's oldest pending single.</summary>
  public required DateTimeOffset OldestCreatedAt { get; init; }

  /// <summary>Creation instant of the group's newest pending single.</summary>
  public required DateTimeOffset NewestCreatedAt { get; init; }
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
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorDtoSurfaceTests.cs:PerspectiveCursorCompletion_ProcessedEventIds_DefaultsEmptyAndRoundTripsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveApplyIdempotencyTests.cs:RunAsync_RerunWithSameEvents_SkipsAllAndDoesNotReWriteAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveApplyIdempotencyTests.cs:RunAsync_RerunWithMixOfAppliedAndNewEvents_AppliesOnlyNewAsync</tests>
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
  /// running on subsequent drains for the same event (a production silent-skip bug).
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
/// Production forensic G6: <see cref="CommitSequence"/> carries <c>wh_event_store.commit_sequence</c> via
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
  /// out triage value (observed in production: tens of thousands of rows collapsed
  /// to one fingerprint cluster). NULL when no failure has been recorded against the row yet (the
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


/// <summary>A table offered for rewrite, with the measurement that justified offering it.</summary>
/// <param name="TableName">Unqualified table name.</param>
/// <param name="BloatRatio">Heap bytes per live row over the expected row width; ~1.0 is lean.</param>
/// <param name="Requested">True when a migration explicitly recorded that this table owes a rewrite.</param>
public readonly record struct TableRewriteCandidate(string TableName, double BloatRatio, bool Requested);
