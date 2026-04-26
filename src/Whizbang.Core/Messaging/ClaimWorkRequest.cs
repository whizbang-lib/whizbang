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
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
public sealed record ClaimWorkRequest(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  int MaxStreams = 1000,
  int PartitionCount = 10000,
  int LeaseSeconds = 300);
