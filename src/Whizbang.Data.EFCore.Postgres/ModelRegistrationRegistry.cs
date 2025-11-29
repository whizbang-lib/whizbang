using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry for model registration callbacks.
/// Consumer assemblies register their discovered models via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// </summary>
public static class ModelRegistrationRegistry {
  private static Action<IServiceCollection, Type, IDbUpsertStrategy>? _registrar;

  /// <summary>
  /// Registers a callback that will register discovered perspective models.
  /// Called by source-generated module initializer in the consumer assembly.
  /// </summary>
  /// <param name="registrar">Callback that registers models with the service collection.</param>
  public static void RegisterModels(Action<IServiceCollection, Type, IDbUpsertStrategy> registrar) {
    _registrar = registrar;
  }

  /// <summary>
  /// Invokes the registered model registration callback.
  /// Called by driver extensions (InMemory, Postgres) to register discovered models.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type.</param>
  /// <param name="upsertStrategy">The database-specific upsert strategy.</param>
  /// <exception cref="InvalidOperationException">Thrown if no models were discovered.</exception>
  internal static void InvokeRegistration(
      IServiceCollection services,
      Type dbContextType,
      IDbUpsertStrategy upsertStrategy) {

    if (_registrar == null) {
      throw new InvalidOperationException(
          "No perspective models were discovered in the DbContext. " +
          "Ensure you have Entity<PerspectiveRow<TModel>> calls in OnModelCreating.");
    }

    _registrar(services, dbContextType, upsertStrategy);
  }
}
