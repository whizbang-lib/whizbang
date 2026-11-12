// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The collective apply registry over every contributed <see cref="ICollectiveApplyRegistry"/>: the one a host whose
/// collective handlers are declared in more than one assembly serves (#2238).
/// </summary>
/// <remarks>
/// Lists each contribution's handlers in the order given (the registration hands them over latest registered first),
/// each handler once: a handler is one method of one type applying one event to one model, and the first contribution
/// that lists it keeps it. Two assemblies cannot both declare one handler type, so a duplicate means the same source
/// was compiled into both; the registration reports it at startup (<see cref="RegistrationCollisions"/>).
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#di-wiring</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs</tests>
public sealed class CompositeCollectiveApplyRegistry : ICollectiveApplyRegistry {
  /// <summary>Combines the given registries, in the order given.</summary>
  /// <param name="registries">The contributions, latest registered first.</param>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs:AHandlerTwoContributionsList_IsListedOnce_AtTheLatestAsync</tests>
  public CompositeCollectiveApplyRegistry(IEnumerable<ICollectiveApplyRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    Entries = CollectiveApplyRegistryServiceCollectionExtensions.Union(registries.SelectMany(registry => registry.Entries));
  }

  /// <inheritdoc />
  public IReadOnlyList<CollectiveApplyEntry> Entries { get; }
}

/// <summary>
/// Registers one assembly's <see cref="ICollectiveApplyRegistry"/> as a contribution to the registry the container
/// serves, and reads the served handlers.
/// </summary>
/// <remarks>
/// Every assembly that declares <c>[CollectiveApplyFor]</c> handlers generates a registry, and its module initializer
/// registers it here through the startup slot named by the assembly, so a host anchored to its service registers only
/// its own service's handlers (#2243). The handler table used to be passed to the collective registration by hand, and
/// a host with handlers in several assemblies gave the dispatcher one assembly's (#2238).
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#di-wiring</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs</tests>
public static class CollectiveApplyRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:collective-apply-registry-contribution";

  /// <summary>
  /// Adds <typeparamref name="TRegistry"/>'s handlers to the <see cref="ICollectiveApplyRegistry"/> the container serves.
  /// </summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <remarks>Idempotent per registry type: an assembly registered twice contributes once.</remarks>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs:TwoAssemblies_TheServedRegistry_ListsBothAssemblysHandlersAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/MultiAssemblyCollectiveDispatchTests.cs:TwoAssemblies_TheDispatcher_AppliesEachAssemblysCollectiveEventsAsync</tests>
  public static IServiceCollection AddCollectiveApplyRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, ICollectiveApplyRegistry {
    ArgumentNullException.ThrowIfNull(services);
    return services.AddContribution<ICollectiveApplyRegistry, TRegistry>(
      CONTRIBUTION_KEY,
      static registries => new CompositeCollectiveApplyRegistry(registries),
      static registry => registry.Entries.Select(CollisionItem));
  }

  /// <summary>
  /// The handlers the container serves, followed by those of <paramref name="handPassed"/> it does not already list:
  /// what the collective dispatcher and the replay applier walk.
  /// </summary>
  /// <param name="services">The host's service provider.</param>
  /// <param name="handPassed">A handler table a consumer passed by hand to the obsolete registration, or none.</param>
  /// <returns>Each handler once; empty when no assembly contributed and nothing was passed.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs:GetCollectiveApplyEntries_WithHandPassedHandlers_AddsThoseTheRegistryLacks_OnceAsync</tests>
  public static IReadOnlyList<CollectiveApplyEntry> GetCollectiveApplyEntries(
      this IServiceProvider services, IReadOnlyList<CollectiveApplyEntry>? handPassed = null) {
    ArgumentNullException.ThrowIfNull(services);
    IReadOnlyList<CollectiveApplyEntry> served = services.GetService<ICollectiveApplyRegistry>()?.Entries ?? [];
    return handPassed is null ? served : Union(served.Concat(handPassed));
  }

  /// <summary>How a handler is named in the startup collisions report: the event and the handler's entry key.</summary>
  /// <param name="entry">The handler.</param>
  /// <returns>The event's display name and <see cref="CollectiveApplyEntry.EntryKey"/>.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveApplyRegistryContributionTests.cs:CollisionItem_NamesTheEventAndTheHandlerMethodAndModelAsync</tests>
  public static string CollisionItem(CollectiveApplyEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return TypeNameFormatter.DisplayName(entry.EventType) + " -> " + entry.EntryKey;
  }

  /// <summary>Each handler once, at its first place.</summary>
  internal static CollectiveApplyEntry[] Union(IEnumerable<CollectiveApplyEntry> entries) {
    var seen = new HashSet<(Type EventType, string EntryKey)>();
    return [.. entries.Where(entry => seen.Add((entry.EventType, entry.EntryKey)))];
  }
}
