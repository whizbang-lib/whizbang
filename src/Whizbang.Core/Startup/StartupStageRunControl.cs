// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// The run-control participant that moves the startup stage with the lifecycle: <c>AcceptingCommands</c> enters
/// <see cref="StartupStage.AcceptingCommands"/>, <c>Running</c> enters <see cref="StartupStage.Running"/>,
/// <c>Stopping</c> enters <see cref="StartupStage.Draining"/> and <c>Stopped</c> enters
/// <see cref="StartupStage.Stopped"/>. Every other phase leaves the stage where it is.
/// </summary>
/// <remarks>
/// Riding the broadcast makes the stage part of the transition: the coordinator awaits every participant, so the
/// transition into an eligible stage does not complete before the stage is written (or held, when the write fails).
/// The reporter never throws for a failed write, so this participant never faults a transition.
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
    _ => ValueTask.CompletedTask,
  };
}
