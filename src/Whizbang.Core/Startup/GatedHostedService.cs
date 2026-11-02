// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.Startup;

/// <summary>
/// Starts a hosted service only once this instance reaches the point in startup the service needs (#1636), so the
/// service itself holds no gate code: when it may start is a fact of its registration, and the host enforces it.
/// </summary>
/// <remarks>
/// <para>
/// The host starts the wrapper with every other hosted service; the wrapper waits for the start point in the
/// background and then starts the service. Stopped before the point is reached, the service is never started and so
/// never stopped. A service whose own loop is a <see cref="BackgroundService"/> has that loop watched: a fault in it is
/// the wrapper's fault, which the host handles exactly as it would have handled the service's.
/// </para>
/// <para>
/// Register a worker through <see cref="WhizbangWorkerServiceCollectionExtensions.AddWhizbangWorker{TWorker}(Microsoft.Extensions.DependencyInjection.IServiceCollection, StartupStage)"/>
/// rather than deriving from this.
/// </para>
/// </remarks>
/// <typeparam name="TService">The hosted service it starts.</typeparam>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/StartupStageGatedServiceTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/WhizbangWorkerRegistrationTests.cs</tests>
public abstract class GatedHostedService<TService> : BackgroundService where TService : class, IHostedService {
  private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Creates the wrapper.</summary>
  /// <param name="inner">The service to start at the start point.</param>
  protected GatedHostedService(TService inner) {
    ArgumentNullException.ThrowIfNull(inner);
    Inner = inner;
  }

  /// <summary>The service this starts.</summary>
  public TService Inner { get; }

  /// <summary>Completes once the service has been started.</summary>
  public Task Started => _started.Task;

  /// <summary>Completes once this instance has reached the point the service starts at.</summary>
  /// <param name="stoppingToken">Ends the wait when the host stops.</param>
  /// <returns>A task that completes at the start point, or is canceled with the token.</returns>
  protected abstract Task WaitToStartAsync(CancellationToken stoppingToken);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    await WaitToStartAsync(stoppingToken).ConfigureAwait(false);
    await Inner.StartAsync(stoppingToken).ConfigureAwait(false);
    _started.TrySetResult();
    if (Inner is BackgroundService background) {
      // Set by StartAsync, which has returned.
      await background.ExecuteTask!.WaitAsync(stoppingToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await base.StopAsync(cancellationToken).ConfigureAwait(false);
    if (_started.Task.IsCompleted) {
      await Inner.StopAsync(cancellationToken).ConfigureAwait(false);
    }
  }
}
