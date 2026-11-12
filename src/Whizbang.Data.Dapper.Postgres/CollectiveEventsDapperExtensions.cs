// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// DI registration for consuming collective events on the Dapper Postgres driver. Symmetric with the
/// EF Core registration: wires the dispatcher, the built-in tenant scope resolver, and the Dapper
/// session accessor (which hands the apply chain the <c>IDbConnectionFactory</c>).
/// </summary>
/// <remarks>
/// The dispatcher's handlers come from the container: every assembly that declares <c>[CollectiveApplyFor]</c>
/// handlers registers its generated table as a contribution, through <c>AddWhizbang()</c> (#2238). Register one
/// executor per perspective model via <see cref="AddCollectiveExecutorDapper{TModel}"/>, supplying the model's
/// <c>wh_per_*</c> table name.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:AddCollectiveEventsDapper_RegistersDispatcherResolverAccessorAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:AddCollectiveEventsDapper_NullEntries_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:AddCollectiveExecutorDapper_NullTableName_ThrowsAsync</tests>
public static class CollectiveEventsDapperExtensions {
  /// <summary>
  /// Registers <see cref="ICollectiveDispatcher"/>, the built-in <see cref="TenantCollectiveScopeResolver"/>,
  /// and the Dapper <see cref="ICollectiveSessionAccessor"/>.
  /// </summary>
  /// <remarks>
  /// The dispatcher and the replay applier read every assembly's <c>[CollectiveApplyFor]</c> handlers from the
  /// container (<see cref="CollectiveApplyRegistryServiceCollectionExtensions.GetCollectiveApplyEntries"/>): each
  /// assembly's generated table is registered by <c>AddWhizbang()</c>, for the host's own service only (#2238).
  /// </remarks>
  /// <param name="services">The service collection.</param>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperMultiAssemblyCollectiveDispatchTests.cs:TwoAssemblies_TheDispatcher_AppliesEachAssemblysCollectiveEventsAsync</tests>
  public static IServiceCollection AddCollectiveEventsDapper(this IServiceCollection services) =>
    _addCollectiveEvents(services, handPassed: null);

  /// <summary>
  /// The registration that took one assembly's handler table by hand. The table now joins the handlers every assembly
  /// registers, so it is no longer needed: call <see cref="AddCollectiveEventsDapper(IServiceCollection)"/>.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="applyEntries">A generated <c>CollectiveApplyRegistry.Entries</c>; handlers it lists that no assembly registered are dispatched too.</param>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperMultiAssemblyCollectiveDispatchTests.cs:TheObsoleteHandPassedTable_StillApplies_AlongsideTheContributedOnesAsync</tests>
  [Obsolete("Every assembly's [CollectiveApplyFor] handlers are now registered automatically (#2238). Drop the argument: call AddCollectiveEventsDapper(). Will be removed in the next major.")]
  public static IServiceCollection AddCollectiveEventsDapper(
      this IServiceCollection services,
      IReadOnlyList<CollectiveApplyEntry> applyEntries) {
    ArgumentNullException.ThrowIfNull(applyEntries);
    return _addCollectiveEvents(services, applyEntries);
  }

  private static IServiceCollection _addCollectiveEvents(
      IServiceCollection services, IReadOnlyList<CollectiveApplyEntry>? handPassed) {
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
    services.TryAddSingleton<ICollectiveSessionAccessor, DapperCollectiveSessionAccessor>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectiveScopeResolver, TenantCollectiveScopeResolver>());
    services.TryAddSingleton<ICollectiveDispatcher>(sp => new CollectiveDispatcher(
      sp,
      sp.GetCollectiveApplyEntries(handPassed),
      [.. sp.GetServices<ICollectiveScopeResolver>()],
      [.. sp.GetServices<ICollectiveEventExecutor>()],
      sp.GetService<EventCategoryMetrics>()));
    // Replay/rebuild seam — folds these collective events back into each stream's per-row rebuild so the
    // mutation survives a perspective rebuild. Driver-neutral applier (Whizbang.Data.Postgres); scoped for the
    // request-scoped event-store query.
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
  /// Registers the Dapper collective executor for one perspective model + its table name. Also records the
  /// model's table so other handlers can reference it as a cross-perspective sibling (<c>q.Of&lt;TModel&gt;()</c>).
  /// </summary>
  /// <typeparam name="TModel">A perspective model with a <c>[CollectiveApplyFor]</c> handler.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="tableName">The model's perspective table (e.g. <c>wh_per_job</c>).</param>
  public static IServiceCollection AddCollectiveExecutorDapper<TModel>(
      this IServiceCollection services, string tableName)
      where TModel : class {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    var registry = _getOrAddTableRegistry(services);
    registry.Add(typeof(TModel), tableName);
    // Factory (not a pre-built instance) so the executor picks up the DI-resolved CollectiveApplyOptions
    // and logger.
    services.AddSingleton<ICollectiveEventExecutor>(sp =>
      new DapperCollectiveEventExecutor<TModel>(tableName, registry.Tables, sp.GetService<CollectiveApplyOptions>(),
        sp.GetService<Microsoft.Extensions.Logging.ILogger<DapperCollectiveEventExecutor<TModel>>>(),
        sp.GetService<CollectiveApplyHookRegistry>()));
    // In-memory twin used during replay/rebuild (driver-neutral). Same TModel as the SQL executor.
    services.AddSingleton<ICollectiveInMemoryExecutor, CollectiveInMemoryExecutor<TModel>>();
    return services;
  }

  /// <summary>
  /// Records a perspective table for a <em>query-only</em> sibling model — one a handler's <c>Where</c>
  /// references via <c>q.Of&lt;TModel&gt;()</c> but does not itself mutate (so it has no executor).
  /// </summary>
  /// <typeparam name="TModel">The sibling read model used only to scope a cohort.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="tableName">The sibling's perspective table (e.g. <c>wh_per_draft_job_status</c>).</param>
  public static IServiceCollection AddCollectiveTableDapper<TModel>(
      this IServiceCollection services, string tableName)
      where TModel : class {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    _getOrAddTableRegistry(services).Add(typeof(TModel), tableName);
    return services;
  }

  // The Dapper table registry is a single shared instance populated across all AddCollective*Dapper calls —
  // each executor closes over its live Tables map, so registrations in any order are visible at apply time.
  private static DapperCollectiveTableRegistry _getOrAddTableRegistry(IServiceCollection services) {
    var existing = services.FirstOrDefault(d => d.ServiceType == typeof(DapperCollectiveTableRegistry))
      ?.ImplementationInstance as DapperCollectiveTableRegistry;
    if (existing is not null) {
      return existing;
    }
    var registry = new DapperCollectiveTableRegistry();
    services.AddSingleton(registry);
    return registry;
  }
}
