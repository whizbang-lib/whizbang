// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// The run-control participant that moves the startup stage with the lifecycle: <c>AcceptingCommands</c> enters
/// <see cref="StartupStage.AcceptingCommands"/>, <c>Running</c> enters <see cref="StartupStage.Running"/>,
/// <c>Stopping</c> enters <see cref="StartupStage.Draining"/> and <c>Stopped</c> enters
/// <see cref="StartupStage.Stopped"/>. A phase off the startup ladder (pausing, paused, standing by, resuming,
/// faulted, halted) is recorded through <see cref="IStartupStageReporter.EnterPhaseAsync"/> with the stage kept; the
/// startup phases before Accept commands leave the stage where it is.
/// </summary>
/// <remarks>
/// Riding the broadcast makes the stage part of the transition: the coordinator awaits every participant, so the
/// transition into an eligible stage does not complete before the stage is written (or held, when the write fails).
/// The reporter never throws for a failed write, so this participant never faults a transition. Since #1636 it is the
/// only participant that writes the instance's row: one write per transition, of stage, phase and version together.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageRunControlTests.cs</tests>
public sealed class StartupStageRunControl : IWhizbangRunControl {
  private readonly IStartupStageReporter _stages;

  /// <summary>Creates the participant over the stage reporter.</summary>
  /// <param name="stages">The reporter whose stage this moves.</param>
  public StartupStageRunControl(IStartupStageReporter stages) {
    ArgumentNullException.ThrowIfNull(stages);
    _stages = stages;
  }

  /// <inheritdoc />
  public string Component => "startup-stage";

  /// <inheritdoc />
  public ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) => phase switch {
    LifecyclePhase.AcceptingCommands => _stages.EnterAsync(StartupStage.AcceptingCommands,
      "the schema is ready; commands are accepted while the read models catch up", cancellationToken),
    LifecyclePhase.Running => _stages.EnterAsync(StartupStage.Running, "reads and writes are served", cancellationToken),
    LifecyclePhase.Stopping => _stages.EnterAsync(StartupStage.Draining, "stopping, finishing in-flight work", cancellationToken),
    LifecyclePhase.Stopped => _stages.EnterAsync(StartupStage.Stopped, "stopped", cancellationToken),
    // Off the startup ladder: written as the phase alone, keeping the stage (#1636). The stage record is the only
    // writer of an instance's phase, so a pause and a stand-by reach the row here, as one write of the transition.
    LifecyclePhase.Pausing or LifecyclePhase.Paused or LifecyclePhase.StandingBy or LifecyclePhase.Resuming
      or LifecyclePhase.Faulted or LifecyclePhase.Halted => _stages.EnterPhaseAsync(phase, cancellationToken),
    // Starting, Connecting and Migrating: the startup stages are entered where that work happens, each with its phase.
    _ => ValueTask.CompletedTask,
  };
}
