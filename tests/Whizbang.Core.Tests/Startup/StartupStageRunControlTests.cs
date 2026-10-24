// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The startup stage moves with the lifecycle (#1636): the transitions into AcceptingCommands and Running enter
/// stages 60 and 70, a stop enters Draining then Stopped, and every other phase leaves the stage where it is. Riding
/// the broadcast makes the stage part of the transition, which awaits it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageRunControl.cs</code-under-test>
[Category("Startup")]
public class StartupStageRunControlTests {
  [Test]
  [Arguments(LifecyclePhase.AcceptingCommands,
    "Enter AcceptingCommands: the schema is ready; commands are accepted while the read models catch up")]
  [Arguments(LifecyclePhase.Running, "Enter Running: reads and writes are served")]
  [Arguments(LifecyclePhase.Stopping, "Enter Draining: stopping, finishing in-flight work")]
  [Arguments(LifecyclePhase.Stopped, "Enter Stopped: stopped")]
  public async Task APhaseThatIsAStage_EntersItAsync(LifecyclePhase phase, string expected) {
    var stages = new RecordingStageReporter();
    var control = new StartupStageRunControl(stages);
    using var cts = new CancellationTokenSource();

    await control.OnPhaseAsync(phase, cts.Token);

    await Assert.That(stages.Calls).IsEquivalentTo([expected]);
    await Assert.That(stages.Tokens[0]).IsEqualTo(cts.Token);
  }

  [Test]
  [Arguments(LifecyclePhase.Starting)]
  [Arguments(LifecyclePhase.Connecting)]
  [Arguments(LifecyclePhase.Migrating)]
  [Arguments(LifecyclePhase.Pausing)]
  [Arguments(LifecyclePhase.Paused)]
  [Arguments(LifecyclePhase.StandingBy)]
  [Arguments(LifecyclePhase.Resuming)]
  [Arguments(LifecyclePhase.Faulted)]
  [Arguments(LifecyclePhase.Halted)]
  public async Task AnyOtherPhase_LeavesTheStageWhereItIsAsync(LifecyclePhase phase) {
    var stages = new RecordingStageReporter();
    var control = new StartupStageRunControl(stages);

    await control.OnPhaseAsync(phase, CancellationToken.None);

    await Assert.That(stages.Calls).IsEmpty()
      .Because("the startup phases are entered where the work happens, and a pause is not a stage");
  }

  [Test]
  public async Task TheParticipant_IsNamedAndNeedsAReporterAsync() {
    await Assert.That(new StartupStageRunControl(new RecordingStageReporter()).Component).IsEqualTo("startup-stage");
    await Assert.That(() => new StartupStageRunControl(null!)).Throws<ArgumentNullException>();
  }
}
