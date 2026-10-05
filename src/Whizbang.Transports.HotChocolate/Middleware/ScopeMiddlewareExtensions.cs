// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
  /// <remarks>
  /// Registers <see cref="WhizbangScopeOptions"/> bound from <c>Whizbang:Scope</c>, so the claim and
  /// header mappings can change at deploy time. A host that registered its own instance keeps it.
  /// </remarks>
  public static IServiceCollection AddWhizbangScope(this IServiceCollection services) {
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    services.TryAddSingleton(sp => _bindFromConfiguration(sp.GetService<IConfiguration>(), new WhizbangScopeOptions()));
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

    // Code first, then Whizbang:Scope over it when the options first resolve: a key that is present
    // wins, an absent key leaves the code value alone.
    var options = new WhizbangScopeOptions();
    configure(options);
    services.AddSingleton(sp => _bindFromConfiguration(sp.GetService<IConfiguration>(), options));

    return services;
  }

  /// <summary>
  /// Applies <c>Whizbang:Scope</c> to <paramref name="options"/>. Indexed keys on the plural claim-type
  /// lists add to the list (the binder appends); the singular key replaces it.
  /// </summary>
  private static WhizbangScopeOptions _bindFromConfiguration(IConfiguration? configuration, WhizbangScopeOptions options) {
    if (configuration is not null) {
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
      configuration.GetSection("Whizbang:Scope").Bind(options);
#pragma warning restore IL2026, IL3050
    }
    return options;
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
