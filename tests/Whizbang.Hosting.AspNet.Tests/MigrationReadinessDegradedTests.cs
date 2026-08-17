using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// A consumer's deployment tooling waits on readiness with a bounded timeout (helm's default is
/// three minutes); a ten-minute migration that reports readiness UNHEALTHY therefore times the
/// rollout out and rolls it back — punishing exactly the deploys that carry schema changes. The
/// requested contract: while migrating, readiness reports <see cref="HealthStatus.Degraded"/> —
/// HTTP 200, so the rollout completes and the pod enters rotation (the data-plane seams keep
/// refusing what genuinely cannot be served) — while staying VISIBLE as not-fully-up, unlike
/// plain Healthy which hides the migration from every dashboard.
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/SchemaReadyHealthCheck.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Health/HealthPolicy.cs</code-under-test>
public class MigrationReadinessDegradedTests {

  private sealed class FakeGate : ISchemaReadyGate {
    public bool IsReady => false;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default)
      => Task.Delay(Timeout.Infinite, cancellationToken);
  }

  [Test]
  public async Task LegacySchemaCheck_WhileMigrating_ReportsDegradedNotUnhealthyAsync() {
    var check = new SchemaReadyHealthCheck(new FakeGate());

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded)
      .Because("schema-initializing is an intentional startup condition, not a fault: Degraded "
             + "keeps a bounded-timeout rollout alive (HTTP 200) while staying visible — "
             + "Unhealthy makes every migration longer than the deploy timeout a rollback");
  }

  [Test]
  public async Task DefaultPolicy_MigratingOnReadiness_IsDegradedNotInvisibleAsync() {
    var status = HealthPolicy.Lenient.Map(ComponentState.Migrating, HealthProbe.Readiness);

    await Assert.That(status).IsEqualTo(HealthStatus.Degraded)
      .Because("the default should pass a bounded-timeout rollout (200) AND show the migration: "
             + "Healthy hides it from every dashboard, Unhealthy rolls the deployment back");
  }
}
