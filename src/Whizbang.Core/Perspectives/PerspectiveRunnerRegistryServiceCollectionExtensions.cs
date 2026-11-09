// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Registers one assembly's <see cref="IPerspectiveRunnerRegistry"/> as a contribution to the registry the container serves.
/// </summary>
/// <remarks>
/// <para>
/// Every assembly that declares perspectives generates a registry of its runners, and its generated registration calls
/// <see cref="AddPerspectiveRunnerRegistryContribution{TRegistry}"/>. A host with several such assemblies resolves one
/// <see cref="IPerspectiveRunnerRegistry"/>: the perspective worker, the rebuilder, the maintenance worker and the
/// stream disposition step all look runners up through it. Registered as a plain singleton, the last assembly's
/// registry displaced the others, and no runner any other assembly declared could be found (#2191).
/// </para>
/// <para>
/// So the generated registry is registered under <see cref="CONTRIBUTION_KEY"/>, once per registry type, and the
/// served registry is a <see cref="CompositePerspectiveRunnerRegistry"/> over every contribution, in registration
/// order; when two assemblies declare the same perspective, the first contribution answers for it. A registry the host
/// registers itself before the contributions is left alone. Contributing a registry also contributes its event types
/// through <see cref="EventTypeProviderServiceCollectionExtensions.AddEventTypeProviderContribution{TProvider}"/>
/// (#2146), so the generated code makes one call for both.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/registry</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/MultiAssemblyPerspectiveRunnerRegistryTests.cs:TwoPerspectiveAssemblies_TheServedRegistry_FindsBothAssembliesRunnersAsync</tests>
public static class PerspectiveRunnerRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:perspective-runner-registry-contribution";

  /// <summary>
  /// Adds <typeparamref name="TRegistry"/>'s runners to the <see cref="IPerspectiveRunnerRegistry"/> the container
  /// serves, and its event types to the <see cref="IEventTypeProvider"/> it serves.
  /// </summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <remarks>Idempotent per registry type: an assembly registered twice contributes once.</remarks>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:AnAssemblyRegisteredTwice_ListsItsPerspectivesOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:AHostsOwnRegistry_RegisteredFirst_IsTheOneServedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:TwoAssemblies_EventTypes_AreTheUnion_OnTheRegistryAndTheServedProviderAsync</tests>
  public static IServiceCollection AddPerspectiveRunnerRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IPerspectiveRunnerRegistry {
    ArgumentNullException.ThrowIfNull(services);
    services.AddContribution<IPerspectiveRunnerRegistry, TRegistry>(
      CONTRIBUTION_KEY, static registries => new CompositePerspectiveRunnerRegistry(registries));
    services.AddEventTypeProviderContribution<TRegistry>();
    return services;
  }
}
