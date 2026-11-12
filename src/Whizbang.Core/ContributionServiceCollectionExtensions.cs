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
/// <para>
/// <strong>A contribution never requires its implementation.</strong> Each contribution entry resolves its
/// implementation with <c>GetService</c>, and one the host does not register contributes nothing: removing an
/// assembly's implementation from the collection used to leave the entry behind and fail the host with "No service
/// for type" (#2243). Hosts get their own service's contributions only (<see cref="ServiceAssemblyScope"/>), so
/// nothing needs removing in the first place.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/RegistrationCollisionsTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveInvokerContributionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs:AContributionWhoseRegistryWasRemoved_IsSkipped_AndTheHostStillBuildsAsync</tests>
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
      contributionKey, static (sp, _) => _resolve<TService, TContribution>(sp)));
    services.TryAddSingletonOverNullDefault<TService>(sp => compose(_latestFirst(sp.GetKeyedServices<TService>(contributionKey))));
    if (items is not null) {
      services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegistrationCollisionSource, ContributionCollisionSource<TService>>(
        sp => new ContributionCollisionSource<TService>(sp, contributionKey, items)));
      services.AddRegistrationCollisionCheck();
    }
    return services;
  }

  /// <summary>
  /// The scoped form of <see cref="AddContribution{TService, TContribution}"/>, for a service that holds per-scope
  /// state: each scope gets its own contributions and its own composite over them. Scoped contributions report no
  /// collisions of their own; the singleton registration that names the same items reports them.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="contributionKey">The key every contribution of the service is registered under.</param>
  /// <param name="compose">Builds the served service from the scope's contributions, latest registered first.</param>
  internal static IServiceCollection AddScopedContribution<TService, TContribution>(
      this IServiceCollection services,
      string contributionKey,
      Func<IReadOnlyList<TService>, TService> compose)
      where TService : class
      where TContribution : class, TService {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEnumerable(ServiceDescriptor.KeyedScoped<TService, TContribution>(
      contributionKey, static (sp, _) => _resolve<TService, TContribution>(sp)));
    services.TryAddScoped(sp => compose(_latestFirst(sp.GetKeyedServices<TService>(contributionKey))));
    return services;
  }

  /// <summary>
  /// The contribution's implementation, or <see langword="null"/> when the host does not register it: a contribution
  /// whose implementation was removed from the collection contributes nothing, and never stops the host building
  /// (#2243). The composite and the collisions report skip the null.
  /// </summary>
  private static TContribution _resolve<TService, TContribution>(IServiceProvider services)
      where TService : class
      where TContribution : class, TService =>
    services.GetService<TContribution>()!;

  private static TService[] _latestFirst<TService>(IEnumerable<TService?> contributions) where TService : class {
    TService[] ordered = [.. contributions.OfType<TService>()];
    Array.Reverse(ordered);
    return ordered;
  }

  /// <summary>The collisions among one service's contributions, each named by the assembly that declares it.</summary>
  private sealed class ContributionCollisionSource<TService>(
      IServiceProvider services, string contributionKey, Func<TService, IEnumerable<string>> items) : IRegistrationCollisionSource
      where TService : class {
    public IReadOnlyList<RegistrationCollision> GetCollisions() =>
      RegistrationCollisions.Find(typeof(TService).Name, services.GetKeyedServices<TService>(contributionKey)
        .OfType<TService>()
        .Select(contribution => (contribution.GetType().Assembly.GetName().Name!, items(contribution))));
  }
}
