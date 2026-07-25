using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers <see cref="SchemaHealthSource"/>: it judges its state against the current lifecycle phase.
/// Gate closed while Migrating ⇒ <see cref="ComponentState.Migrating"/> (ready under Lenient); gate open
/// ⇒ <see cref="ComponentState.Operational"/>; a failed init drives Faulted ⇒
/// <see cref="ComponentState.Degraded"/> (warning) then Halted ⇒ <see cref="ComponentState.Faulted"/>
/// (failure) — so a genuine failure surfaces (warning → failure) instead of reading healthy forever.
/// </summary>
public class SchemaHealthSourceTests {

  private sealed class FakeGate(bool ready) : ISchemaReadyGate {
    public bool IsReady { get; } = ready;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
  }

  private sealed class FakeLifecycle(LifecyclePhase phase) : IWhizbangLifecycleState {
    public LifecyclePhase Phase { get; } = phase;
    public ValueTask AdvanceToAsync(LifecyclePhase p, CancellationToken cancellationToken) => default;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => default;
  }

  private static async Task<ComponentState> _reportAsync(bool ready, LifecyclePhase phase) {
    var source = new SchemaHealthSource(new FakeGate(ready), new FakeLifecycle(phase));
    var health = await source.ReportAsync(CancellationToken.None);
    return health.State;
  }

  [Test]
  public async Task GateClosed_WhileMigrating_ReportsMigratingAsync()
    => await Assert.That(await _reportAsync(ready: false, LifecyclePhase.Migrating)).IsEqualTo(ComponentState.Migrating);

  [Test]
  public async Task GateOpen_ReportsOperationalAsync()
    => await Assert.That(await _reportAsync(ready: true, LifecyclePhase.Running)).IsEqualTo(ComponentState.Operational);

  [Test]
  public async Task GateClosed_WhileConnecting_ReportsConnectingAsync()
    => await Assert.That(await _reportAsync(ready: false, LifecyclePhase.Connecting)).IsEqualTo(ComponentState.Connecting);

  // A failed init drives Faulted -> (record window) -> Halted. The record window is a warning
  // (Degraded ⇒ readiness Degraded); the terminal Halted is the failure (Faulted ⇒ readiness Unhealthy).
  [Test]
  public async Task FaultedPhase_ReportsDegradedWarningAsync()
    => await Assert.That(await _reportAsync(ready: false, LifecyclePhase.Faulted)).IsEqualTo(ComponentState.Degraded);

  [Test]
  public async Task HaltedPhase_ReportsFaultedFailureAsync()
    => await Assert.That(await _reportAsync(ready: false, LifecyclePhase.Halted)).IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task Component_IsSchemaAsync() {
    var source = new SchemaHealthSource(new FakeGate(ready: false), new FakeLifecycle(LifecyclePhase.Migrating));
    await Assert.That(source.Component).IsEqualTo("schema");
  }
}
