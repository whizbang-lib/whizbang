// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// The host's own lifecycle: it enters <see cref="StartupStage.Preflight"/> when the host starts, before any hosted
/// service does, and when the host starts to stop, before any hosted service stops, it completes the one exit path
/// (#2126), which takes the one run-control path (#2045) with <see cref="RunHaltReason.Stop"/>.
/// </summary>
/// <remarks>
/// <para>
/// The host calls <see cref="IHostedLifecycleService.StartingAsync"/> on every lifecycle service before any
/// <c>StartAsync</c>, so Preflight is always the first stage logged, ahead of a schema initialization that runs in the
/// background, and the exit path measures uptime from there.
/// </para>
/// <para>
/// Every exit ends here: a stop the host was asked for (a host shutdown), one a hosted worker's failure caused (a crash),
/// and one a cause asked the exit path for (an eviction, an operator's stop, a startup failure), which stopped the host so
/// that this completes it. The exit path closes the run permit while every worker is still running, so the work in hand
/// finishes, what is left is released, roles are handed off, and the path writes the Stopping and Stopped phases (the
/// stage record shows them as Draining and Stopped), before the workers stop; then it logs the exit's one entry, records
/// it, and sets the exit code. The host's stop token bounds every step, so releasing work and roles against an
/// unreachable database cannot overrun the host's shutdown budget; a step it cuts short is said in the entry, and the
/// exit still completes rather than turning a graceful stop into an exception.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageHostLifecycleTests.cs</tests>
public sealed class StartupStageHostLifecycle : IHostedLifecycleService {
  private readonly IStartupStageReporter _stages;
  private readonly IWhizbangInstanceExit _instanceExit;

  /// <summary>Creates the service over the stage reporter and the exit path.</summary>
  /// <param name="stages">The reporter whose stage this moves at startup.</param>
  /// <param name="instanceExit">The one exit path the host's stop completes.</param>
  public StartupStageHostLifecycle(IStartupStageReporter stages, IWhizbangInstanceExit instanceExit) {
    ArgumentNullException.ThrowIfNull(stages);
    ArgumentNullException.ThrowIfNull(instanceExit);
    _stages = stages;
    _instanceExit = instanceExit;
  }

  /// <inheritdoc />
  public Task StartingAsync(CancellationToken cancellationToken) {
    _instanceExit.OnHostStarting();
    return _stages.EnterAsync(StartupStage.Preflight, "host starting", cancellationToken).AsTask();
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StoppingAsync(CancellationToken cancellationToken) => _instanceExit.OnHostStoppingAsync(cancellationToken);

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
