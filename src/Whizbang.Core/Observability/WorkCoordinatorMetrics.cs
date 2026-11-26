// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the work coordination pipeline: flush, handler commits, publisher worker, maintenance, gate.
/// Meter name: Whizbang.WorkCoordinator
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/WorkCoordinatorMetricsTests.cs</tests>
public sealed class WorkCoordinatorMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.WorkCoordinator";

  /// <summary>Tag on <see cref="OutboxFlushRetries"/>: the kind of transient database failure retried.</summary>
  public const string REASON_TAG = "reason";

  /// <summary>Tag on <see cref="OutboxFlushFailedMessages"/>: why the messages were not stored (<see cref="Whizbang.Core.Messaging.OutboxFlushFailureCause"/>).</summary>
  public const string CAUSE_TAG = "cause";

  /// <summary>Tag on <see cref="OutboxFlushFailedMessages"/>: where the messages went, <see cref="OUTCOME_DEAD_LETTERED"/> or <see cref="OUTCOME_NOT_RECORDED"/>.</summary>
  public const string OUTCOME_TAG = "outcome";

  /// <summary>Tag on <see cref="QueueStatisticsRefreshSkips"/>: the queue table whose statistics were not refreshed.</summary>
  public const string TABLE_TAG = "table";

  /// <summary>The messages were written to the dead-letter table, from where recovery stores them again.</summary>
  public const string OUTCOME_DEAD_LETTERED = "dead_lettered";

  /// <summary>The messages could not be written to the dead-letter table either; their ids are in the error log only.</summary>
  public const string OUTCOME_NOT_RECORDED = "not_recorded";
