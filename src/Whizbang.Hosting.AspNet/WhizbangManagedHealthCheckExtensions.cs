using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Health;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Registers the managed-resource liveness + readiness checks. The liveness check is tagged
/// <c>"live"</c> and the readiness check <c>"ready"</c> so they slot into the standard probe
/// endpoints. Both read the same <see cref="WhizbangHealthAggregator"/> but evaluate different
/// <see cref="HealthProbe"/>s — so an intentionally-migrating service is alive yet, under a strict
/// policy, not ready.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public static class WhizbangManagedHealthCheckExtensions {
  /// <summary>
  /// Adds the Whizbang liveness (tag <c>"live"</c>) and readiness (tag <c>"ready"</c>) checks. Call
  /// <c>AddWhizbangManagedHealth()</c> + register sources on the service collection first.
  /// </summary>
  public static IHealthChecksBuilder AddWhizbangManagedHealthChecks(
      this IHealthChecksBuilder builder,
      string livenessName = "whizbang-live", string readinessName = "whizbang-ready") {
    ArgumentNullException.ThrowIfNull(builder);
    builder.Add(new HealthCheckRegistration(
      livenessName,
      static sp => new WhizbangManagedHealthCheck(sp.GetRequiredService<WhizbangHealthAggregator>(), HealthProbe.Liveness),
      failureStatus: null, tags: ["live"]));
    builder.Add(new HealthCheckRegistration(
      readinessName,
      static sp => new WhizbangManagedHealthCheck(sp.GetRequiredService<WhizbangHealthAggregator>(), HealthProbe.Readiness),
      failureStatus: null, tags: ["ready"]));
    return builder;
  }
}
