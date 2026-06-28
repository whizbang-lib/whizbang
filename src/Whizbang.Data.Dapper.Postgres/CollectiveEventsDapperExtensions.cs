using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

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
    services.TryAddSingleton<ICollectiveSessionAccessor, DapperCollectiveSessionAccessor>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ICollectiveScopeResolver, TenantCollectiveScopeResolver>());
    services.TryAddSingleton<ICollectiveDispatcher>(sp => new CollectiveDispatcher(
      sp,
      applyEntries,
      sp.GetServices<ICollectiveScopeResolver>().ToList(),
      sp.GetServices<ICollectiveEventExecutor>().ToList(),
      sp.GetService<EventCategoryMetrics>()));
    return services;
  }

  /// <summary>
  /// Registers the Dapper collective executor for one perspective model + its table name.
  /// </summary>
  /// <typeparam name="TModel">A perspective model with a <c>[CollectiveApplyFor]</c> handler.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="tableName">The model's perspective table (e.g. <c>wh_per_job</c>).</param>
  public static IServiceCollection AddCollectiveExecutorDapper<TModel>(
      this IServiceCollection services, string tableName)
      where TModel : class {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    services.AddSingleton<ICollectiveEventExecutor>(new DapperCollectiveEventExecutor<TModel>(tableName));
    return services;
  }
}
