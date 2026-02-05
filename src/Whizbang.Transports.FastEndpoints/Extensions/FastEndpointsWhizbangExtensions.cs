using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Extension methods for registering Whizbang FastEndpoints services.
/// </summary>
/// <docs>v0.1.0/rest/setup</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/ServiceRegistrationTests.cs</tests>
public static class FastEndpointsWhizbangExtensions {
  /// <summary>
  /// Adds Whizbang lens endpoint services for REST API integration.
  /// Generated lens endpoints are discovered and registered automatically.
  /// </summary>
  /// <param name="services">The service collection</param>
  /// <returns>The service collection for chaining</returns>
  /// <example>
  /// builder.Services.AddFastEndpoints()
  ///     .AddWhizbangLenses();
  /// </example>
  public static IServiceCollection AddWhizbangLenses(this IServiceCollection services) {
    // Note: Generated lens endpoints are auto-discovered by FastEndpoints
    // This method is a placeholder for future lens-specific service registration
    // (e.g., custom filter handlers, sort handlers, etc.)
    return services;
  }

  /// <summary>
  /// Adds Whizbang mutation endpoint services for REST API integration.
  /// Generated mutation endpoints are discovered and registered automatically.
  /// </summary>
  /// <param name="services">The service collection</param>
  /// <returns>The service collection for chaining</returns>
  /// <example>
  /// builder.Services.AddFastEndpoints()
  ///     .AddWhizbangLenses()
  ///     .AddWhizbangMutations();
  /// </example>
  public static IServiceCollection AddWhizbangMutations(this IServiceCollection services) {
    // Note: Generated mutation endpoints are auto-discovered by FastEndpoints
    // This method is a placeholder for future mutation-specific service registration
    // (e.g., custom validators, authorization handlers, etc.)
    return services;
  }
}
