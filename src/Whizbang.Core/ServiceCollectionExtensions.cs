using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>
/// Extension methods for registering Whizbang services with dependency injection.
/// Provides the unified AddWhizbang() API.
/// </summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers Whizbang core infrastructure services and returns a builder for storage configuration.
  /// This is the unified entry point for configuring Whizbang.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>A WhizbangBuilder for configuring storage providers.</returns>
  /// <remarks>
  /// <para>
  /// Use this method to register all Whizbang core services in one call.
  /// After calling AddWhizbang(), chain storage configuration methods like:
  /// </para>
  /// <para>
  /// <strong>EF Core with Postgres:</strong>
  /// <code>
  /// services
  ///     .AddWhizbang()
  ///     .WithEFCore&lt;MyDbContext&gt;()
  ///     .WithDriver.Postgres;
  /// </code>
  /// </para>
  /// <para>
  /// <strong>EF Core with InMemory (testing):</strong>
  /// <code>
  /// services
  ///     .AddWhizbang()
  ///     .WithEFCore&lt;MyDbContext&gt;()
  ///     .WithDriver.InMemory;
  /// </code>
  /// </para>
  /// </remarks>
  public static WhizbangBuilder AddWhizbang(this IServiceCollection services) {
    // TODO: Register core services here as they are implemented
    // services.AddWhizbangDispatcher();
    // services.AddReceptors();
    // services.AddWhizbangAggregateIdExtractor();
    // services.AddWhizbangPerspectiveInvoker();
    // services.AddSingleton<ITraceStore, InMemoryTraceStore>();
    // services.AddSingleton<IPolicyEngine, PolicyEngine>();

    return new WhizbangBuilder(services);
  }
}
