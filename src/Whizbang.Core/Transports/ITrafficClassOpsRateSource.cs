using System.Collections.Generic;

namespace Whizbang.Core.Transports;

/// <summary>
/// One namespace's projected broker-operation rate.
/// </summary>
/// <param name="TransportNamespace">The broker namespace key.</param>
/// <param name="TrafficClass">The traffic class routed to it.</param>
/// <param name="OpsPerSecond">Projected operations per second.</param>
/// <docs>operations/observability/metrics#traffic-classes</docs>
public readonly record struct TrafficClassOpsRate(
  string TransportNamespace, string TrafficClass, double OpsPerSecond);

/// <summary>
/// A transport's per-namespace ops-rate projection (topology arc phase 10, spec increment 5).
/// Optional and injected, like <see cref="IBacklogPeek"/>.
/// </summary>
/// <remarks>
/// The incident this arc was written for was invisible precisely because this number was never
/// published: the receive machinery demanded thousands of operations per second against a
/// ~1,000/sec pool WHILE IDLE, and the only witness was the cloud provider's billing meter. A
/// per-namespace projection makes the pool that is actually exhausted nameable.
/// </remarks>
/// <docs>operations/observability/metrics#traffic-classes</docs>
public interface ITrafficClassOpsRateSource {
  /// <summary>The short transport tag (<c>asb</c>, <c>rabbitmq</c>).</summary>
  string TransportName { get; }

  /// <summary>The current projection per TransportNamespace.</summary>
  /// <returns>One entry per namespace this transport holds a client for.</returns>
  IReadOnlyList<TrafficClassOpsRate> Project();
}
