// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registers one assembly's <see cref="IEventTypeProvider"/> as a contribution to the provider the container serves.
/// </summary>
/// <remarks>
/// <para>
/// Every assembly that declares perspectives generates a provider listing the events its perspectives fold plus the
/// collective events it knows, and its generated registration calls
/// <see cref="AddEventTypeProviderContribution{TProvider}"/>. A host with several such assemblies resolves one
/// <see cref="IEventTypeProvider"/>: the perspective worker and its drain, the inbox pre-filter and the polymorphic
/// event reader all read the single provider. Registered as a plain singleton, the last assembly's provider displaced
/// the others, and every event only those knew was invisible to all of them (#2146).
/// </para>
/// <para>
/// So the generated provider is registered under <see cref="CONTRIBUTION_KEY"/>, once per provider type, and the
/// served provider is a <see cref="CompositeEventTypeProvider"/> over every contribution. The composite replaces the
/// framework's <see cref="NullEventTypeProvider"/> whichever is registered first, and a provider the host registers
/// itself before the contributions is left alone: that is the host's decision, as with every other null-default seam.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/MultiAssemblyEventTypeProviderTests.cs:TwoPerspectiveAssemblies_TheServedProvider_ListsBothAsync</tests>
public static class EventTypeProviderServiceCollectionExtensions {
  /// <summary>The service key each assembly's provider is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:event-type-provider-contribution";

  /// <summary>
  /// Adds <typeparamref name="TProvider"/>'s event types to the <see cref="IEventTypeProvider"/> the container serves.
  /// </summary>
  /// <typeparam name="TProvider">The contributing provider, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <remarks>Idempotent per provider type: an assembly registered twice contributes once.</remarks>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs:AnAssemblyRegisteredTwice_ContributesItsTypesOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventTypeProviderContributionTests.cs:AHostsOwnProvider_RegisteredFirst_IsTheOneServedAsync</tests>
  public static IServiceCollection AddEventTypeProviderContribution<TProvider>(this IServiceCollection services)
      where TProvider : class, IEventTypeProvider {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEnumerable(ServiceDescriptor.KeyedSingleton<IEventTypeProvider, TProvider>(
      CONTRIBUTION_KEY, static (sp, _) => sp.GetRequiredService<TProvider>()));
    services.TryAddSingletonOverNullDefault<IEventTypeProvider>(static sp =>
      new CompositeEventTypeProvider(sp.GetKeyedServices<IEventTypeProvider>(CONTRIBUTION_KEY)));
    return services;
  }
}
