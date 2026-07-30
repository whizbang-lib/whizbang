using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers <see cref="WhizbangManagedHealthCheck"/>: the bridge from the aggregator's worst-status
/// result to an ASP.NET <see cref="HealthCheckResult"/>, per probe. Migrating is ready under the
/// Lenient default; a real fault fails readiness but never liveness.
/// </summary>
public class WhizbangManagedHealthCheckTests {

  private sealed class FakeSource(string component, ComponentState state) : IWhizbangHealthSource {
    public string Component { get; } = component;
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
      => new(new ComponentHealth(state));
  }

  private static WhizbangManagedHealthCheck _check(HealthProbe probe, params IWhizbangHealthSource[] sources)
    => new(new WhizbangHealthAggregator(sources, new WhizbangHealthOptions()), probe);

  [Test]
  public async Task Readiness_MigratingUnderLenient_IsHealthyAsync() {
    var result = await _check(HealthProbe.Readiness, new FakeSource("schema", ComponentState.Migrating))
      .CheckHealthAsync(new HealthCheckContext());
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task Readiness_Faulted_IsUnhealthyAsync() {
    var result = await _check(HealthProbe.Readiness, new FakeSource("offload", ComponentState.Faulted))
      .CheckHealthAsync(new HealthCheckContext());
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
  }

  [Test]
  public async Task Liveness_Faulted_StaysHealthyAsync() {
    var result = await _check(HealthProbe.Liveness, new FakeSource("offload", ComponentState.Faulted))
      .CheckHealthAsync(new HealthCheckContext());
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task Readiness_Degraded_IsDegradedAsync() {
    var result = await _check(HealthProbe.Readiness, new FakeSource("transport", ComponentState.Degraded))
      .CheckHealthAsync(new HealthCheckContext());
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
  }

  [Test]
  public async Task Result_IncludesPerComponentDataAsync() {
    var result = await _check(HealthProbe.Readiness, new FakeSource("schema", ComponentState.Migrating))
      .CheckHealthAsync(new HealthCheckContext());
    await Assert.That(result.Data.ContainsKey("schema")).IsTrue();
  }

  [Test]
  public async Task Result_DuplicateComponentNames_SurfacesEveryReportWithoutThrowingAsync() {
    // Sources sharing a component name (e.g. a per-context source registered by more than one
    // pipeline) must never kill the health endpoint — a diagnostic bridge that throws takes the
    // pod out with a crash loop instead of reporting the very state it exists to surface.
    var result = await _check(HealthProbe.Readiness,
        new FakeSource("schema", ComponentState.Operational),
        new FakeSource("schema", ComponentState.Faulted))
      .CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);   // worst still wins
    await Assert.That(result.Data.ContainsKey("schema")).IsTrue();
    await Assert.That(result.Data.ContainsKey("schema[2]")).IsTrue();
  }
}
