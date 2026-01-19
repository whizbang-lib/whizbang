using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>
/// Global registry for strongly-typed WhizbangId providers.
/// Providers are auto-registered via ModuleInitializer when assemblies load.
/// </summary>
/// <remarks>
/// <para>
/// This registry maintains factories for creating <see cref="IWhizbangIdProvider{TId}"/> instances
/// and callbacks for registering providers with DI containers. Registration happens automatically
/// via source-generated ModuleInitializer methods - users should not call registration methods directly.
/// </para>
/// <para>
/// <strong>Architecture:</strong>
/// Each assembly containing WhizbangId types includes a generated WhizbangIdProviderRegistration class
/// with a ModuleInitializer that registers factories when the assembly loads.
/// </para>
/// <para>
/// <strong>Example - Creating Provider Directly:</strong>
/// <code>
/// var baseProvider = new Uuid7IdProvider();
/// var orderIdProvider = WhizbangIdProviderRegistry.CreateProvider&lt;OrderId&gt;(baseProvider);
/// var orderId = orderIdProvider.NewId();
/// </code>
/// </para>
/// </remarks>
/// <docs>core-concepts/whizbang-ids</docs>
public static class WhizbangIdProviderRegistry {
  private static readonly Dictionary<Type, Func<IWhizbangIdProvider, object>> _factories = new();
  private static readonly List<Action<IServiceCollection, IWhizbangIdProvider>> _diRegistrations = new();
  private static readonly object _lock = new();

  /// <summary>
  /// Registers a factory for creating IWhizbangIdProvider{TId} instances.
  /// Called automatically by generated ModuleInitializer - users should not call this directly.
  /// </summary>
  /// <typeparam name="TId">The WhizbangId struct type</typeparam>
  /// <param name="factory">Factory function that creates a provider given a base provider</param>
  /// <exception cref="ArgumentNullException">Thrown when factory is null</exception>
  public static void RegisterFactory<TId>(Func<IWhizbangIdProvider, IWhizbangIdProvider<TId>> factory)
    where TId : struct {
    ArgumentNullException.ThrowIfNull(factory);

    lock (_lock) {
      _factories[typeof(TId)] = baseProvider => factory(baseProvider);
    }
  }

  /// <summary>
  /// Registers a DI callback for registering providers with IServiceCollection.
  /// Called automatically by generated ModuleInitializer - users should not call this directly.
  /// </summary>
  /// <param name="callback">Callback that registers providers with a service collection</param>
  /// <exception cref="ArgumentNullException">Thrown when callback is null</exception>
  public static void RegisterDICallback(Action<IServiceCollection, IWhizbangIdProvider> callback) {
    ArgumentNullException.ThrowIfNull(callback);

    lock (_lock) {
      _diRegistrations.Add(callback);
    }
  }

  /// <summary>
  /// Creates a strongly-typed provider for the specified WhizbangId type.
  /// </summary>
  /// <typeparam name="TId">The WhizbangId struct type</typeparam>
  /// <param name="baseProvider">The base provider to use for Guid generation</param>
  /// <returns>A provider that generates TId instances</returns>
  /// <exception cref="ArgumentNullException">Thrown when baseProvider is null</exception>
  /// <exception cref="InvalidOperationException">Thrown when no provider is registered for TId</exception>
  /// <remarks>
  /// This method queries the registry for a factory registered by the WhizbangIdGenerator.
  /// If the TId type was not discovered during compilation, this will throw an exception.
  /// </remarks>
  public static IWhizbangIdProvider<TId> CreateProvider<TId>(IWhizbangIdProvider baseProvider)
    where TId : struct {
    ArgumentNullException.ThrowIfNull(baseProvider);

    lock (_lock) {
      if (!_factories.TryGetValue(typeof(TId), out var factory)) {
        throw new InvalidOperationException(
          $"No provider factory registered for {typeof(TId).Name}. " +
          $"Ensure the WhizbangIdGenerator has processed this type. " +
          $"Add the [WhizbangId] attribute to the {typeof(TId).Name} struct declaration.");
      }

      return (IWhizbangIdProvider<TId>)factory(baseProvider);
    }
  }

  /// <summary>
  /// Registers all WhizbangId providers from all loaded assemblies with the DI container.
  /// Called by <see cref="Microsoft.Extensions.DependencyInjection.WhizbangIdServiceCollectionExtensions.AddWhizbangIdProviders"/>.
  /// </summary>
  /// <param name="services">The service collection to register providers with</param>
  /// <param name="baseProvider">The base provider to use for all typed providers</param>
  /// <exception cref="ArgumentNullException">Thrown when services or baseProvider is null</exception>
  public static void RegisterAllWithDI(IServiceCollection services, IWhizbangIdProvider baseProvider) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(baseProvider);

    lock (_lock) {
      foreach (var registration in _diRegistrations) {
        registration(services, baseProvider);
      }
    }
  }

  /// <summary>
  /// Gets all registered WhizbangId types.
  /// Useful for diagnostics and testing.
  /// </summary>
  /// <returns>Collection of all registered ID types</returns>
  public static IEnumerable<Type> GetRegisteredIdTypes() {
    lock (_lock) {
      return _factories.Keys.ToArray();
    }
  }
}
