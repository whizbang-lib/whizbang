using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Health;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Bridges the managed-resource <see cref="WhizbangHealthAggregator"/> into the ASP.NET health system
/// for one <see cref="HealthProbe"/>. Register one instance tagged <c>"live"</c> (liveness) and one
/// tagged <c>"ready"</c> (readiness) via <see cref="WhizbangManagedHealthCheckExtensions"/>; each
/// evaluates every source through its policy and reports the aggregated worst status, with each
/// component's state in the result data for diagnostics.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public sealed class WhizbangManagedHealthCheck : IHealthCheck {
  private readonly WhizbangHealthAggregator _aggregator;
  private readonly HealthProbe _probe;

  /// <summary>Creates a check that evaluates the aggregator for the given probe.</summary>
  public WhizbangManagedHealthCheck(WhizbangHealthAggregator aggregator, HealthProbe probe) {
    ArgumentNullException.ThrowIfNull(aggregator);
    _aggregator = aggregator;
    _probe = probe;
  }

  /// <inheritdoc />
  public async Task<HealthCheckResult> CheckHealthAsync(
      HealthCheckContext context, CancellationToken cancellationToken = default) {
    var result = await _aggregator.EvaluateAsync(_probe, cancellationToken).ConfigureAwait(false);
    // Sources sharing a component name must never kill the endpoint — a diagnostic bridge that
    // throws takes the pod down in a crash loop instead of reporting the state it exists to
    // surface. Repeated names get a stable [n] suffix so every report stays visible.
    var data = new Dictionary<string, object>(result.Components.Count, StringComparer.Ordinal);
    foreach (var c in result.Components) {
      var key = c.Component;
      for (var n = 2; data.ContainsKey(key); n++) {
        key = $"{c.Component}[{n}]";
      }
      data[key] = c.Detail is null ? $"{c.State} => {c.Status}" : $"{c.State} => {c.Status} ({c.Detail})";
    }
    var description = string.Join(", ", result.Components.Select(static c => $"{c.Component}={c.State}"));
    return result.Status switch {
      HealthStatus.Healthy => HealthCheckResult.Healthy(description, data),
      HealthStatus.Degraded => HealthCheckResult.Degraded(description, exception: null, data: data),
      _ => HealthCheckResult.Unhealthy(description, exception: null, data: data)
    };
  }
}
