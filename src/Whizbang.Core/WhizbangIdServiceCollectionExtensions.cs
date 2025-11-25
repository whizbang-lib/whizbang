using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>
/// Extension methods for registering WhizbangId services with dependency injection.
/// </summary>
public static class WhizbangIdServiceCollectionExtensions {
  /// <summary>
  /// Registers a WhizbangId factory for dependency injection.
  /// </summary>
  /// <typeparam name="TId">The WhizbangId type.</typeparam>
  /// <typeparam name="TFactory">The factory implementation type.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// Use this method to register generated WhizbangId factories for dependency injection.
  /// Factories are registered as singletons since they are stateless and thread-safe.
  /// </para>
  /// <para>
  /// <strong>Example:</strong>
  /// <code>
  /// services.AddWhizbangIdFactory&lt;ProductId, ProductIdFactory&gt;();
  /// services.AddWhizbangIdFactory&lt;OrderId, OrderIdFactory&gt;();
  /// services.AddWhizbangIdFactory&lt;CustomerId, CustomerIdFactory&gt;();
  /// </code>
  /// </para>
  /// </remarks>
  public static IServiceCollection AddWhizbangIdFactory<TId, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFactory>(this IServiceCollection services)
      where TFactory : class, IWhizbangIdFactory<TId> {
    return services.AddSingleton<IWhizbangIdFactory<TId>, TFactory>();
  }

  /// <summary>
  /// Configures the global WhizbangId provider during application startup.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="provider">The provider to use for ID generation.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method sets the global <see cref="WhizbangIdProvider"/> used by all WhizbangId types.
  /// It should be called during application startup, before any IDs are generated.
  /// </para>
  /// <para>
  /// <strong>Note:</strong> This configures a static global provider, not a DI-scoped provider.
  /// For DI-based ID generation, use <see cref="AddWhizbangIdFactory{TId, TFactory}"/> instead.
  /// </para>
  /// <para>
  /// <strong>Example - Custom Provider:</strong>
  /// <code>
  /// services.ConfigureWhizbangIdProvider(new MyCustomIdProvider());
  /// </code>
  /// </para>
  /// <para>
  /// <strong>Example - Testing with Sequential IDs:</strong>
  /// <code>
  /// services.ConfigureWhizbangIdProvider(new SequentialTestIdProvider());
  /// </code>
  /// </para>
  /// </remarks>
  public static IServiceCollection ConfigureWhizbangIdProvider(
      this IServiceCollection services,
      IWhizbangIdProvider provider) {
    WhizbangIdProvider.SetProvider(provider);
    return services;
  }
}
