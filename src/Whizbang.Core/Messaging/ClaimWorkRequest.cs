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
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public sealed record ClaimWorkRequest(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  int MaxStreams = 1000,
  int PartitionCount = 10000,
  int LeaseSeconds = 300,
  bool IncludeOutstanding = false);
