// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Registers one assembly's <see cref="IPerspectiveRunnerRegistry"/> as a contribution to the registry the container serves.
/// </summary>
/// <docs>fundamentals/perspectives/registry</docs>
public static class PerspectiveRunnerRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:perspective-runner-registry-contribution";

  /// <summary>Registers <typeparamref name="TRegistry"/>'s runners.</summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddPerspectiveRunnerRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IPerspectiveRunnerRegistry {
    services.AddSingleton<IPerspectiveRunnerRegistry>(sp => sp.GetRequiredService<TRegistry>());
    services.AddEventTypeProviderContribution<TRegistry>();
    return services;
  }
}
