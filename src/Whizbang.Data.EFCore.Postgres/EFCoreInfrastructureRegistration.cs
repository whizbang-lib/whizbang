using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Public helper class for registering EF Core infrastructure (IPerspectiveStore and ILensQuery).
/// Called by source-generated registration code in consumer assemblies.
/// </summary>
public static class EFCoreInfrastructureRegistration {
  /// <summary>
  /// Registers IPerspectiveStore&lt;TModel&gt; and ILensQuery&lt;TModel&gt; for a specific model type.
  /// This method is called by source-generated code in the consumer assembly.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type (e.g., typeof(MyDbContext)).</param>
  /// <param name="modelType">The perspective model type (e.g., typeof(OrderReadModel)).</param>
  /// <param name="tableName">The database table name for this perspective (e.g., "order_read_model").</param>
  /// <param name="upsertStrategy">The database-specific upsert strategy to use.</param>
  public static void RegisterPerspectiveModel(
      IServiceCollection services,
      Type dbContextType,
      Type modelType,
      string tableName,
      IDbUpsertStrategy upsertStrategy) {

    // Register IPerspectiveStore<TModel>
    var storeInterfaceType = typeof(IPerspectiveStore<>).MakeGenericType(modelType);
    services.AddScoped(storeInterfaceType, sp => {
      var context = (DbContext)sp.GetRequiredService(dbContextType);
      var storeType = typeof(EFCorePostgresPerspectiveStore<>).MakeGenericType(modelType);
      return Activator.CreateInstance(storeType, context, tableName, upsertStrategy)
          ?? throw new InvalidOperationException($"Failed to create {storeType.Name}");
    });

    // Register ILensQuery<TModel>
    var queryInterfaceType = typeof(ILensQuery<>).MakeGenericType(modelType);
    services.AddScoped(queryInterfaceType, sp => {
      var context = (DbContext)sp.GetRequiredService(dbContextType);
      var queryType = typeof(EFCorePostgresLensQuery<>).MakeGenericType(modelType);
      return Activator.CreateInstance(queryType, context, tableName)
          ?? throw new InvalidOperationException($"Failed to create {queryType.Name}");
    });
  }
}
