using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering WhizbangId services with dependency injection.
/// </summary>
/// <docs>core-concepts/whizbang-ids</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs</tests>
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
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:AddWhizbangIdFactory_WithValidFactory_ShouldRegisterFactoryAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:AddWhizbangIdFactory_RegisteredFactory_CanBeResolvedAsync</tests>
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
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:ConfigureWhizbangIdProvider_WithValidProvider_ShouldSetGlobalProviderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:ConfigureWhizbangIdProvider_WithCustomProvider_ShouldAffectGlobalGenerationAsync</tests>
  public static IServiceCollection ConfigureWhizbangIdProvider(
      this IServiceCollection services,
      IWhizbangIdProvider provider) {
    WhizbangIdProvider.SetProvider(provider);
    return services;
  }

  /// <summary>
  /// Registers all discovered IWhizbangIdProvider{TId} implementations in the DI container.
  /// This automatically registers a provider for every WhizbangId type discovered by the source generator.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="baseProvider">Optional base provider for Guid generation (defaults to Uuid7IdProvider).</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method registers:
  /// <list type="bullet">
  /// <item>The base <see cref="IWhizbangIdProvider"/> (used for underlying Guid generation)</item>
  /// <item>All <see cref="IWhizbangIdProvider{TId}"/> types for WhizbangIds discovered across all loaded assemblies</item>
  /// </list>
  /// </para>
  /// <para>
  /// Registration happens automatically via source-generated ModuleInitializers.
  /// When each assembly containing WhizbangId types loads, it registers provider factories
  /// with the <see cref="WhizbangIdProviderRegistry"/>.
  /// </para>
  /// <para>
  /// <strong>Example - Basic Usage:</strong>
  /// <code>
  /// // In Program.cs
  /// builder.Services.AddWhizbangIdProviders();
  ///
  /// // In your service
  /// public class OrderService {
  ///   private readonly IWhizbangIdProvider&lt;OrderId&gt; _idProvider;
  ///
  ///   public OrderService(IWhizbangIdProvider&lt;OrderId&gt; idProvider) {
  ///     _idProvider = idProvider;
  ///   }
  ///
  ///   public Order CreateOrder() {
  ///     var orderId = _idProvider.NewId();  // Type-safe!
  ///     return new Order { Id = orderId };
  ///   }
  /// }
  /// </code>
  /// </para>
  /// <para>
  /// <strong>Example - Custom Base Provider:</strong>
  /// <code>
  /// // Use custom provider for all ID types
  /// builder.Services.AddWhizbangIdProviders(new CustomIdProvider());
  /// </code>
  /// </para>
  /// <para>
  /// <strong>Example - Override Specific Type:</strong>
  /// <code>
  /// builder.Services.AddWhizbangIdProviders();  // Register defaults
  ///
  /// // Override OrderId with sequential IDs for testing
  /// builder.Services.AddSingleton&lt;IWhizbangIdProvider&lt;OrderId&gt;&gt;(
  ///   sp =&gt; OrderId.CreateProvider(new SequentialTestIdProvider())
  /// );
  /// </code>
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:AddWhizbangIdProviders_RegistersAllProvidersAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:AddWhizbangIdProviders_WithCustomProvider_UsesCustomProviderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ValueObjects/WhizbangIdServiceCollectionExtensionsTests.cs:AddWhizbangIdProviders_RegistersBaseProviderAsync</tests>
  public static IServiceCollection AddWhizbangIdProviders(
      this IServiceCollection services,
      IWhizbangIdProvider? baseProvider = null) {

    baseProvider ??= new Uuid7IdProvider();

    // Register base provider as singleton
    services.AddSingleton(baseProvider);

    // Register all typed providers from all assemblies
    // This calls ModuleInitializer-registered callbacks
    WhizbangIdProviderRegistry.RegisterAllWithDI(services, baseProvider);

    return services;
  }
}
