// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core;

/// <summary>
/// The one shape every per-assembly generated registration of a host-wide service takes: the assembly's
/// implementation is a keyed contribution, and the container serves a composite over every contribution.
/// </summary>
/// <remarks>
/// <para>
/// Every assembly compiled with the generators emits its own implementation of a service the host resolves once (the
/// event types its perspectives fold, the runners for those perspectives, its lifecycle receptors, its pinned
/// identities, its perspective invoker). Registered as a plain singleton, the last assembly's registration displaced
/// the others' (#2146, #2191, #2197, #2198, #2200). Each public <c>Add...Contribution</c> method calls this, so the
/// generated code has one correct call to make and no way to register the service itself.
/// </para>
/// <para>
/// <strong>The latest registration wins.</strong> The composite receives the contributions latest registered
/// first, and asks them in that order, so when two assemblies contribute the same item the latest one answers. An
/// assembly registered twice contributes once, at its first registration's place: a re-registration replaces
/// nothing and moves nothing. Given <c>items</c>, the registration also adds a collision source, so every item two
/// different assemblies contribute is reported at startup and, under
/// <c>Whizbang:Startup:RegistrationCollisions = Fail</c>, stops it (<see cref="RegistrationCollisions"/>).
/// </para>
/// <para>
/// The composite is added only when no registration of the service exists other than a framework null default, so
/// a service the host registers itself before the contributions is left alone.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs</tests>
internal static class ContributionServiceCollectionExtensions {
  /// <summary>
  /// Adds <typeparamref name="TContribution"/> under <paramref name="contributionKey"/> and serves
  /// <paramref name="compose"/> over every contribution under that key, latest registered first, as the singleton
  /// <typeparamref name="TService"/>.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="contributionKey">The key every contribution of the service is registered under.</param>
  /// <param name="compose">Builds the served service from the contributions, latest registered first.</param>
  /// <param name="items">
  /// The items a contribution registers, for the collisions report; <see langword="null"/> when two assemblies
  /// cannot hide each other's (the same event type known twice is still one type).
  /// </param>
  internal static IServiceCollection AddContribution<TService, TContribution>(
      this IServiceCollection services,
      string contributionKey,
      Func<IReadOnlyList<TService>, TService> compose,
      Func<TService, IEnumerable<string>>? items = null)
      where TService : class
      where TContribution : class, TService {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEnumerable(ServiceDescriptor.KeyedSingleton<TService, TContribution>(
      contributionKey, static (sp, _) => sp.GetRequiredService<TContribution>()));
    services.TryAddSingletonOverNullDefault<TService>(sp => compose([.. sp.GetKeyedServices<TService>(contributionKey)]));
    _ = items;
    return services;
  }
}
