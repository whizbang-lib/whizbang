using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry for model registration callbacks.
/// Consumer assemblies register their discovered models via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs</tests>
public static class ModelRegistrationRegistry {
  private static Action<IServiceCollection, Type, IDbUpsertStrategy>? _registrar;

  /// <summary>
  /// Registers a callback that will register discovered perspective models.
  /// Called by source-generated module initializer in the consumer assembly.
  /// </summary>
  /// <param name="registrar">Callback that registers models with the service collection.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:RegisterModels_WithValidRegistrar_StoresCallbackAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:RegisterModels_MultipleRegistrations_UsesLatestRegistrarAsync</tests>
  public static void RegisterModels(Action<IServiceCollection, Type, IDbUpsertStrategy> registrar) {
    _registrar = registrar;
  }

  /// <summary>
  /// Invokes the registered model registration callback.
  /// Called by driver extensions (InMemory, Postgres) to register discovered models and infrastructure.
  /// If no registrar has been set (module initializer hasn't run or no DbContext found), does nothing gracefully.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type.</param>
  /// <param name="upsertStrategy">The database-specific upsert strategy.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:InvokeRegistration_WithNoRegistrar_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:InvokeRegistration_PassesCorrectParametersToRegistrarAsync</tests>
  internal static void InvokeRegistration(
      IServiceCollection services,
      Type dbContextType,
      IDbUpsertStrategy upsertStrategy) {

    // If no registrar was set, the module initializer hasn't run or no DbContext was discovered.
    // This can happen when:
    // 1. There are no perspectives in the consumer assembly (only infrastructure)
    // 2. The module initializer hasn't been triggered yet
    // Either way, gracefully skip registration rather than throwing an error.
    if (_registrar == null) {
      return;
    }

    _registrar(services, dbContextType, upsertStrategy);
  }
}
