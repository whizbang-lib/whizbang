using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core;

/// <summary>
/// TryAdd variants for subsystems that supply a real implementation of an extensibility point the
/// core registers a null default for. Plain TryAdd would lose to the placeholder whenever the
/// subsystem is registered after the core; these remove the placeholder first and leave every
/// other registration, in particular a host's own, untouched.
/// </summary>
/// <docs>extending/extensibility/replaceable-services</docs>
public static class NullDefaultServiceCollectionExtensions {
  /// <summary>Registers <typeparamref name="TImplementation"/> unless a non-placeholder registration exists.</summary>
  public static IServiceCollection TryAddSingletonOverNullDefault<TService,
      [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
      this IServiceCollection services)
      where TService : class
      where TImplementation : class, TService {
    ArgumentNullException.ThrowIfNull(services);
    _removeNullDefaults(services, typeof(TService));
    services.TryAddSingleton<TService, TImplementation>();
    return services;
  }

  /// <summary>Registers the factory unless a non-placeholder registration exists.</summary>
  public static IServiceCollection TryAddSingletonOverNullDefault<TService>(
      this IServiceCollection services, Func<IServiceProvider, TService> factory)
      where TService : class {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(factory);
    _removeNullDefaults(services, typeof(TService));
    services.TryAddSingleton(factory);
    return services;
  }

  /// <summary>Registers the instance unless a non-placeholder registration exists.</summary>
  public static IServiceCollection TryAddSingletonOverNullDefault<TService>(this IServiceCollection services, TService instance)
      where TService : class {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(instance);
    _removeNullDefaults(services, typeof(TService));
    services.TryAddSingleton(instance);
    return services;
  }

  private static void _removeNullDefaults(IServiceCollection services, Type serviceType) {
    for (var i = services.Count - 1; i >= 0; i--) {
      var descriptor = services[i];
      if (descriptor.ServiceType != serviceType || descriptor.IsKeyedService) {
        continue;
      }
      var isPlaceholder = descriptor.ImplementationInstance is INullDefault
        || (descriptor.ImplementationType is { } implementation && typeof(INullDefault).IsAssignableFrom(implementation));
      if (isPlaceholder) {
        services.RemoveAt(i);
      }
    }
  }
}
