// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Parameters for <see cref="IWorkCoordinator.ClaimWorkAsync"/> — the focused
/// replacement for the claim portion of the legacy <c>ProcessWorkBatchAsync</c>.
/// Only the polling claim worker calls this method; flushers and heartbeats
/// have their own dedicated calls.
/// </summary>
/// <param name="InstanceId">Calling service instance.</param>
/// <param name="ServiceName">Service name (diagnostics).</param>
/// <param name="HostName">Pod / host name (diagnostics).</param>
/// <param name="ProcessId">OS process id (diagnostics).</param>
/// <param name="MaxStreams">Cap on rows returned per call.</param>
/// <param name="PartitionCount">Modulo partition count for load balancing.</param>
/// <param name="LeaseSeconds">Duration of the lease assigned to claimed work.</param>
/// <param name="IncludeOutstanding">
/// When true, the coordinator also returns this instance's untruncated outstanding-work counts on
/// <see cref="WorkBatch.Outstanding"/>, batched into the SAME round trip as the claim. The
/// outstanding budget needs those counts every cycle, and issuing them as a separate call doubled
/// the claim loop's round trips; batching reads them from the same snapshot instead. Stores that do
/// not support it leave <see cref="WorkBatch.Outstanding"/> null and the caller falls back to
/// <see cref="IWorkCoordinator.CountOutstandingWorkAsync"/>.
/// </param>
/// <param name="FreshWorkShare">Share of the inbox re-emission reserved for fresh-head streams (migration 126).</param>
/// <param name="MaxAcquireRows">
/// Row bound on inbox ACQUISITION (the rows leased from the unowned backlog in this cycle). Null means
/// the store falls back to <paramref name="MaxStreams"/>. Acquisition needs its own bound in rows: the
/// stream count used as a row cap turned fat streams into one row per cycle (#714).
/// </param>
/// <param name="AllowSteal">
/// When true, an instance whose own residue of the unowned backlog is empty may acquire unowned rows
/// assigned to other residues (#725). The caller sets it only after its own claims have come back
/// empty, so ownership stays stable under normal load.
/// </param>
/// <param name="MaxPerspectiveStreams">
/// Bound on perspective ACQUISITION (new perspective streams leased this cycle). Null means
/// <paramref name="MaxStreams"/>; zero means "lease no new perspective work this cycle" while the
/// drain channel is above its cap (#719). Re-emission of work already held is unaffected.
/// </param>
/// <param name="IdleSettled">
/// Whether the SERVICE reads settled, which admits the idle band at full width. The caller owns
/// this because it already measures it for housekeeping; deciding it inside the claim would mean
/// counting the service-wide backlog on every poll.
/// </param>
/// <param name="IdleTrickleAfter">
/// How long an idle row may wait before a busy service takes it anyway. Null leaves the store's
/// own default in force.
/// </param>
/// <param name="IdleTrickleSlice">
/// How many idle rows one claim may take while the service is busy. Null leaves the store default.
/// </param>
/// <param name="IdleForceAfter">
/// How long the band may go without a full drain before one happens regardless of activity. Null
/// leaves the store default.
/// </param>
/// <param name="MaxOutboxAcquireRows">
/// Row bound on outbox ACQUISITION, independent of <paramref name="MaxStreams"/>. Null means the store
/// falls back to <paramref name="MaxStreams"/> rows, the behavior before the outbox had its own bound:
/// the stream window used as a row cap leased about one row per stream per claim on a backlog of a
/// few long streams (#917).
/// </param>
/// <param name="OutboxRunLength">
/// How many consecutive rows of one outbox stream a claim may lease. The oldest rows still choose
/// which streams move (at most <paramref name="MaxStreams"/> of them); each then leases a run of its
/// next rows within <paramref name="MaxOutboxAcquireRows"/>. Null or 1 is one row per chosen head.
/// </param>
/// <param name="PartitionAssignment">
/// The version of the partition assignment the caller has cached (#1254). While it is still the published one and
/// unexpired, the claim takes this instance's rank and the member count from it instead of ranking the instances it
/// believes are alive; otherwise the claim ranks itself as before and reports the copy stale
/// (<see cref="WorkBatch.PartitionAssignmentStale"/>). Null: no assignment, the claim ranks itself.
/// </param>
/// <param name="AliveLockHeld">
/// Whether this instance holds its session alive-lock right now (#1286), from
/// <see cref="Whizbang.Core.Workers.IInstanceAliveLockSource"/>. A direct instance holding it is live by the lock and
/// beats on the slow cadence, so the claim does not report its registration stale between beats
/// (<see cref="WorkBatch.InstanceRegistrationStale"/>). A missing registration is reported whatever this says.
/// </param>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public sealed record ClaimWorkRequest(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  int MaxStreams = 1000,
  int PartitionCount = 10000,
  int LeaseSeconds = 300,
  bool IncludeOutstanding = false,
  double FreshWorkShare = 0.5,
  int? MaxAcquireRows = null,
  bool AllowSteal = false,
  int? MaxPerspectiveStreams = null,
  bool IdleSettled = false,
  TimeSpan? IdleTrickleAfter = null,
  int? IdleTrickleSlice = null,
  TimeSpan? IdleForceAfter = null,
  int? MaxOutboxAcquireRows = null,
  int? OutboxRunLength = null,
  Whizbang.Core.Workers.PartitionAssignmentVersion? PartitionAssignment = null,
  bool AliveLockHeld = false);
