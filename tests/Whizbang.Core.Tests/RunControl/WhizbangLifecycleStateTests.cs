using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="WhizbangLifecycleState"/>: advancing the phase updates <see cref="IWhizbangLifecycleState.Phase"/>
/// and drives the controller — so Migrating pauses the default-paused components and Ready resumes them.
/// </summary>
public class WhizbangLifecycleStateTests {

  private sealed class FakeControl(string component) : IWhizbangRunControl {
    public string Component { get; } = component;
    public RunState Current { get; private set; } = RunState.Running;
    public ValueTask ApplyAsync(RunState desired, CancellationToken cancellationToken) {
      Current = desired;
      return default;
    }
  }

  private static (WhizbangLifecycleState state, FakeControl workers) _build() {
    var workers = new FakeControl("workers");
    var controller = new WhizbangRunController([workers], WhizbangRunControlOptions.Default());
    return (new WhizbangLifecycleState(controller), workers);
  }

  [Test]
  public async Task AdvanceTo_Migrating_PausesThenReady_ResumesAsync() {
    var (state, workers) = _build();

    await state.AdvanceToAsync(LifecyclePhase.Migrating, CancellationToken.None);
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Migrating);
    await Assert.That(workers.Current).IsEqualTo(RunState.Paused);

    await state.AdvanceToAsync(LifecyclePhase.Ready, CancellationToken.None);
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Ready);
    await Assert.That(workers.Current).IsEqualTo(RunState.Running);
  }

  [Test]
  public async Task StartsAtStartingAsync() {
    var (state, _) = _build();
    await Assert.That(state.Phase).IsEqualTo(LifecyclePhase.Starting);
  }
}
