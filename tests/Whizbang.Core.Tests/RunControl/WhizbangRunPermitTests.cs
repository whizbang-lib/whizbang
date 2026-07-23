using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="WhizbangRunPermit"/> (the re-closable run gate) and <see cref="RunPermitControl"/>
/// (the adapter that flips it): Running is open, Paused blocks until resumed, Stopped cancels awaiters.
/// Completion-signal based — no timers/delays.
/// </summary>
public class WhizbangRunPermitTests {

  [Test]
  public async Task Running_ReturnsImmediatelyAsync() {
    var permit = new WhizbangRunPermit();
    await permit.WaitAsync(CancellationToken.None);
    await Assert.That(permit.IsRunning).IsTrue();
  }

  [Test]
  public async Task Paused_BlocksUntilResumedAsync() {
    var permit = new WhizbangRunPermit();
    permit.Set(RunState.Paused);

    var waiter = permit.WaitAsync(CancellationToken.None);
    await Assert.That(waiter.IsCompleted).IsFalse(); // blocked while paused

    permit.Set(RunState.Running);
    await waiter; // resumes
    await Assert.That(waiter.IsCompletedSuccessfully).IsTrue();
  }

  [Test]
  public async Task Stopped_CancelsAwaitersAsync() {
    var permit = new WhizbangRunPermit();
    permit.Set(RunState.Stopped);
    await Assert.That(async () => await permit.WaitAsync(CancellationToken.None))
      .ThrowsExactly<TaskCanceledException>();
  }

  [Test]
  public async Task Adapter_InterpretsPhaseIntoPermitAsync() {
    var permit = new WhizbangRunPermit();
    var adapter = RunPermitControl.ForWorkers("workers", permit);

    await adapter.OnPhaseAsync(LifecyclePhase.Migrating, CancellationToken.None);

    await Assert.That(permit.State).IsEqualTo(RunState.Paused);
    await Assert.That(adapter.Current).IsEqualTo(RunState.Paused);
    await Assert.That(adapter.Component).IsEqualTo("workers");
  }

  [Test]
  public async Task Adapter_DrivenByCoordinator_PausesThenRunsThenDrainsAsync() {
    var permit = new WhizbangRunPermit();
    var coordinator = new WhizbangLifecycleCoordinator(
      [RunPermitControl.ForWorkers("workers", permit)], new WhizbangLifecycleOptions());

    await coordinator.TransitionAsync(LifecyclePhase.Migrating, CancellationToken.None);
    await Assert.That(permit.State).IsEqualTo(RunState.Paused); // paused during migration

    await coordinator.TransitionAsync(LifecyclePhase.Running, CancellationToken.None);
    await Assert.That(permit.State).IsEqualTo(RunState.Running); // resumes when running

    await coordinator.TransitionAsync(LifecyclePhase.Stopping, CancellationToken.None);
    await Assert.That(permit.State).IsEqualTo(RunState.Stopped); // drains on stopping
  }

  [Test]
  [Arguments(LifecyclePhase.Running, RunState.Running)]
  [Arguments(LifecyclePhase.Migrating, RunState.Paused)]
  [Arguments(LifecyclePhase.Connecting, RunState.Paused)]
  [Arguments(LifecyclePhase.Paused, RunState.Paused)]
  [Arguments(LifecyclePhase.Stopping, RunState.Stopped)]
  [Arguments(LifecyclePhase.Stopped, RunState.Stopped)]
  [Arguments(LifecyclePhase.Faulted, RunState.Stopped)]
  public async Task ForWorkers_InterpretationAsync(LifecyclePhase phase, RunState expected)
    => await Assert.That(RunPermitControl.InterpretForWorkers(phase)).IsEqualTo(expected);
}
