// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// DI registration for consuming collective events on the EF Core Postgres driver. Wires the
/// worker-facing pieces of the consume path: the dispatcher, the built-in tenant scope resolver, and
/// the session accessor that hands the perspective <c>DbContext</c> to the apply chain.
/// </summary>
/// <remarks>
/// The dispatcher's handlers come from the container: every assembly that declares <c>[CollectiveApplyFor]</c>
/// handlers registers its generated table as a contribution, through <c>AddWhizbang()</c>, so a host whose handlers
/// live in several assemblies dispatches to all of them and passes nothing by hand (#2238). Per-model executors are
/// registered with <see cref="AddCollectiveExecutorEFCore{TModel}"/>, one line per perspective model. Kept as explicit
/// compile-time generic calls (no <c>MakeGenericType</c>) to stay AOT-clean (L19); a source generator can emit these
/// calls for full turnkey registration as a follow-up.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveDiUnitTests.cs:AddCollectiveEventsEFCore_RegistersDispatcherResolverAccessorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveDiUnitTests.cs:AddCollectiveEventsEFCore_NullEntries_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsCollectiveEvent_OnlyIntoTheTargetRowAsync</tests>
public static class CollectiveEventsEFCoreExtensions {
  /// <summary>
  /// Registers <see cref="ICollectiveDispatcher"/>, the built-in <see cref="TenantCollectiveScopeResolver"/>,
  /// and the EF Core <see cref="ICollectiveSessionAccessor"/> bound to <typeparamref name="TDbContext"/>.
  /// Call <see cref="AddCollectiveExecutorEFCore{TModel}"/> once per collective perspective model.
  /// </summary>
  /// <remarks>
  /// The dispatcher and the replay applier read every assembly's <c>[CollectiveApplyFor]</c> handlers from the
  /// container (<see cref="CollectiveApplyRegistryServiceCollectionExtensions.GetCollectiveApplyEntries"/>): each
  /// assembly's generated table is registered by <c>AddWhizbang()</c>, for the host's own service only (#2238).
  /// </remarks>
  /// <typeparam name="TDbContext">The perspective-owning EF Core context.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/MultiAssemblyCollectiveDispatchTests.cs:TwoAssemblies_TheDispatcher_AppliesEachAssemblysCollectiveEventsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/MultiAssemblyCollectiveDispatchTests.cs:AddWhizbang_GivesTheDispatcherThisAssemblysHandlers_WithNothingPassedByHandAsync</tests>
  public static IServiceCollection AddCollectiveEventsEFCore<TDbContext>(this IServiceCollection services)
      where TDbContext : DbContext =>
    _addCollectiveEvents<TDbContext>(services, handPassed: null);

  /// <summary>
  /// The registration that took one assembly's handler table by hand. The table now joins the handlers every assembly
  /// registers, so it is no longer needed: call <see cref="AddCollectiveEventsEFCore{TDbContext}(IServiceCollection)"/>.
  /// </summary>
  /// <typeparam name="TDbContext">The perspective-owning EF Core context.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="applyEntries">A generated <c>CollectiveApplyRegistry.Entries</c>; handlers it lists that no assembly registered are dispatched too.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/MultiAssemblyCollectiveDispatchTests.cs:TheObsoleteHandPassedTable_StillApplies_AlongsideTheContributedOnesAsync</tests>
  [Obsolete("Every assembly's [CollectiveApplyFor] handlers are now registered automatically (#2238). Drop the argument: call AddCollectiveEventsEFCore<TDbContext>(). Will be removed in the next major.")]
  public static IServiceCollection AddCollectiveEventsEFCore<TDbContext>(
      this IServiceCollection services,
      IReadOnlyList<CollectiveApplyEntry> applyEntries)
      where TDbContext : DbContext {
    ArgumentNullException.ThrowIfNull(applyEntries);
    return _addCollectiveEvents<TDbContext>(services, applyEntries);
  }

  private static IServiceCollection _addCollectiveEvents<TDbContext>(
      IServiceCollection services, IReadOnlyList<CollectiveApplyEntry>? handPassed)
      where TDbContext : DbContext {
    // Apply policy (batching + statement_timeout) from PostgresOptions. TryAdd so a consumer can register
    // their own CollectiveApplyOptions first to override.
    services.TryAddSingleton(sp => {
      var pg = sp.GetService<IOptions<PostgresOptions>>()?.Value;
      return pg is null
        ? CollectiveApplyOptions.Default
        : new CollectiveApplyOptions {
          BatchSize = pg.CollectiveApplyBatchSize,
          StatementTimeoutSeconds = pg.CollectiveApplyStatementTimeoutSeconds,
        };
    });
    // Collective apply-hook registry, seeded with the framework defaults (whizbang.timestamps: stamp updated_at
    // + bump version). TryAdd so a consumer can register their own (defaults + custom hooks) first to override.
    services.TryAddSingleton(_ => WhizbangApplyHooks.CreateCollectiveWithDefaults());
    services.TryAddSingleton<ICollectiveSessionAccessor, EFCoreCollectiveSessionAccessor<TDbContext>>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectiveScopeResolver, TenantCollectiveScopeResolver>());
    services.TryAddSingleton<ICollectiveDispatcher>(sp => new CollectiveDispatcher(
      sp,
      sp.GetCollectiveApplyEntries(handPassed),
      [.. sp.GetServices<ICollectiveScopeResolver>()],
      [.. sp.GetServices<ICollectiveEventExecutor>()],
      sp.GetService<EventCategoryMetrics>()));
    // Replay/rebuild seam: folds these collective events back into each stream's per-row rebuild so the
    // mutation survives a perspective rebuild (the rebuilder never runs the set-based SQL path). Scoped so it
    // can resolve the request-scoped event-store query. Driver-neutral applier (lives in Whizbang.Data.Postgres).
    services.TryAddScoped<ICollectiveReplayApplier>(sp => new CollectiveReplayApplier(
      sp.GetCollectiveApplyEntries(handPassed),
      sp,
      sp.GetRequiredService<IEventStore>(),
      sp.GetRequiredService<IEventStoreQuery>(),
      [.. sp.GetServices<ICollectiveInMemoryExecutor>()],
      // The driver's database: a collective stream archived whole is listed from its lifecycle ledger (#1932).
      sp.GetService<Whizbang.Data.Postgres.PostgresArchiveConnection>()));
    return services;
  }

  /// <summary>
  /// Registers the EF Core collective executor for one perspective model. Add a custom
  /// <see cref="ICollectiveScopeResolver"/> separately for any non-tenant scope kind.
  /// </summary>
  /// <typeparam name="TModel">A perspective model with a <c>[CollectiveApplyFor]</c> handler.</typeparam>
  public static IServiceCollection AddCollectiveExecutorEFCore<TModel>(this IServiceCollection services)
      where TModel : class {
    services.AddSingleton<ICollectiveEventExecutor, EFCoreCollectiveEventExecutor<TModel>>();
    // In-memory twin used during replay/rebuild (driver-neutral). Same TModel as the SQL executor so a
    // collective event that mutated this model is re-applied per-row on rebuild.
    services.AddSingleton<ICollectiveInMemoryExecutor, CollectiveInMemoryExecutor<TModel>>();
    return services;
  }
}
