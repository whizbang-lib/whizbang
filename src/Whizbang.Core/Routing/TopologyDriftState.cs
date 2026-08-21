using System.Collections.Concurrent;

namespace Whizbang.Core.Routing;

/// <summary>
/// One observed topology-ownership violation: a second service's subscription already exists
/// on a command inbox entity this service is about to own (one service owns a command
/// namespace — the ownership analyzer enforces the intra-compilation invariant at build time,
/// this runtime check covers the cross-service case build-time visibility cannot see).
/// </summary>
/// <param name="Entity">The broker entity (topic/exchange) with the competing claim.</param>
/// <param name="ObservedSubscription">The foreign subscription/queue observed on it.</param>
/// <param name="Detail">Human-readable diagnosis for logs and health detail.</param>
/// <docs>fundamentals/dispatcher/routing#ownership-drift</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyDriftStateTests.cs</tests>
public sealed record TopologyDriftFinding(
  string Entity,
  string ObservedSubscription,
  string Detail
);

/// <summary>
/// Thread-safe accumulator for startup topology-drift findings (topology arc phase 5). The
/// provisioning path records findings while it inspects command inbox entities it is about to
/// own; <see cref="Health.TopologyDriftHealthSource"/> surfaces them as a Degraded component so
/// the violation is visible on the health endpoint, not just in a log line.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#ownership-drift</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyDriftStateTests.cs</tests>
public sealed class TopologyDriftState {
  private readonly ConcurrentQueue<TopologyDriftFinding> _findings = new();

  /// <summary>Records one observed ownership violation.</summary>
  /// <param name="finding">The finding to record.</param>
  /// <exception cref="ArgumentNullException">Thrown when finding is null.</exception>
  public void Record(TopologyDriftFinding finding) {
    ArgumentNullException.ThrowIfNull(finding);
    _findings.Enqueue(finding);
  }

  /// <summary>True when at least one drift finding has been recorded.</summary>
  public bool HasDrift => !_findings.IsEmpty;

  /// <summary>Snapshot of every recorded finding, in record order.</summary>
  public IReadOnlyList<TopologyDriftFinding> Findings => [.. _findings];
}
