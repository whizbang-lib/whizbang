// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.Startup;

/// <summary>
/// Enters the stages the host's own lifecycle marks: <see cref="StartupStage.Preflight"/> when the host starts, before
/// any hosted service does; <see cref="StartupStage.Draining"/> when it starts to stop, before any hosted service
/// stops; and <see cref="StartupStage.Stopped"/> once every hosted service has stopped.
/// </summary>
/// <remarks>
/// The host calls <see cref="IHostedLifecycleService.StartingAsync"/> on every lifecycle service before any
/// <c>StartAsync</c>, so Preflight is always the first stage logged, ahead of a schema initialization that runs in the
/// background. Draining is written before the workers stop, so the instance stops being eligible for work at once
/// rather than when its heartbeat lapses. The stop-side writes run on their own bounded budget rather than the host's
/// stop token, so an expired stop budget cannot turn a graceful stop into an exception.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageHostLifecycleTests.cs</tests>
public sealed class StartupStageHostLifecycle : IHostedLifecycleService {
  private readonly IStartupStageReporter _stages;

  /// <summary>Creates the service over the stage reporter.</summary>
  /// <param name="stages">The reporter whose stage this moves.</param>
  public StartupStageHostLifecycle(IStartupStageReporter stages) {
    ArgumentNullException.ThrowIfNull(stages);
    _stages = stages;
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
    _stages.EnterAsync(StartupStage.Draining, "host stopping, finishing in-flight work", CancellationToken.None).AsTask();

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public Task StoppedAsync(CancellationToken cancellationToken) =>
    _stages.EnterAsync(StartupStage.Stopped, "host stopped", CancellationToken.None).AsTask();
}
