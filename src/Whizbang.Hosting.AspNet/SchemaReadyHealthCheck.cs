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
      : HealthCheckResult.Unhealthy("Schema initialization has not completed."));
}
