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
/// <docs>fundamentals/identity/whizbang-ids</docs>
public static class WhizbangIdProviderRegistry {
  private static readonly Dictionary<Type, Func<IWhizbangIdProvider, object>> _factories = [];
  private static readonly List<Action<IServiceCollection, IWhizbangIdProvider>> _diRegistrations = [];
  private static readonly Lock _lock = new();

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
  /// Test seam: removes every DI registration callback and hands them back so the caller can
  /// restore them.
  /// </summary>
  /// <remarks>
  /// The callback list is process-global and append-only, and every assembly carrying a
  /// <c>[WhizbangId]</c> struct fills it from a module initializer before any other code runs.
  /// That makes the "nothing registered" answer in <see cref="InvokeDICallbacks"/> — the one a
  /// host with no id types gets, and the only thing that keeps <c>AddWhizbang</c> from adding a
  /// base provider nobody asked for — unreachable in any process that has one. Callers must
  /// restore with <see cref="RestoreDICallbacksForTests"/> in a finally, and must serialize
  /// against anything else that touches this registry.
  /// </remarks>
  internal static Action<IServiceCollection, IWhizbangIdProvider>[] TakeDICallbacksForTests() {
    lock (_lock) {
      var taken = _diRegistrations.ToArray();
      _diRegistrations.Clear();
      return taken;
    }
  }

  /// <summary>Puts back the callbacks taken by <see cref="TakeDICallbacksForTests"/>.</summary>
  internal static void RestoreDICallbacksForTests(Action<IServiceCollection, IWhizbangIdProvider>[] callbacks) {
    ArgumentNullException.ThrowIfNull(callbacks);
    lock (_lock) {
      _diRegistrations.Clear();
      _diRegistrations.AddRange(callbacks);
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
          "Ensure the WhizbangIdGenerator has processed this type. " +
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
      return [.. _factories.Keys];
    }
  }

  /// <summary>
  /// Auto-invokes all DI registration callbacks if any are registered.
  /// Uses <see cref="Uuid7IdProvider"/> as the default base provider.
  /// Called by <see cref="ServiceCollectionExtensions.AddWhizbang"/> for automatic registration.
  /// </summary>
  /// <param name="services">The service collection to register providers with.</param>
  /// <remarks>
  /// <para>
  /// This method is safe to call multiple times - if the base provider is already registered,
  /// it will use the existing provider. If no DI callbacks were registered by module initializers,
  /// this method does nothing.
  /// </para>
  /// <para>
  /// For explicit control over the base provider, use
  /// <see cref="WhizbangIdServiceCollectionExtensions.AddWhizbangIdProviders"/> instead.
  /// </para>
  /// </remarks>
  internal static void InvokeDICallbacks(IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    lock (_lock) {
      // If no DI registrations, nothing to do
      if (_diRegistrations.Count == 0) {
        return;
      }

      // Check if base provider is already registered (user called AddWhizbangIdProviders manually)
      var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IWhizbangIdProvider));
      IWhizbangIdProvider baseProvider;

      if (existingDescriptor != null) {
        // Base provider already registered - this is fine, RegisterAllWithDI is idempotent
        // Skip re-registration to avoid duplicate service descriptors
        return;
      }

      // Use default Uuid7 provider
      baseProvider = new Uuid7IdProvider();
      services.AddSingleton(baseProvider);

      // Register all typed providers
      foreach (var registration in _diRegistrations) {
        registration(services, baseProvider);
      }
    }
  }
}
