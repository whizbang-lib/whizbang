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
  private readonly IReadOnlyList<IWhizbangHealthSource> _sources;
  private readonly WhizbangHealthOptions _options;

  /// <summary>Creates an aggregator over the given health sources and policy options.</summary>
  public WhizbangHealthAggregator(IEnumerable<IWhizbangHealthSource> sources, WhizbangHealthOptions options) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(options);
    // Materialised once: the sources are asked concurrently and then indexed to pair each
    // result with its component, which a lazily-enumerated sequence cannot support.
    _sources = sources as IReadOnlyList<IWhizbangHealthSource> ?? [.. sources];
    _options = options;
  }

  /// <summary>Evaluates every source for the given probe and returns the aggregated (worst-wins) result.</summary>
  public async ValueTask<AggregatedHealth> EvaluateAsync(HealthProbe probe, CancellationToken cancellationToken) {
    // Every source is asked at once and each is bounded. Both halves matter.
    //
    // Bounded, because a source reports on a dependency and a dependency probe can block forever:
    // a network call inside a library that does not observe cancellation neither returns nor
    // throws. Awaiting it unbounded took the whole health response with it, so the policy below --
    // which says liveness is healthy for every state, precisely so a dependency fault never
    // restarts a pod -- never got to run. Kubelet killed processes that were running perfectly well.
    //
    // At once, because a per-source timeout that runs sequentially still accumulates: five
    // unresponsive sources at two seconds each is ten seconds, and the probe times out anyway.
    var sources = _sources;
    var tasks = new Task<ComponentHealth>[sources.Count];
    for (var i = 0; i < sources.Count; i++) {
      tasks[i] = _reportBoundedAsync(sources[i], cancellationToken);
    }
    await Task.WhenAll(tasks).ConfigureAwait(false);

    var reports = new List<ComponentReport>(sources.Count);
    // HealthStatus is ordered Unhealthy(0) < Degraded(1) < Healthy(2), so the worst is the minimum.
    var worst = HealthStatus.Healthy;
    for (var i = 0; i < sources.Count; i++) {
      var health = tasks[i].Result;
      var status = _options.PolicyFor(sources[i].Component).Map(health.State, probe);
      reports.Add(new ComponentReport(sources[i].Component, health.State, status, health.Detail));
      if (status < worst) {
        worst = status;
      }
    }
    return new AggregatedHealth(worst, reports);
  }

  /// <summary>
  /// Asks one source, returning a faulted report rather than waiting indefinitely.
  /// </summary>
  /// <remarks>
  /// A source that does not answer is reported as faulted rather than healthy: reporting healthy
  /// would hide the one component that needs looking at. It is never rethrown, because a probe that
  /// throws is a probe that does not answer, which is the failure being fixed.
  /// </remarks>
  private async Task<ComponentHealth> _reportBoundedAsync(
      IWhizbangHealthSource source, CancellationToken cancellationToken) {
    try {
      return await source.ReportAsync(cancellationToken)
        .AsTask()
        .WaitAsync(_options.SourceTimeout, cancellationToken)
        .ConfigureAwait(false);
    } catch (TimeoutException) {
      return new ComponentHealth(
        ComponentState.Faulted,
        $"health source did not answer within {_options.SourceTimeout.TotalSeconds:0.##}s");
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      return new ComponentHealth(ComponentState.Faulted, "health source was canceled");
    } catch (Exception ex) {
      // A throwing source is a faulted component, not a failed probe.
      return new ComponentHealth(ComponentState.Faulted, ex.GetType().Name);
    }
  }
}
