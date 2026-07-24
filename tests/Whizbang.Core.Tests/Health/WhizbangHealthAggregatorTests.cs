using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Locks the managed-resource health model (proposal: resilience/managed-resource-health): the
/// framework maps each source's raw <see cref="ComponentState"/> through its effective
/// <see cref="HealthPolicy"/> and aggregates worst-wins. Key invariants: intentional states are
/// healthy under the Lenient default (pod ready + serving during migration), liveness never fails
/// for an intentional state under any policy, and a per-component override changes only that
/// component. See <see cref="feedback_lock_invariants_in_tests"/>.
/// </summary>
public class WhizbangHealthAggregatorTests {

  private sealed class FakeSource(string component, ComponentState state, string? detail = null)
      : IWhizbangHealthSource {
    public string Component { get; } = component;
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
      => new(new ComponentHealth(state, detail));
  }

  private static WhizbangHealthAggregator _agg(WhizbangHealthOptions options, params IWhizbangHealthSource[] sources)
    => new(sources, options);

  [Test]
  public async Task LenientDefault_Migrating_IsReadyAsync() {
    var result = await _agg(new WhizbangHealthOptions(), new FakeSource("schema", ComponentState.Migrating))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task LenientDefault_Migrating_IsAliveAsync() {
    var result = await _agg(new WhizbangHealthOptions(), new FakeSource("schema", ComponentState.Migrating))
      .EvaluateAsync(HealthProbe.Liveness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task Faulted_FailsReadinessAsync() {
    var result = await _agg(new WhizbangHealthOptions(), new FakeSource("transport", ComponentState.Faulted))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
  }

  [Test]
  public async Task Faulted_StillAlive_NeverRestartsOnDependencyFaultAsync() {
    var result = await _agg(new WhizbangHealthOptions(), new FakeSource("transport", ComponentState.Faulted))
      .EvaluateAsync(HealthProbe.Liveness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task Aggregate_WorstStatusWinsAsync() {
    var result = await _agg(new WhizbangHealthOptions(),
        new FakeSource("event-store", ComponentState.Operational),
        new FakeSource("offload", ComponentState.Faulted))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Components.Count).IsEqualTo(2);
  }

  [Test]
  public async Task StrictOverride_HoldsThatComponentOutOfRotationAsync() {
    var options = new WhizbangHealthOptions();
    options.Components["schema"] = HealthPolicy.Strict;
    var result = await _agg(options, new FakeSource("schema", ComponentState.Migrating))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
  }

  [Test]
  public async Task StrictOverride_DoesNotAffectOtherComponentsAsync() {
    var options = new WhizbangHealthOptions();
    options.Components["schema"] = HealthPolicy.Strict;
    // A different component, migrating, still Lenient -> Ready.
    var result = await _agg(options, new FakeSource("event-store", ComponentState.Migrating))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  public async Task Liveness_NeverFailsForIntentionalStates_UnderStrictAsync() {
    var options = new WhizbangHealthOptions { Default = HealthPolicy.Strict };
    foreach (var state in new[] {
        ComponentState.Starting, ComponentState.Connecting, ComponentState.Migrating,
        ComponentState.PausedByDesign, ComponentState.Draining }) {
      var result = await _agg(options, new FakeSource("workers", state))
        .EvaluateAsync(HealthProbe.Liveness, CancellationToken.None);
      await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    }
  }

  [Test]
  [Arguments(ComponentState.Connecting)]
  [Arguments(ComponentState.Draining)]
  public async Task LenientDefault_IntentionalState_IsReadyAsync(ComponentState state) {
    var result = await _agg(new WhizbangHealthOptions(), new FakeSource("transport", state))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }

  [Test]
  [Arguments(ComponentState.Connecting)]
  [Arguments(ComponentState.Draining)]
  public async Task StrictOverride_IntentionalState_HeldOutOfRotationAsync(ComponentState state) {
    var options = new WhizbangHealthOptions { Default = HealthPolicy.Strict };
    var result = await _agg(options, new FakeSource("transport", state))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
  }

  [Test]
  public async Task Detail_IsPropagatedToTheReportAsync() {
    var result = await _agg(new WhizbangHealthOptions(),
        new FakeSource("schema", ComponentState.Migrating, "migrating: step 7/12"))
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Components[0].Detail).IsEqualTo("migrating: step 7/12");
    await Assert.That(result.Components[0].State).IsEqualTo(ComponentState.Migrating);
  }

  [Test]
  public async Task NoSources_IsHealthyAsync() {
    var result = await _agg(new WhizbangHealthOptions())
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
  }
}
