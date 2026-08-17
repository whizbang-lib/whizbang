using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers <see cref="SchemaReadyHealthCheck"/>: Degraded until the schema-ready gate signals, then
/// Healthy — visible-but-serving (HTTP 200), so a bounded-timeout rollout completes during a long
/// migration while the availability gate and data-plane seams refuse what cannot be served yet.
/// </summary>
public class SchemaReadyHealthCheckTests {

  private sealed class FakeGate(bool ready) : ISchemaReadyGate {
    public bool IsReady { get; private set; } = ready;
    public void MarkReady() => IsReady = true;
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
  }

  [Test]
  public async Task NotReady_ReportsDegradedNotUnhealthyAsync() {
    var check = new SchemaReadyHealthCheck(new FakeGate(ready: false));

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded)
      .Because("an intentional startup condition must not roll a deployment back — Unhealthy made "
             + "every migration longer than the deploy timeout a rollback");
  }

  [Test]
  public async Task Ready_ReportsHealthyAsync() {
    var check = new SchemaReadyHealthCheck(new FakeGate(ready: true));

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }
}
