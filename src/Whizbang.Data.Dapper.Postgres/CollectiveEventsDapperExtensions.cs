using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
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
/// <c>applyEntries</c> is the consumer's generated
/// <c>Whizbang.Core.Generated.CollectiveApplyRegistry.Entries</c> (the framework assembly's copy is
/// empty). Register one executor per perspective model via
/// <see cref="AddCollectiveExecutorDapper{TModel}"/>, supplying the model's <c>wh_per_*</c> table name.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public static class CollectiveEventsDapperExtensions {
  /// <summary>
  /// Registers <see cref="ICollectiveDispatcher"/>, the built-in <see cref="TenantCollectiveScopeResolver"/>,
  /// and the Dapper <see cref="ICollectiveSessionAccessor"/>.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="applyEntries">The consumer's <c>CollectiveApplyRegistry.Entries</c>.</param>
  public static IServiceCollection AddCollectiveEventsDapper(
      this IServiceCollection services,
      IReadOnlyList<CollectiveApplyEntry> applyEntries) {
    ArgumentNullException.ThrowIfNull(applyEntries);
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
    services.TryAddSingleton<ICollectiveSessionAccessor, DapperCollectiveSessionAccessor>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectiveScopeResolver, TenantCollectiveScopeResolver>());
    services.TryAddSingleton<ICollectiveDispatcher>(sp => new CollectiveDispatcher(
      sp,
      applyEntries,
      sp.GetServices<ICollectiveScopeResolver>().ToList(),
      sp.GetServices<ICollectiveEventExecutor>().ToList(),
      sp.GetService<EventCategoryMetrics>()));
    // Replay/rebuild seam — folds these collective events back into each stream's per-row rebuild so the
    // mutation survives a perspective rebuild. Driver-neutral applier (Whizbang.Data.Postgres); scoped for the
    // request-scoped event-store query.
    services.TryAddScoped<ICollectiveReplayApplier>(sp => new CollectiveReplayApplier(
      applyEntries,
      sp,
      sp.GetRequiredService<IEventStore>(),
      sp.GetRequiredService<IEventStoreQuery>(),
      sp.GetServices<ICollectiveInMemoryExecutor>().ToList()));
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
        sp.GetService<Microsoft.Extensions.Logging.ILogger<DapperCollectiveEventExecutor<TModel>>>()));
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

  // The Dapper table registry is a single shared instance populated across all AddCollective*Dapper calls;
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
