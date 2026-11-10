// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registers one assembly's <see cref="IReceptorRegistry"/> as a contribution to the registry the container serves.
/// </summary>
/// <remarks>
/// Every assembly that declares receptors generates a registry of its lifecycle receptors, and its generated
/// dispatcher registration calls <see cref="AddReceptorRegistryContribution{TRegistry}"/>. The receptor invoker, the
/// workers' stage checks and the dispatcher all read the one <see cref="IReceptorRegistry"/> the host serves.
/// Registered as a plain singleton, the last assembly's registry displaced the others, and no lifecycle receptor
/// (<c>[FireAt]</c>) declared in any other assembly ever fired at a worker stage (#2197). The served registry is a
/// <see cref="CompositeReceptorRegistry"/> over every contribution, latest registered first: a receptor declared in
/// two assemblies under the same class name answers from the latest, and startup reports the collision. Runtime
/// registrations go to the latest contribution. A registry the host registers itself before the contributions is
/// left alone, and the composite replaces the framework's <see cref="NullReceptorRegistry"/> whichever comes first.
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors#several-assemblies</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorRegistryContributionTests.cs</tests>
public static class ReceptorRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:receptor-registry-contribution";

  /// <summary>
  /// Adds <typeparamref name="TRegistry"/>'s receptors to the <see cref="IReceptorRegistry"/> the container serves.
  /// </summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <remarks>Idempotent per registry type: an assembly registered twice contributes once.</remarks>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorRegistryContributionTests.cs:TwoAssemblies_EachAssemblysLifecycleReceptorFires_WhicheverRegisteredLastAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorRegistryContributionTests.cs:TheReceptorRegistry_ReportsReceptorsTwoAssembliesShareAsync</tests>
  public static IServiceCollection AddReceptorRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IReceptorRegistry {
    ArgumentNullException.ThrowIfNull(services);
    return services.AddContribution<IReceptorRegistry, TRegistry>(
      CONTRIBUTION_KEY,
      static registries => new CompositeReceptorRegistry(registries),
      static registry => registry.GetCompiledReceptorIds());
  }
}
