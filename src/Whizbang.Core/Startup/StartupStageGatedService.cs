// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.Startup;

/// <summary>
/// Starts a hosted service only once this instance reaches the startup stage the service needs (#1636), so nothing
/// it does touches the database before that stage.
/// </summary>
/// <remarks>
/// <para>
/// The host starts the wrapper with every other hosted service; the wrapper waits on the <see cref="IStartupStageGate"/>
/// in the background and then starts the service. Stopped before the stage is reached, the service is never started
/// and so never stopped. A service whose own loop is a <see cref="BackgroundService"/> has that loop watched: a fault
/// in it is the wrapper's fault, which the host handles exactly as it would have handled the service's.
/// </para>
/// <para>
/// With no gate registered (a host composed without the worker pipeline, which therefore records no stages) the
/// service starts with the host, as it did before it was wrapped.
/// </para>
/// </remarks>
/// <typeparam name="TService">The hosted service it starts.</typeparam>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/StartupStageGatedServiceTests.cs</tests>
public sealed class StartupStageGatedService<TService> : BackgroundService where TService : class, IHostedService {
  private readonly IStartupStageGate? _gate;
  private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Creates the wrapper.</summary>
  /// <param name="inner">The service to start at the stage.</param>
  /// <param name="stage">The startup stage the service needs.</param>
  /// <param name="gate">This instance's stage gate; null starts the service with the host, as it would start unwrapped.</param>
  public StartupStageGatedService(TService inner, StartupStage stage, IStartupStageGate? gate) {
    ArgumentNullException.ThrowIfNull(inner);
    Inner = inner;
    Stage = stage;
    _gate = gate;
  }

  /// <summary>The service this starts.</summary>
  public TService Inner { get; }

  /// <summary>The startup stage the service waits for.</summary>
  public StartupStage Stage { get; }

  /// <summary>Completes once the service has been started.</summary>
  public Task Started => _started.Task;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_gate is not null) {
      await _gate.WaitForAsync(Stage, stoppingToken).ConfigureAwait(false);
    }
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

/// <summary>Registers hosted services that start at a startup stage (#1636).</summary>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/StartupStageGatedServiceTests.cs</tests>
public static class StartupStageGatedServiceCollectionExtensions {
  /// <summary>
  /// Registers the service <paramref name="factory"/> returns as a hosted service that starts once this instance
  /// reaches <paramref name="stage"/>. Registering the same service type again changes nothing.
  /// </summary>
  /// <typeparam name="TService">The hosted service.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="stage">The startup stage the service needs.</param>
  /// <param name="factory">Resolves the service, usually a singleton registered on its own.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddHostedServiceAtStage<TService>(
      this IServiceCollection services, StartupStage stage, Func<IServiceProvider, TService> factory)
      where TService : class, IHostedService {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(factory);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupStageGatedService<TService>>(sp =>
      new StartupStageGatedService<TService>(factory(sp), stage, sp.GetService<IStartupStageGate>())));
    return services;
  }
}
