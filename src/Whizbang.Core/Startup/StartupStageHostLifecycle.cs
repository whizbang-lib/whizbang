// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// The host's own lifecycle: it enters <see cref="StartupStage.Preflight"/> when the host starts, before any hosted
/// service does, and takes the one run-control path (#2045) with <see cref="RunHaltReason.Stop"/> when the host starts
/// to stop, before any hosted service stops.
/// </summary>
/// <remarks>
/// The host calls <see cref="IHostedLifecycleService.StartingAsync"/> on every lifecycle service before any
/// <c>StartAsync</c>, so Preflight is always the first stage logged, ahead of a schema initialization that runs in the
/// background. On stopping, the path closes the run permit while every worker is still running, so the work in hand
/// finishes, what is left is released, roles are handed off, and the path writes the Stopping and Stopped phases (the
/// stage record shows them as Draining and Stopped), before the workers stop: the instance stops being eligible for work
/// at once rather than when its heartbeat lapses. The host's stop token bounds every step of the path, so releasing work
/// and roles against an unreachable database cannot overrun the host's shutdown budget; a step it cuts short is logged,
/// and the path still writes the final phase rather than turning a graceful stop into an exception.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageHostLifecycleTests.cs</tests>
public sealed class StartupStageHostLifecycle : IHostedLifecycleService {
  private readonly IStartupStageReporter _stages;
  private readonly IWhizbangRunController _runControl;

  /// <summary>Creates the service over the stage reporter and the run-control path.</summary>
  /// <param name="stages">The reporter whose stage this moves at startup.</param>
  /// <param name="runControl">The path the host's stop takes.</param>
  public StartupStageHostLifecycle(IStartupStageReporter stages, IWhizbangRunController runControl) {
    ArgumentNullException.ThrowIfNull(stages);
    ArgumentNullException.ThrowIfNull(runControl);
    _stages = stages;
    _runControl = runControl;
  }

  /// <inheritdoc />
  public Task StartingAsync(CancellationToken cancellationToken) =>
    _stages.EnterAsync(StartupStage.Preflight, "host starting", cancellationToken).AsTask();

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StoppingAsync(CancellationToken cancellationToken) =>
    _runControl.HaltAsync(RunHaltReason.Stop, cancellationToken);

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
