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
  public async Task Adapter_AppliesRunStateToPermitAsync() {
    var permit = new WhizbangRunPermit();
    var adapter = new RunPermitControl("workers", permit);

    await adapter.ApplyAsync(RunState.Paused, CancellationToken.None);

    await Assert.That(permit.State).IsEqualTo(RunState.Paused);
    await Assert.That(adapter.Current).IsEqualTo(RunState.Paused);
    await Assert.That(adapter.Component).IsEqualTo("workers");
  }

  [Test]
  public async Task Adapter_DrivenByController_PausesPermitOnMigrationAsync() {
    var permit = new WhizbangRunPermit();
    var controller = new WhizbangRunController(
      [new RunPermitControl("workers", permit)], WhizbangRunControlOptions.Default());

    await controller.TransitionAsync(LifecyclePhase.Migrating, CancellationToken.None);
    await Assert.That(permit.State).IsEqualTo(RunState.Paused);

    await controller.TransitionAsync(LifecyclePhase.Ready, CancellationToken.None);
    await Assert.That(permit.State).IsEqualTo(RunState.Running);
  }
}
