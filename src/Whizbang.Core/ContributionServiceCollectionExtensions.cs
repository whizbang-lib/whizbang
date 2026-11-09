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
/// event types its perspectives fold, the runners for those perspectives). Registered as a plain singleton, the last
/// assembly's registration displaced the others' (#2146, #2191). Each public <c>Add...Contribution</c> method calls
/// this, so the generated code has one correct call to make and no way to register the service itself.
/// </para>
/// <para>
/// Contributions are deduplicated by implementation type, so an assembly registered twice contributes once, and keep
/// the order they were registered in. The composite is added only when no registration of the service exists other
/// than a framework null default, so a service the host registers itself before the contributions is left alone.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerRegistryContributionTests.cs</tests>
internal static class ContributionServiceCollectionExtensions {
  /// <summary>
  /// Adds <typeparamref name="TContribution"/> under <paramref name="contributionKey"/> and serves
  /// <paramref name="compose"/> over every contribution under that key as <typeparamref name="TService"/>.
  /// </summary>
  internal static IServiceCollection AddContribution<TService, TContribution>(
      this IServiceCollection services,
      string contributionKey,
      Func<IEnumerable<TService>, TService> compose)
      where TService : class
      where TContribution : class, TService {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEnumerable(ServiceDescriptor.KeyedSingleton<TService, TContribution>(
      contributionKey, static (sp, _) => sp.GetRequiredService<TContribution>()));
    services.TryAddSingletonOverNullDefault<TService>(sp => compose(sp.GetKeyedServices<TService>(contributionKey)));
    return services;
  }
}
