using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Public helper class for registering EF Core infrastructure (IPerspectiveStore, ILensQuery).
/// Called by source-generated registration code in consumer assemblies.
/// Note: Infrastructure (Inbox, Outbox, EventStore) registration happens via source-generated code with concrete types.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs</tests>
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
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterPerspectiveModel_RegistersIPerspectiveStoreAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterPerspectiveModel_RegistersILensQueryAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterPerspectiveModel_WithMultipleModels_RegistersBothAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterPerspectiveModel_CreatesCorrectStoreTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterPerspectiveModel_CreatesCorrectQueryTypeAsync</tests>
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

  /// <summary>
  /// Registers ILensQuery&lt;T1, T2&gt; for multi-model queries with shared DbContext.
  /// Enables LINQ joins across multiple perspective types.
  /// </summary>
  /// <typeparam name="T1">First perspective model type</typeparam>
  /// <typeparam name="T2">Second perspective model type</typeparam>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type (e.g., typeof(MyDbContext)).</param>
  /// <param name="tableNames">Dictionary mapping model types to their table names.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterMultiLensQuery_TwoGeneric_RegistersILensQueryAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterMultiLensQuery_TwoGeneric_IsTransient_ReturnsDifferentInstancesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterMultiLensQuery_TwoGeneric_CreatesCorrectTypeAsync</tests>
  public static void RegisterMultiLensQuery<T1, T2>(
      IServiceCollection services,
      Type dbContextType,
      IReadOnlyDictionary<Type, string> tableNames)
      where T1 : class
      where T2 : class {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(dbContextType);
    ArgumentNullException.ThrowIfNull(tableNames);

    // Register as Transient - each injection gets its own DbContext for parallel resolver safety
    services.AddTransient<ILensQuery<T1, T2>>(sp => {
      var context = (DbContext)sp.GetRequiredService(dbContextType);
      return new EFCorePostgresLensQuery<T1, T2>(context, tableNames);
    });
  }

  /// <summary>
  /// Registers ILensQuery&lt;T1, T2, T3&gt; for multi-model queries with shared DbContext.
  /// Enables LINQ joins across multiple perspective types.
  /// </summary>
  /// <typeparam name="T1">First perspective model type</typeparam>
  /// <typeparam name="T2">Second perspective model type</typeparam>
  /// <typeparam name="T3">Third perspective model type</typeparam>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type (e.g., typeof(MyDbContext)).</param>
  /// <param name="tableNames">Dictionary mapping model types to their table names.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterMultiLensQuery_ThreeGeneric_RegistersILensQueryAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreInfrastructureRegistrationTests.cs:RegisterMultiLensQuery_ThreeGeneric_IsTransient_ReturnsDifferentInstancesAsync</tests>
  public static void RegisterMultiLensQuery<T1, T2, T3>(
      IServiceCollection services,
      Type dbContextType,
      IReadOnlyDictionary<Type, string> tableNames)
      where T1 : class
      where T2 : class
      where T3 : class {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(dbContextType);
    ArgumentNullException.ThrowIfNull(tableNames);

    // Register as Transient - each injection gets its own DbContext for parallel resolver safety
    services.AddTransient<ILensQuery<T1, T2, T3>>(sp => {
      var context = (DbContext)sp.GetRequiredService(dbContextType);
      return new EFCorePostgresLensQuery<T1, T2, T3>(context, tableNames);
    });
  }
}
