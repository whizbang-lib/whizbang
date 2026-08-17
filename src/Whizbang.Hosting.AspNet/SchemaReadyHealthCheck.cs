using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Workers;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Readiness <see cref="IHealthCheck"/> that reports <see cref="HealthStatus.Unhealthy"/> until
/// <see cref="ISchemaReadyGate"/> signals schema readiness, then <see cref="HealthStatus.Healthy"/>.
/// Register it tagged for <b>readiness</b> (not liveness) so a host running non-blocking schema init
/// reports "not ready" — and is kept out of traffic rotation — while migrations run, without failing
/// its liveness probe. Pairs with <see cref="DatabaseAvailabilityMiddleware"/>.
/// </summary>
/// <docs>resilience/database-availability-middleware</docs>
public sealed class SchemaReadyHealthCheck(ISchemaReadyGate schemaReadyGate) : IHealthCheck {
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate;

  /// <inheritdoc />
  public Task<HealthCheckResult> CheckHealthAsync(
      HealthCheckContext context, CancellationToken cancellationToken = default)
    => Task.FromResult(_schemaReadyGate.IsReady
      ? HealthCheckResult.Healthy("Schema is initialized.")
      // Degraded, not Unhealthy: schema-initializing is an intentional startup condition, not a
      // fault. Degraded keeps a bounded-timeout rollout alive (HTTP 200) and the pod in rotation —
      // the availability gate and data-plane seams refuse what cannot be served yet — while the
      // condition stays visible. Unhealthy made every migration longer than the deploy timeout a
      // rollback.
      : HealthCheckResult.Degraded("Schema initialization has not completed."));
}
