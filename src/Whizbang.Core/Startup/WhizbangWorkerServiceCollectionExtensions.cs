// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// Hosts a background worker with the point in startup it starts at (#1636). The host starts the worker there and stops
/// it normally on shutdown, so the worker holds no gate code of its own and cannot start early by forgetting one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddWhizbangWorker{TWorker}(IServiceCollection, StartupStage)"/> is the path for every worker, the
/// application's included: with no stage named it starts the worker at <see cref="StartupStage.AcceptingCommands"/>,
/// once this instance's row records that it is eligible for work, which is the safe point for anything that reads or
/// writes the message tables. A worker registered with <c>AddHostedService</c> instead starts with the host, before the
/// schema is migrated.
/// </para>
/// <para>
/// <see cref="AddWhizbangWorkerAtSchemaReady{TWorker}(IServiceCollection)"/> starts a worker when the schema-ready gate
/// opens, at the end of <see cref="StartupStage.Coordinate"/>: the framework uses it for the work that keeps an instance
/// coordinated while it is not yet eligible for work. The heartbeat starts earlier, at
/// <see cref="HeartbeatWorker.StartsAt"/>, right after the instance registers (#2126).
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/WhizbangWorkerRegistrationTests.cs</tests>
public static class WhizbangWorkerServiceCollectionExtensions {
  /// <summary>
  /// Registers <typeparamref name="TWorker"/> as a singleton (unless one is registered already) and hosts it, starting
  /// once this instance reaches <paramref name="stage"/>. Registering the same worker again changes nothing.
  /// </summary>
  /// <typeparam name="TWorker">The worker.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="stage">The startup stage the worker needs; Accept commands unless the worker needs another.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangWorker<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWorker>(
      this IServiceCollection services, StartupStage stage = StartupStage.AcceptingCommands)
      where TWorker : class, IHostedService {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton<TWorker>();
    return services.AddHostedServiceAtStage(stage, static sp => sp.GetRequiredService<TWorker>());
  }

  /// <summary>
  /// Hosts the worker <paramref name="factory"/> returns, starting once this instance reaches <paramref name="stage"/>.
  /// Registering the same worker type again changes nothing.
  /// </summary>
  /// <typeparam name="TWorker">The worker.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="stage">The startup stage the worker needs.</param>
  /// <param name="factory">Resolves the worker, usually a singleton registered on its own.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangWorker<TWorker>(
      this IServiceCollection services, StartupStage stage, Func<IServiceProvider, TWorker> factory)
      where TWorker : class, IHostedService =>
    services.AddHostedServiceAtStage(stage, factory);

  /// <summary>
  /// Registers <typeparamref name="TWorker"/> as a singleton (unless one is registered already) and hosts it, starting
  /// when the schema-ready gate opens. Registering the same worker again changes nothing.
  /// </summary>
  /// <typeparam name="TWorker">The worker.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangWorkerAtSchemaReady<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWorker>(
      this IServiceCollection services)
      where TWorker : class, IHostedService {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton<TWorker>();
    return services.AddWhizbangWorkerAtSchemaReady(static sp => sp.GetRequiredService<TWorker>());
  }

  /// <summary>
  /// Hosts the worker <paramref name="factory"/> returns, starting when the schema-ready gate opens. Registering the same
  /// worker type again changes nothing.
  /// </summary>
  /// <typeparam name="TWorker">The worker.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="factory">Resolves the worker, usually a singleton registered on its own.</param>
  /// <returns>The same collection.</returns>
  public static IServiceCollection AddWhizbangWorkerAtSchemaReady<TWorker>(
      this IServiceCollection services, Func<IServiceProvider, TWorker> factory)
      where TWorker : class, IHostedService {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(factory);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SchemaReadyGatedService<TWorker>>(sp =>
      new SchemaReadyGatedService<TWorker>(factory(sp), StartupStageGatedServiceCollectionExtensions.SchemaReadyGateOf(sp))));
    return services;
  }
}
