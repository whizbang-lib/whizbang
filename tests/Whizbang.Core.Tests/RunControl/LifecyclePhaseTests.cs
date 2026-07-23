using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Covers <see cref="LifecyclePhaseExtensions"/>: transitional phases are the coordinator ack-barrier
/// windows; settled phases are resting points; terminal phases are end states. Locks the classification
/// so the state machine's shape can't silently drift.
/// </summary>
public class LifecyclePhaseTests {

  [Test]
  [Arguments(LifecyclePhase.Starting)]
  [Arguments(LifecyclePhase.Connecting)]
  [Arguments(LifecyclePhase.Migrating)]
  [Arguments(LifecyclePhase.Pausing)]
  [Arguments(LifecyclePhase.Resuming)]
  [Arguments(LifecyclePhase.Stopping)]
  [Arguments(LifecyclePhase.Faulted)]
  public async Task TransitionalPhases_AreTransitional_NotSettledAsync(LifecyclePhase phase) {
    await Assert.That(phase.IsTransitional()).IsTrue();
    await Assert.That(phase.IsSettled()).IsFalse();
  }

  [Test]
  [Arguments(LifecyclePhase.Running)]
  [Arguments(LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.Stopped)]
  [Arguments(LifecyclePhase.Halted)]
  public async Task SettledPhases_AreSettled_NotTransitionalAsync(LifecyclePhase phase) {
    await Assert.That(phase.IsSettled()).IsTrue();
    await Assert.That(phase.IsTransitional()).IsFalse();
  }

  [Test]
  [Arguments(LifecyclePhase.Stopped)]
  [Arguments(LifecyclePhase.Halted)]
  public async Task TerminalPhases_AreTerminalAsync(LifecyclePhase phase)
    => await Assert.That(phase.IsTerminal()).IsTrue();

  [Test]
  [Arguments(LifecyclePhase.Running)]
  [Arguments(LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.Migrating)]
  public async Task NonTerminalPhases_AreNotTerminalAsync(LifecyclePhase phase)
    => await Assert.That(phase.IsTerminal()).IsFalse();
}
