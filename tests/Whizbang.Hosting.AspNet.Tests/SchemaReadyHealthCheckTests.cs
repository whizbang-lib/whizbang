using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers <see cref="SchemaReadyHealthCheck"/>: Unhealthy until the schema-ready gate signals, then
/// Healthy — the readiness signal that keeps a non-blocking-init host out of traffic rotation during
/// migration without failing liveness.
/// </summary>
public class SchemaReadyHealthCheckTests {

  private sealed class FakeGate(bool ready) : ISchemaReadyGate {
    public bool IsReady { get; private set; } = ready;
    public void MarkReady() => IsReady = true;
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
  }

  [Test]
  public async Task NotReady_ReportsUnhealthyAsync() {
    var check = new SchemaReadyHealthCheck(new FakeGate(ready: false));

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
  }

  [Test]
  public async Task Ready_ReportsHealthyAsync() {
    var check = new SchemaReadyHealthCheck(new FakeGate(ready: true));

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }
}
