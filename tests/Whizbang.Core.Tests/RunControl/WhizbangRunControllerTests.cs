using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers the run-control (killswitch) core: <see cref="WhizbangRunControlOptions.Resolve"/> (override
/// → drain → phase-table → Running) and <see cref="WhizbangRunController"/> applying the resolved
/// run-state to each control on a phase transition and honoring operator overrides. This is the
/// enforcement half of "serve reads, pause writes + processing during migration".
/// </summary>
public class WhizbangRunControllerTests {

  private sealed class FakeControl(string component) : IWhizbangRunControl {
    public string Component { get; } = component;
    public RunState Current { get; private set; } = RunState.Running;
    public ValueTask ApplyAsync(RunState desired, CancellationToken cancellationToken) {
      Current = desired;
      return default;
    }
  }

  // ---- WhizbangRunControlOptions.Resolve ----

  [Test]
  public async Task Resolve_OverrideWinsAsync() {
    var options = WhizbangRunControlOptions.Default();
    options.Overrides["workers"] = RunState.Stopped;
    await Assert.That(options.Resolve("workers", LifecyclePhase.Ready)).IsEqualTo(RunState.Stopped);
  }

  [Test]
  public async Task Resolve_DrainingStopsEverythingAsync() {
    var options = new WhizbangRunControlOptions();
    await Assert.That(options.Resolve("anything", LifecyclePhase.Draining)).IsEqualTo(RunState.Stopped);
  }

  [Test]
  public async Task Resolve_DefaultPausesWorkersDuringMigrationAsync() {
    var options = WhizbangRunControlOptions.Default();
    await Assert.That(options.Resolve("workers", LifecyclePhase.Migrating)).IsEqualTo(RunState.Paused);
  }

  [Test]
  public async Task Resolve_UnlistedComponentRunsAsync() {
    var options = WhizbangRunControlOptions.Default();
    await Assert.That(options.Resolve("reads", LifecyclePhase.Migrating)).IsEqualTo(RunState.Running);
  }

  // ---- WhizbangRunController ----

  [Test]
  public async Task Transition_Migrating_PausesWorkers_KeepsReadsRunningAsync() {
    var workers = new FakeControl("workers");
    var reads = new FakeControl("reads");
    var controller = new WhizbangRunController([workers, reads], WhizbangRunControlOptions.Default());

    await controller.TransitionAsync(LifecyclePhase.Migrating, CancellationToken.None);

    await Assert.That(workers.Current).IsEqualTo(RunState.Paused);
    await Assert.That(reads.Current).IsEqualTo(RunState.Running);
  }

  [Test]
  public async Task Transition_Draining_StopsEverythingAsync() {
    var workers = new FakeControl("workers");
    var reads = new FakeControl("reads");
    var controller = new WhizbangRunController([workers, reads], WhizbangRunControlOptions.Default());

    await controller.TransitionAsync(LifecyclePhase.Draining, CancellationToken.None);

    await Assert.That(workers.Current).IsEqualTo(RunState.Stopped);
    await Assert.That(reads.Current).IsEqualTo(RunState.Stopped);
  }

  [Test]
  public async Task SetOverride_ForcesComponent_ThenClearRestoresPhaseAsync() {
    var workers = new FakeControl("workers");
    var options = WhizbangRunControlOptions.Default();
    var controller = new WhizbangRunController([workers], options);

    // Force stop even though the phase is Ready (would otherwise be Running).
    await controller.SetOverrideAsync("workers", RunState.Stopped, LifecyclePhase.Ready, CancellationToken.None);
    await Assert.That(workers.Current).IsEqualTo(RunState.Stopped);

    // Clear the override -> re-resolve under Ready -> Running.
    await controller.SetOverrideAsync("workers", null, LifecyclePhase.Ready, CancellationToken.None);
    await Assert.That(workers.Current).IsEqualTo(RunState.Running);
  }
}