#pragma warning restore CA1707


  // Timing

  /// <summary>
  /// Time one flush spends storing what a strategy queued (outbox and inbox rows, completions,
  /// failures) and running its distribute lifecycle stages, in milliseconds, tagged <c>strategy</c>.
  /// Recorded by every strategy for each flush that had work; an empty flush records nothing here
  /// and counts in <see cref="EmptyFlushCalls"/> instead.
  /// </summary>
  public Histogram<double> FlushDuration { get; }

  // Counters

  /// <summary>
  /// Handler-commit batches that fell back from the bulk tier to the per-handler savepoint
  /// loop (#573). A fleet quietly living on the slow path is invisible without this —
  /// sustained non-zero is the operator's cue to read the paired warning's SQLSTATE.
  /// </summary>
  public PassiveCounter<long> CommitHandlerFallbacks { get; }

  /// <summary>
  /// Outbox rows the store skipped because a row with the same message id already existed. With
  /// deterministic emission identity (<see cref="Messaging.EmissionIdentity"/>) this is the count of
  /// republishes a retry would have produced; tagged by <c>message_type</c>. Sustained non-zero means
  /// rows are being re-dispatched after their emissions committed, which points at the completion path.
  /// </summary>
  public PassiveCounter<long> OutboxEmissionDeduplicated { get; }

  /// <summary>Total FlushAsync calls.</summary>
  public PassiveCounter<long> FlushCalls { get; }

  /// <summary>Flushes that found nothing queued, tagged <c>strategy</c>. Every strategy counts them.</summary>
  public PassiveCounter<long> EmptyFlushCalls { get; }

  /// <summary>
  /// Retries of a stream's outbox batch after a transient database failure (a lost connection, an exhausted pool, a
  /// command timeout, the server out of connection slots), tagged <c>reason</c> (#1871). A steady rate is a database
  /// or pool that cannot keep up with the concurrent per-stream flushes.
  /// </summary>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs:FlushFailsOnceForSomeStreams_EveryAcceptedMessageIsStoredAsync</tests>
  public PassiveCounter<long> OutboxFlushRetries { get; }

  /// <summary>
  /// Outbox messages the batcher accepted and could not store, tagged <c>cause</c> (retries exhausted, not
  /// retryable, abandoned at shutdown) and <c>outcome</c> (<c>dead_lettered</c>, or <c>not_recorded</c> when the
  /// dead-letter write failed too) (#1871). Any <c>not_recorded</c> count is messages that exist only in the error log.
  /// </summary>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowOutboxFlushFailureTests.cs:FlushFailsTransientlyOnEveryAttempt_DeadLettersTheBatchAfterMaxFlushAttemptsAsync</tests>
  public PassiveCounter<long> OutboxFlushFailedMessages { get; }

  /// <summary>
  /// Retries of a stream's inbox batch after a transient database failure, tagged <c>reason</c> (#1885). A steady
  /// rate is a database or pool that cannot keep up with the concurrent per-stream inbox flushes.
  /// </summary>
  /// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs:FlushFailsOnceForSomeStreams_EveryAcceptedMessageIsStoredAsync</tests>
  public PassiveCounter<long> InboxFlushRetries { get; }

  /// <summary>
  /// Inbox messages the inbox batcher accepted and could not store, tagged <c>cause</c> (retries exhausted, not
  /// retryable, abandoned at shutdown) and <c>outcome</c> (<c>dead_lettered</c>, or <c>not_recorded</c> when the
  /// dead-letter write failed too) (#1885). Any <c>not_recorded</c> count is messages that exist only in the error log.
  /// </summary>
  /// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/SlidingWindowInboxFlushFailureTests.cs:FlushFailsTransientlyOnEveryAttempt_DeadLettersTheBatchAfterMaxFlushAttemptsAsync</tests>
  public PassiveCounter<long> InboxFlushFailedMessages { get; }

  // Publisher worker

  /// <summary>Lease renewals due to transport not ready.</summary>
  public PassiveCounter<long> PublisherLeaseRenewals { get; }

  /// <summary>Total messages buffered for publish.</summary>
  public PassiveCounter<long> PublisherBufferedMessages { get; }

  // Maintenance

  /// <summary>Duration per maintenance task.</summary>
  public Histogram<double> MaintenanceTaskDuration { get; }

  /// <summary>Rows cleaned per task.</summary>
  public Histogram<long> MaintenanceTaskRowsAffected { get; }

  // Gate

  /// <summary>
  /// Time between <see cref="Messaging.WorkCoordinatorGate.AcquireAsync"/> grant
  /// and the returned <c>Releaser.Dispose</c>, tagged with <c>caller</c> (the
  /// method that called <c>AcquireAsync</c>). Surfaces which gated code paths
  /// are holding slots longest; a production forensic investigation showed a
  /// consumer's service at 20% CPU with the gate saturated, meaning slots were
  /// held during application work, not just DB I/O.
  /// </summary>
  public Histogram<double> GateHoldDuration { get; }

  // Batch buffer (#2220)

  /// <summary>Appends that waited for room in the batch buffer.</summary>
  /// <docs>messaging/outbox-pattern#bounded-buffer</docs>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/BatchBufferTests.cs:WaitsStartingAndStopping_AreEachWarnedOnce_AndTheWaitsAreMeasuredAsync</tests>
  public PassiveCounter<long> BatchBufferAppendWaits { get; }

  /// <summary>How long an append waited for room in the batch buffer.</summary>
  /// <docs>messaging/outbox-pattern#bounded-buffer</docs>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/BatchBufferTests.cs:WaitsStartingAndStopping_AreEachWarnedOnce_AndTheWaitsAreMeasuredAsync</tests>
  public Histogram<double> BatchBufferWaitDuration { get; }

  /// <summary>Appends admitted past the batch buffer's limit because the caller held a database transaction open.</summary>
  /// <docs>messaging/outbox-pattern#bounded-buffer</docs>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/BatchBufferTests.cs:ACallerHoldingATransactionOpen_GoesPastTheLimit_UpToTheCeiling_AndIsNamedAsync</tests>
  public PassiveCounter<long> BatchBufferCeilingOvershoots { get; }

  /// <summary>Tag on <see cref="FencedWrites"/>: what was fenced (an outbox completion, a handler commit, a cursor, ...).</summary>
  public const string KIND_TAG = "kind";

  /// <summary>
  /// Writes that landed nowhere because the partition epoch they were made under is no longer the partition's (#2556):
  /// the partition's ownership moved on while this instance worked, so the result stays for the current owner. Tagged
  /// <see cref="KIND_TAG"/>. Expected around a rebalance; sustained non-zero outside one means an instance keeps working
  /// partitions it no longer owns.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AFencedWrite_IsCountedByKind_AndLoggedAsync</tests>
  public PassiveCounter<long> FencedWrites { get; }

  /// <summary>
  /// Claims that claimed nothing because no valid partition map was published (#2554): in map mode an instance claims
  /// only through the published map, and waits while there is none. Every such claim is counted; the warning is written
  /// once per streak.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AClaimWithNoValidMap_IsCounted_AndWarnedOncePerStreakAsync</tests>
  public PassiveCounter<long> PartitionMapUnavailableClaims { get; }

  /// <summary>
  /// Queue tables whose stale planner statistics a claim could not refresh (#2611), tagged <see cref="TABLE_TAG"/> and
  /// <see cref="REASON_TAG"/>: <c>lock_not_available</c> when another session held a lock that conflicts with ANALYZE (a
  /// concurrent index build, or a transaction that locked the table), <c>lock_timeout</c> when a lock wait passed the
  /// refresh's short backstop, <c>failed</c> when ANALYZE raised an error. Counted once per back-off: the table is then left
  /// alone for a bounded interval, so each count is one stretch of claims planned on stale statistics.
  /// </summary>
  /// <docs>operations/observability/metrics#queue-statistics-refresh</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ClaimWorkStaleStatisticsTests.cs:ClaimWork_WhileTheRefreshCannotAnalyze_NeverWaits_CountsTheSkip_AndWarnsOncePerBackoffAsync</tests>
  public PassiveCounter<long> QueueStatisticsRefreshSkips { get; }

  // 1 while the claims of this process find no valid partition map, so the warning is written once per streak.
  private int _partitionMapUnavailable;

  /// <summary>
  /// Records whether a claim found a valid partition map, and answers whether it starts a streak without one: the first
  /// claim with no map after one that had a map (or the first ever), which is the one to warn about.
  /// </summary>
  /// <param name="unavailable">Whether the claim reported no valid map.</param>
  /// <returns>True when this claim starts a streak of claims without a map.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AClaimWithNoValidMap_IsCounted_AndWarnedOncePerStreakAsync</tests>
  public bool RecordPartitionMapAvailability(bool unavailable) {
    if (!unavailable) {
      Interlocked.Exchange(ref _partitionMapUnavailable, 0);
      return false;
    }
    PartitionMapUnavailableClaims.Add(1);
    return Interlocked.Exchange(ref _partitionMapUnavailable, 1) == 0;
  }

  private readonly Meter _meter;

  /// <summary>
  /// Publishes <c>whizbang.batch_buffer.messages</c> and <c>whizbang.batch_buffer.bytes</c>: what the outbox and inbox
  /// batch strategies hold in memory now, read at every export (#2220).
  /// </summary>
  /// <param name="messages">Reads the buffered message count.</param>
  /// <param name="bytes">Reads the buffered byte count.</param>
  /// <docs>messaging/outbox-pattern#bounded-buffer</docs>
  /// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/BatchBufferTests.cs:WaitsStartingAndStopping_AreEachWarnedOnce_AndTheWaitsAreMeasuredAsync</tests>
  public void ObserveBatchBuffer(Func<long> messages, Func<long> bytes) {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(bytes);
    _meter.CreateObservableGauge("whizbang.batch_buffer.messages", messages,
      description: "Messages the outbox and inbox batch strategies hold in memory, waiting to be stored");
    _meter.CreateObservableGauge("whizbang.batch_buffer.bytes", bytes, "By",
      "Payload bytes the outbox and inbox batch strategies hold in memory, waiting to be stored");
  }

  /// <summary>
  /// Publishes <c>whizbang.work_coordinator.handler_commits.queued</c>: handler results dispatched but not
  /// yet committed, read from <paramref name="pending"/> at every export. The commit queue is the one place
  /// dispatched work waits in memory; under a bulk fan-out it once held whole composite expansions while the
  /// lease count stayed capped and nothing said where the memory was (#740).
  /// </summary>
  /// <param name="pending">Reads the queue depth: what waits in the channel plus what the flusher has taken up.</param>
  /// <docs>operations/observability/metrics#handler-commit-queue</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/InboxHandlerWorkerQueueDepthTests.cs</tests>
  public void ObserveHandlerCommitQueue(Func<long> pending) {
    ArgumentNullException.ThrowIfNull(pending);
    _meter.CreateObservableGauge("whizbang.work_coordinator.handler_commits.queued", pending,
      description: "Handler results dispatched but not yet committed: queued in the commit channel plus taken up by the flusher");
  }

  /// <summary>
  /// The stream-integrity metrics the work coordinator records the integrity ledger's opened and closed buckets on
  /// (#2256); null when they are not registered, and then nothing is counted.
  /// </summary>
  /// <remarks>Carried here so the coordinator reaches every metric it records through one collaborator.</remarks>
  public StreamIntegrityMetrics? Integrity { get; }

  /// <summary>Initializes a new instance of the <see cref="WorkCoordinatorMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  /// <param name="integrity">The stream-integrity metrics, when registered; the container supplies them.</param>
  public WorkCoordinatorMetrics(WhizbangMetrics whizbangMetrics, StreamIntegrityMetrics? integrity = null) {
    Integrity = integrity;
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    _meter = meter;

    FlushDuration = meter.CreateHistogram<double>("whizbang.work_coordinator.flush.duration", "ms", "Time one flush spends storing queued work and running its distribute lifecycle stages; tagged by strategy");

    CommitHandlerFallbacks = meter.CreatePassiveCounter<long>(
      "whizbang.work_coordinator.commit_handler.fallbacks",
      description: "Handler-commit batches that fell back from the bulk tier to the per-handler savepoint loop");
    OutboxEmissionDeduplicated = meter.CreatePassiveCounter<long>(
      "whizbang.work_coordinator.outbox.emission_deduplicated",
      description: "Outbox rows skipped because the same message id was already stored: republishes a retry would have produced; tagged by message_type");
    FlushCalls = meter.CreatePassiveCounter<long>("whizbang.work_coordinator.flush.calls", description: "Total FlushAsync calls");
    EmptyFlushCalls = meter.CreatePassiveCounter<long>("whizbang.work_coordinator.flush.empty_calls", description: "Flushes that found nothing queued; tagged by strategy");

    OutboxFlushRetries = meter.CreatePassiveCounter<long>(
      "whizbang.work_coordinator.outbox_flush.retries",
      description: "Retries of a stream's outbox batch after a transient database failure; tagged by reason");
    OutboxFlushFailedMessages = meter.CreatePassiveCounter<long>(
      "whizbang.work_coordinator.outbox_flush.failed_messages",
      description: "Outbox messages the batcher accepted and could not store; tagged by cause and outcome (dead_lettered or not_recorded)");
    OutboxFlushFailedMessages.Touch(OUTCOME_TAG, [OUTCOME_DEAD_LETTERED, OUTCOME_NOT_RECORDED]);

    InboxFlushRetries = meter.CreatePassiveCounter<long>(
      "whizbang.work_coordinator.inbox_flush.retries",
      description: "Retries of a stream's inbox batch after a transient database failure; tagged by reason");
    InboxFlushFailedMessages = meter.CreatePassiveCounter<long>(
      "whizbang.work_coordinator.inbox_flush.failed_messages",
      description: "Inbox messages the inbox batcher accepted and could not store; tagged by cause and outcome (dead_lettered or not_recorded)");
    InboxFlushFailedMessages.Touch(OUTCOME_TAG, [OUTCOME_DEAD_LETTERED, OUTCOME_NOT_RECORDED]);

    PublisherLeaseRenewals = meter.CreatePassiveCounter<long>("whizbang.publisher.lease_renewals", description: "Lease renewals due to transport not ready");
    PublisherBufferedMessages = meter.CreatePassiveCounter<long>("whizbang.publisher.buffered_messages", description: "Total messages buffered for publish");

    MaintenanceTaskDuration = meter.CreateHistogram<double>("whizbang.maintenance.task.duration", "ms", "Duration per maintenance task");
    MaintenanceTaskRowsAffected = meter.CreateHistogram<long>("whizbang.maintenance.task.rows_affected", description: "Rows cleaned per task");

    GateHoldDuration = meter.CreateHistogram<double>("whizbang.gate.hold_duration_ms", "ms", "WorkCoordinatorGate slot-held duration; tagged with caller");

    BatchBufferAppendWaits = meter.CreatePassiveCounter<long>("whizbang.batch_buffer.append_waits",
      description: "Appends to the outbox or inbox batch strategy that waited for room in the batch buffer");
    BatchBufferWaitDuration = meter.CreateHistogram<double>("whizbang.batch_buffer.wait_duration", "ms",
      "How long an append waited for room in the batch buffer");
    BatchBufferCeilingOvershoots = meter.CreatePassiveCounter<long>("whizbang.batch_buffer.ceiling_overshoots",
      description: "Appends admitted past the batch buffer's limit because the caller held a database transaction open");

    FencedWrites = meter.CreatePassiveCounter<long>("whizbang.work_coordinator.fenced_writes",
      description: "Writes that landed nowhere because the partition's ownership moved on since the work was claimed; tagged by kind");
    PartitionMapUnavailableClaims = meter.CreatePassiveCounter<long>("whizbang.work_coordinator.partition_map_unavailable",
      description: "Claims that claimed nothing because no valid partition map was published (ownership by map)");
    QueueStatisticsRefreshSkips = meter.CreatePassiveCounter<long>("whizbang.work_coordinator.queue_statistics.refresh_skipped",
      description: "Queue tables whose stale planner statistics a claim could not refresh, once per back-off; tagged by table and reason");
  }
}
