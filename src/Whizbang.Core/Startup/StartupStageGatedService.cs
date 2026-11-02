// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;

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
/// With no stage gate registered (a host composed without the worker pipeline, which therefore records no stages) a
/// service that needs <see cref="StartupStage.Coordinate"/> or an earlier stage starts with the host, as it did before
/// it was wrapped, and one that needs a later stage starts when the schema-ready gate opens, the latest point such a
/// host has; with neither gate it starts with the host.
/// </para>
/// </remarks>
/// <typeparam name="TService">The hosted service it starts.</typeparam>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/StartupStageGatedServiceTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/WhizbangWorkerRegistrationTests.cs</tests>
public sealed class StartupStageGatedService<TService> : GatedHostedService<TService> where TService : class, IHostedService {
  private readonly IStartupStageGate? _gate;
  private readonly ISchemaReadyGate? _schemaReady;

  /// <summary>Creates the wrapper.</summary>
  /// <param name="inner">The service to start at the stage.</param>
  /// <param name="stage">The startup stage the service needs.</param>
  /// <param name="gate">This instance's stage gate; null starts the service with the host, as it would start unwrapped.</param>
  public StartupStageGatedService(TService inner, StartupStage stage, IStartupStageGate? gate)
    : this(inner, stage, gate, schemaReady: null) {
  }

  /// <summary>Creates the wrapper, with the schema-ready gate to fall back on when there is no stage gate.</summary>
  /// <param name="inner">The service to start at the stage.</param>
  /// <param name="stage">The startup stage the service needs.</param>
  /// <param name="gate">This instance's stage gate.</param>
  /// <param name="schemaReady">
  /// The schema-ready gate, waited for instead when there is no stage gate and <paramref name="stage"/> comes after
  /// <see cref="StartupStage.Coordinate"/>.
  /// </param>
  public StartupStageGatedService(TService inner, StartupStage stage, IStartupStageGate? gate, ISchemaReadyGate? schemaReady)
    : base(inner) {
    Stage = stage;
    _gate = gate;
    _schemaReady = schemaReady;
  }

  /// <summary>The startup stage the service waits for.</summary>
  public StartupStage Stage { get; }

  /// <inheritdoc />
  protected override Task WaitToStartAsync(CancellationToken stoppingToken) {
    if (_gate is not null) {
      return _gate.WaitForAsync(Stage, stoppingToken);
    }
    return Stage > StartupStage.Coordinate && _schemaReady is not null
      ? _schemaReady.WaitForReadyAsync(stoppingToken)
      : Task.CompletedTask;
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
      new StartupStageGatedService<TService>(
        factory(sp), stage, sp.GetService<IStartupStageGate>(), sp.GetService<ISchemaReadyGate>())));
    return services;
  }
}
