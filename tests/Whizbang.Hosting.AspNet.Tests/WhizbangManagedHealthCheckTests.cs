// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  public async Task Readiness_MigratingUnderLenient_IsDegradedButServingAsync() {
    var result = await _check(HealthProbe.Readiness, new FakeSource("schema", ComponentState.Migrating))
      .CheckHealthAsync(new HealthCheckContext());
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded)
      .Because("HTTP 200 — bounded-timeout rollouts complete — while the migration stays visible");
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

  /// <summary>
  /// A component's detail, when it reports one, is what tells an operator why it is in that state,
  /// so it rides along in the component's data entry; a component without one gets no empty
  /// parentheses.
  /// </summary>
  [Test]
  public async Task Result_ComponentDetail_AppearsInItsDataEntryOnlyWhenReportedAsync() {
    var result = await _check(HealthProbe.Readiness,
        new DetailSource("offload", new ComponentHealth(ComponentState.Faulted, "connection refused")),
        new DetailSource("schema", new ComponentHealth(ComponentState.Operational)))
      .CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Data["offload"]).IsEqualTo("Faulted => Unhealthy (connection refused)");
    await Assert.That(result.Data["schema"]).IsEqualTo("Operational => Healthy");
  }

  private sealed class DetailSource(string component, ComponentHealth health) : IWhizbangHealthSource {
    public string Component { get; } = component;
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) => new(health);
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

  // --- the startup stage (#1636) ---------------------------------------------------------------

  [Test]
  public async Task Result_CarriesTheStartupStagesNumberNameAndMessageAsync() {
    var stages = new StageReporter(new Whizbang.Core.Startup.StartupStageSnapshot(
      Whizbang.Core.Startup.StartupStage.AcceptingCommands, "commands are accepted", false, DateTimeOffset.UnixEpoch));
    var check = new WhizbangManagedHealthCheck(
      new WhizbangHealthAggregator([new FakeSource("schema", ComponentState.Operational)], new WhizbangHealthOptions(), stages),
      HealthProbe.Readiness);

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Data["startup_stage"]).IsEqualTo(60);
    await Assert.That(result.Data["startup_stage_name"]).IsEqualTo("Accept commands");
    await Assert.That(result.Data["startup_stage_message"]).IsEqualTo("commands are accepted");
    await Assert.That(result.Data.ContainsKey("schema")).IsTrue();
  }

  [Test]
  public async Task Result_WithNoStage_CarriesNoStageKeysAsync() {
    var result = await _check(HealthProbe.Readiness, new FakeSource("schema", ComponentState.Operational))
      .CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Data.Keys).DoesNotContain(k => k.StartsWith("startup_stage", StringComparison.Ordinal));
  }

  private sealed class StageReporter(Whizbang.Core.Startup.StartupStageSnapshot current)
      : Whizbang.Core.Startup.IStartupStageReporter {
    public Whizbang.Core.Startup.StartupStageSnapshot? Current { get; } = current;
    public ValueTask EnterAsync(Whizbang.Core.Startup.StartupStage stage, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask SkipAsync(Whizbang.Core.Startup.StartupStage stage, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }
}
