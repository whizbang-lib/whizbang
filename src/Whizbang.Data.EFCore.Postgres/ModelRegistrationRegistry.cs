using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry for model registration callbacks.
/// Consumer assemblies register their discovered models via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// Supports multiple assemblies registering callbacks (e.g., InventoryWorker + BFF.API).
/// Tracks which (DbContext, callback) pairs have been invoked to prevent duplicate registrations.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs</tests>
public static class ModelRegistrationRegistry {
  private static readonly List<Action<IServiceCollection, Type, IDbUpsertStrategy>> _registrars = new();
  private static readonly HashSet<(Type dbContextType, int callbackIndex)> _invoked = new();
  private static readonly object _lock = new();

  /// <summary>
  /// Registers a callback that will register discovered perspective models.
  /// Called by source-generated module initializer in the consumer assembly.
  /// Supports multiple assemblies registering callbacks (thread-safe).
  /// </summary>
  /// <param name="registrar">Callback that registers models with the service collection.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:RegisterModels_WithValidRegistrar_StoresCallbackAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:RegisterModels_MultipleRegistrations_UsesLatestRegistrarAsync</tests>
  public static void RegisterModels(Action<IServiceCollection, Type, IDbUpsertStrategy> registrar) {
    lock (_lock) {
      _registrars.Add(registrar);
    }
  }

  /// <summary>
  /// Invokes the LATEST registered model registration callback.
  /// Called by driver extensions (InMemory, Postgres) to register discovered models and infrastructure.
  /// If no registrars have been set (module initializers hasn't run or no DbContext found), does nothing gracefully.
  /// Only the latest registrar is invoked to support hot reload and test scenarios where registrations can be replaced.
  /// Tracks which callbacks have been invoked for which DbContext to prevent duplicate service registrations.
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

    // If no registrars were set, the module initializers haven't run or no DbContext was discovered.
    // This can happen when:
    // 1. There are no perspectives in the consumer assembly (only infrastructure)
    // 2. The module initializers haven't been triggered yet
    // Either way, gracefully skip registration rather than throwing an error.
    lock (_lock) {
      if (_registrars.Count == 0) {
        return;
      }

      // Invoke ONLY the latest registered callback
      // This allows later registrations to override earlier ones (hot reload, test scenarios)
      var latestIndex = _registrars.Count - 1;
      var key = (dbContextType, latestIndex);
      if (_invoked.Add(key)) {
        _registrars[latestIndex](services, dbContextType, upsertStrategy);
      }
    }
  }
}
