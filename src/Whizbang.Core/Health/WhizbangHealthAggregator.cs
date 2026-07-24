using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Whizbang.Core.Health;

/// <summary>One managed resource's contribution to an aggregated evaluation.</summary>
/// <docs>resilience/managed-resource-health</docs>
public readonly record struct ComponentReport(
  string Component, ComponentState State, HealthStatus Status, string? Detail);

/// <summary>
/// The aggregated result for a probe: the worst <see cref="Status"/> across all components (which is
/// the overall answer) plus each <see cref="Components"/> contribution for diagnostics.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public readonly record struct AggregatedHealth(HealthStatus Status, IReadOnlyList<ComponentReport> Components);

/// <summary>
/// Aggregates every <see cref="IWhizbangHealthSource"/> into a single liveness/readiness answer.
/// Each source's raw <see cref="ComponentState"/> is mapped through its effective
/// <see cref="HealthPolicy"/> (per <see cref="WhizbangHealthOptions"/>), and the overall status is
/// the worst mapped status — so one genuinely Faulted resource fails the probe while intentional
/// states (migrating, paused) stay healthy under the default policy.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class WhizbangHealthAggregator {
  private readonly IEnumerable<IWhizbangHealthSource> _sources;
  private readonly WhizbangHealthOptions _options;

  /// <summary>Creates an aggregator over the given health sources and policy options.</summary>
  public WhizbangHealthAggregator(IEnumerable<IWhizbangHealthSource> sources, WhizbangHealthOptions options) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(options);
    _sources = sources;
    _options = options;
  }

  /// <summary>Evaluates every source for the given probe and returns the aggregated (worst-wins) result.</summary>
  public async ValueTask<AggregatedHealth> EvaluateAsync(HealthProbe probe, CancellationToken cancellationToken) {
    var reports = new List<ComponentReport>();
    // HealthStatus is ordered Unhealthy(0) < Degraded(1) < Healthy(2), so the worst is the minimum.
    var worst = HealthStatus.Healthy;
    foreach (var source in _sources) {
      var health = await source.ReportAsync(cancellationToken).ConfigureAwait(false);
      var status = _options.PolicyFor(source.Component).Map(health.State, probe);
      reports.Add(new ComponentReport(source.Component, health.State, status, health.Detail));
      if (status < worst) {
        worst = status;
      }
    }
    return new AggregatedHealth(worst, reports);
  }
}
