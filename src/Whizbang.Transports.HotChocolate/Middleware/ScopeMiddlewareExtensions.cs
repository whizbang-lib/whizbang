using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Security;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// Extension methods for configuring Whizbang scope middleware.
/// </summary>
/// <docs>apis/graphql/scoping#setup</docs>
/// <example>
/// // In Program.cs
/// builder.Services.AddWhizbangScope();
///
/// var app = builder.Build();
/// app.UseWhizbangScope();
/// app.MapGraphQL();
///
/// // Or with custom options
/// app.UseWhizbangScope(options => {
///     options.TenantIdClaimType = "tenant_id";
///     options.TenantIdHeaderName = "X-Tenant-Id";
/// });
/// </example>
public static class ScopeMiddlewareExtensions {
  /// <summary>
  /// Adds Whizbang scope services to the service collection.
  /// Registers <see cref="IScopeContextAccessor"/> for request-scoped scope access.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddWhizbangScope(this IServiceCollection services) {
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    return services;
  }

  /// <summary>
  /// Adds Whizbang scope services with custom options to the service collection.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Action to configure <see cref="WhizbangScopeOptions"/>.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddWhizbangScope(
      this IServiceCollection services,
      Action<WhizbangScopeOptions> configure) {
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();

    var options = new WhizbangScopeOptions();
    configure(options);
    services.AddSingleton(options);

    return services;
  }

  /// <summary>
  /// Adds Whizbang scope middleware to the application pipeline.
  /// Extracts scope from HTTP headers and JWT claims.
  /// </summary>
  /// <param name="app">The application builder.</param>
  /// <returns>The application builder for chaining.</returns>
  public static IApplicationBuilder UseWhizbangScope(this IApplicationBuilder app) {
    return app.UseMiddleware<WhizbangScopeMiddleware>();
  }

  /// <summary>
  /// Adds Whizbang scope middleware with custom options to the application pipeline.
  /// </summary>
  /// <param name="app">The application builder.</param>
  /// <param name="configure">Action to configure <see cref="WhizbangScopeOptions"/>.</param>
  /// <returns>The application builder for chaining.</returns>
  public static IApplicationBuilder UseWhizbangScope(
      this IApplicationBuilder app,
      Action<WhizbangScopeOptions> configure) {
    var options = new WhizbangScopeOptions();
    configure(options);
    return app.UseMiddleware<WhizbangScopeMiddleware>(options);
  }
}
