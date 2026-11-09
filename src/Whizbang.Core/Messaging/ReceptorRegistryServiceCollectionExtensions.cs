// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Messaging;

/// <summary>Registers one assembly's <see cref="IReceptorRegistry"/> as a contribution to the registry the container serves.</summary>
/// <docs>fundamentals/receptors/lifecycle-receptors#several-assemblies</docs>
public static class ReceptorRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:receptor-registry-contribution";

  /// <summary>Registers <typeparamref name="TRegistry"/>'s receptors.</summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  public static IServiceCollection AddReceptorRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IReceptorRegistry {
    services.AddSingleton<IReceptorRegistry>(sp => sp.GetRequiredService<TRegistry>());
    return services;
  }
}
