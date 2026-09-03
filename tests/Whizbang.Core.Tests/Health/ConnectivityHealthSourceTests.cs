using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers <see cref="ConnectivityHealthSource"/>: a phase-aware reachability probe. Key invariants —
/// an <see cref="ConnectivityRequirement.AlwaysRequired"/> resource (the DB) reports a failed probe as
/// <see cref="ComponentState.Faulted"/> <b>even during a migration</b> (the depended-on dependency is
/// never masked); a <see cref="ConnectivityRequirement.RequiredWhenRunning"/> resource (transport,
/// offload) is only probed while Running and reports its intentional state otherwise.
/// </summary>
public class ConnectivityHealthSourceTests {

  private sealed class FakeLifecycle(LifecyclePhase phase) : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; } = phase;
    public ValueTask AdvanceToAsync(LifecyclePhase p, CancellationToken cancellationToken) => default;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => default;
  }

  private static async Task<ComponentState> _alwaysAsync(bool reachable, LifecyclePhase phase) {
    var source = ConnectivityHealthSource.AlwaysRequired(
      "event-store", _ => new ValueTask<bool>(reachable), new FakeLifecycle(phase));
    return (await source.ReportAsync(CancellationToken.None)).State;
  }

  private static async Task<ComponentState> _whenRunningAsync(bool reachable, LifecyclePhase phase) {
    var source = ConnectivityHealthSource.RequiredWhenRunning(
      "transport", _ => new ValueTask<bool>(reachable), new FakeLifecycle(phase));
    return (await source.ReportAsync(CancellationToken.None)).State;
  }

  // ---- AlwaysRequired (event-store / DB) ----

  [Test]
  public async Task Always_Running_Reachable_OperationalAsync()
    => await Assert.That(await _alwaysAsync(reachable: true, LifecyclePhase.Running)).IsEqualTo(ComponentState.Operational);

  [Test]
  public async Task Always_Running_Unreachable_FaultedAsync()
    => await Assert.That(await _alwaysAsync(reachable: false, LifecyclePhase.Running)).IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task Always_Migrating_Unreachable_IsFaulted_NotMaskedAsync()
    // The migration needs the DB — a DB fault mid-migration is real, never masked.
    => await Assert.That(await _alwaysAsync(reachable: false, LifecyclePhase.Migrating)).IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task Always_Migrating_Reachable_OperationalAsync()
    => await Assert.That(await _alwaysAsync(reachable: true, LifecyclePhase.Migrating)).IsEqualTo(ComponentState.Operational);

  [Test]
  public async Task Always_Connecting_Unreachable_StillConnectingAsync()
    => await Assert.That(await _alwaysAsync(reachable: false, LifecyclePhase.Connecting)).IsEqualTo(ComponentState.Connecting);

  [Test]
  public async Task Always_Starting_NotProbed_ReportsStartingAsync()
    => await Assert.That(await _alwaysAsync(reachable: false, LifecyclePhase.Starting)).IsEqualTo(ComponentState.Starting);

  // ---- RequiredWhenRunning (transport / offload) ----

  [Test]
  public async Task WhenRunning_Running_Unreachable_FaultedAsync()
    => await Assert.That(await _whenRunningAsync(reachable: false, LifecyclePhase.Running)).IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task WhenRunning_Running_Reachable_OperationalAsync()
    => await Assert.That(await _whenRunningAsync(reachable: true, LifecyclePhase.Running)).IsEqualTo(ComponentState.Operational);

  [Test]
  public async Task WhenRunning_Migrating_NotProbed_PausedByDesignAsync()
    // A disconnected transport during a migration is by-design — not probed, not a fault.
    => await Assert.That(await _whenRunningAsync(reachable: false, LifecyclePhase.Migrating)).IsEqualTo(ComponentState.PausedByDesign);

  [Test]
  public async Task WhenRunning_Connecting_ReportsConnectingAsync()
    => await Assert.That(await _whenRunningAsync(reachable: false, LifecyclePhase.Connecting)).IsEqualTo(ComponentState.Connecting);

  [Test]
  public async Task WhenRunning_Stopping_ReportsDrainingAsync()
    => await Assert.That(await _whenRunningAsync(reachable: true, LifecyclePhase.Stopping)).IsEqualTo(ComponentState.Draining);

  // ---- shared ----

  [Test]
  [Arguments(LifecyclePhase.Faulted)]
  [Arguments(LifecyclePhase.Halted)]
  public async Task FaultPhase_ReportsFaultedAsync(LifecyclePhase phase)
    => await Assert.That(await _alwaysAsync(reachable: true, phase)).IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task ThrowingProbe_TreatedAsUnreachableAsync() {
    var source = ConnectivityHealthSource.AlwaysRequired(
      "event-store", _ => throw new InvalidOperationException("down"), new FakeLifecycle(LifecyclePhase.Running));
    await Assert.That((await source.ReportAsync(CancellationToken.None)).State).IsEqualTo(ComponentState.Faulted);
  }

  [Test]
  public async Task CanceledProbe_PropagatesRatherThanReportingUnreachableAsync() {
    // The companion to ThrowingProbe_TreatedAsUnreachable, and the opposite answer. A probe that
    // throws cannot answer the question, so "unreachable" is the safe reading. A probe canceled
    // by shutdown answers nothing either, but calling that unreachable faults the component on
    // every deploy — the health surface would flap on the way down, and an operator watching for
    // a real outage learns to ignore it.
    var source = ConnectivityHealthSource.AlwaysRequired(
      "event-store",
      _ => throw new OperationCanceledException(),
      new FakeLifecycle(LifecyclePhase.Running));

    await Assert.That(async () => await source.ReportAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("shutdown is not an outage, and reporting it as one trains operators to ignore "
             + "the signal that matters");
  }

  [Test]
  public async Task FaultDetail_IsSurfacedAsync() {
    var source = ConnectivityHealthSource.AlwaysRequired(
      "event-store", _ => new ValueTask<bool>(false), new FakeLifecycle(LifecyclePhase.Running), "cannot reach db");
    var health = await source.ReportAsync(CancellationToken.None);
    await Assert.That(health.State).IsEqualTo(ComponentState.Faulted);
    await Assert.That(health.Detail).IsEqualTo("cannot reach db");
  }

  // ---- AssumedHealthy (placeholder — hard-coded healthy, phase-aware) ----

  [Test]
  [Arguments(LifecyclePhase.Running, ComponentState.Operational)]
  [Arguments(LifecyclePhase.Migrating, ComponentState.PausedByDesign)]
  [Arguments(LifecyclePhase.Connecting, ComponentState.Connecting)]
  [Arguments(LifecyclePhase.Stopping, ComponentState.Draining)]
  public async Task AssumedHealthy_IsPhaseAware_NeverFaultsFromProbeAsync(LifecyclePhase phase, ComponentState expected) {
    var source = ConnectivityHealthSource.AssumedHealthy("transport", new FakeLifecycle(phase));
    await Assert.That((await source.ReportAsync(CancellationToken.None)).State).IsEqualTo(expected);
  }

  [Test]
  public async Task AssumedHealthy_Component_IsSetAsync() {
    var source = ConnectivityHealthSource.AssumedHealthy("offload", new FakeLifecycle(LifecyclePhase.Running));
    await Assert.That(source.Component).IsEqualTo("offload");
  }
}
