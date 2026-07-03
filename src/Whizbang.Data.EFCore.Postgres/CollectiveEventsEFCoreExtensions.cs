using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
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
/// The <c>applyEntries</c> argument is the consumer's generated
/// <c>Whizbang.Core.Generated.CollectiveApplyRegistry.Entries</c> — passed in (not referenced here)
/// because the registry is generated per consuming assembly from its <c>[CollectiveApplyFor]</c> handlers;
/// the framework assembly's own copy is empty. Per-model executors are registered with
/// <see cref="AddCollectiveExecutorEFCore{TModel}"/> — one line per perspective model. Kept as explicit
/// compile-time generic calls (no <c>MakeGenericType</c>) to stay AOT-clean (L19); a source generator can
/// emit these calls for full turnkey registration as a follow-up.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public static class CollectiveEventsEFCoreExtensions {
  /// <summary>
  /// Registers <see cref="ICollectiveDispatcher"/>, the built-in <see cref="TenantCollectiveScopeResolver"/>,
  /// and the EF Core <see cref="ICollectiveSessionAccessor"/> bound to <typeparamref name="TDbContext"/>.
  /// Call <see cref="AddCollectiveExecutorEFCore{TModel}"/> once per collective perspective model.
  /// </summary>
  /// <typeparam name="TDbContext">The perspective-owning EF Core context.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="applyEntries">The consumer's <c>CollectiveApplyRegistry.Entries</c>.</param>
  public static IServiceCollection AddCollectiveEventsEFCore<TDbContext>(
      this IServiceCollection services,
      IReadOnlyList<CollectiveApplyEntry> applyEntries)
      where TDbContext : DbContext {
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
    services.TryAddSingleton<ICollectiveSessionAccessor, EFCoreCollectiveSessionAccessor<TDbContext>>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectiveScopeResolver, TenantCollectiveScopeResolver>());
    services.TryAddSingleton<ICollectiveDispatcher>(sp => new CollectiveDispatcher(
      sp,
      applyEntries,
      sp.GetServices<ICollectiveScopeResolver>().ToList(),
      sp.GetServices<ICollectiveEventExecutor>().ToList(),
      sp.GetService<EventCategoryMetrics>()));
    // Replay/rebuild seam: folds these collective events back into each stream's per-row rebuild so the
    // mutation survives a perspective rebuild (the rebuilder never runs the set-based SQL path). Scoped so it
    // can resolve the request-scoped event-store query. Driver-neutral applier (lives in Whizbang.Data.Postgres).
    services.TryAddScoped<ICollectiveReplayApplier>(sp => new CollectiveReplayApplier(
      applyEntries,
      sp,
      sp.GetRequiredService<IEventStore>(),
      sp.GetRequiredService<IEventStoreQuery>(),
      sp.GetServices<ICollectiveInMemoryExecutor>().ToList()));
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
