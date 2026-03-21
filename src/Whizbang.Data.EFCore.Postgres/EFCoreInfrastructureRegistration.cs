#pragma warning disable S2436 // Fluent API with intentional generic type parameter overloads

using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;

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
      var scopeContextAccessor = sp.GetRequiredService<IScopeContextAccessor>();
      var whizbangOptions = sp.GetRequiredService<IOptions<WhizbangCoreOptions>>();
      var queryType = typeof(EFCorePostgresLensQuery<>).MakeGenericType(modelType);
      return Activator.CreateInstance(queryType, context, tableName, scopeContextAccessor, whizbangOptions)
          ?? throw new InvalidOperationException($"Failed to create {queryType.Name}");
    });
  }

  /// <summary>
  /// Registers ILensQuery&lt;T1, T2&gt; for multi-model queries with shared DbContext.
  /// Enables LINQ joins across multiple perspective types.
  /// </summary>
  public static void RegisterMultiLensQuery<
      [DynamicallyAccessedMembers(
          DynamicallyAccessedMemberTypes.PublicConstructors |
          DynamicallyAccessedMemberTypes.NonPublicConstructors |
          DynamicallyAccessedMemberTypes.PublicProperties)] TDbContext,
      T1, T2>(
      IServiceCollection services,
      IReadOnlyDictionary<Type, string> tableNames)
      where TDbContext : DbContext
      where T1 : class
      where T2 : class {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(tableNames);

    services.AddTransient<ILensQuery<T1, T2>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
      var context = factory.CreateDbContext();
      var scopeContextAccessor = sp.GetRequiredService<IScopeContextAccessor>();
      var whizbangOptions = sp.GetRequiredService<IOptions<WhizbangCoreOptions>>();
      return new EFCorePostgresLensQuery<T1, T2>(context, tableNames, scopeContextAccessor, whizbangOptions);
    });
  }

  /// <summary>
  /// Registers ILensQuery&lt;T1, T2, T3&gt; for multi-model queries with shared DbContext.
  /// Enables LINQ joins across multiple perspective types.
  /// </summary>
  public static void RegisterMultiLensQuery<
      [DynamicallyAccessedMembers(
          DynamicallyAccessedMemberTypes.PublicConstructors |
          DynamicallyAccessedMemberTypes.NonPublicConstructors |
          DynamicallyAccessedMemberTypes.PublicProperties)] TDbContext,
      T1, T2, T3>(
      IServiceCollection services,
      IReadOnlyDictionary<Type, string> tableNames)
      where TDbContext : DbContext
      where T1 : class
      where T2 : class
      where T3 : class {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(tableNames);

    services.AddTransient<ILensQuery<T1, T2, T3>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();
      var context = factory.CreateDbContext();
      var scopeContextAccessor = sp.GetRequiredService<IScopeContextAccessor>();
      var whizbangOptions = sp.GetRequiredService<IOptions<WhizbangCoreOptions>>();
      return new EFCorePostgresLensQuery<T1, T2, T3>(context, tableNames, scopeContextAccessor, whizbangOptions);
    });
  }
}
