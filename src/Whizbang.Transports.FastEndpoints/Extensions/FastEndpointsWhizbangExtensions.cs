// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Extension methods for registering Whizbang FastEndpoints services.
/// </summary>
/// <docs>apis/rest/setup</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/ServiceRegistrationTests.cs</tests>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/ServiceRegistrationTests.cs:AddWhizbangLenses_ShouldReturnSameServicesInstanceAsync</tests>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/ServiceRegistrationTests.cs:AddWhizbangMutations_ShouldReturnSameServicesInstanceAsync</tests>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/ServiceRegistrationTests.cs:AddWhizbangMutations_ShouldBeCallableMultipleTimesAsync</tests>
public static class FastEndpointsWhizbangExtensions {
  /// <summary>
  /// Adds Whizbang lens endpoint services for REST API integration.
  /// Generated lens endpoints are discovered and registered automatically.
  /// </summary>
  /// <remarks>
  /// Adds the <see cref="FieldPermissionJson"/> masking to the host's JSON options, which FastEndpoints writes
  /// responses with, so <c>[FieldPermission]</c> members are masked for a caller without the permission. A
  /// generated endpoint whose model has protected members refuses to respond when the masking is missing.
  /// Calling this more than once adds the masking once.
  /// </remarks>
  /// <param name="services">The service collection</param>
  /// <returns>The service collection for chaining</returns>
  /// <example>
  /// builder.Services.AddFastEndpoints()
  ///     .AddWhizbangLenses();
  /// </example>
  public static IServiceCollection AddWhizbangLenses(this IServiceCollection services) {
    // Generated lens endpoints are auto-discovered by FastEndpoints; responses need the field masking.
    services.AddOptions();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<JsonOptions>, FieldPermissionJsonOptionsSetup>());
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
