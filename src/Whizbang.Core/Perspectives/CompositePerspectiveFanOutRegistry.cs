// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The fan-out registry over every contributed <see cref="IPerspectiveFanOutRegistry"/>: the one a host whose
/// fan-outs are declared in more than one assembly serves.
/// </summary>
/// <remarks>
/// Lists each contribution's entries in the order given (latest registered first), each fan-out once, by its
/// <see cref="PerspectiveFanOutEntry.EntryKey"/>: the first contribution that lists it keeps it. A duplicate means the
/// same source was compiled into two assemblies, and the registration reports it at startup.
/// </remarks>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs</tests>
public sealed class CompositePerspectiveFanOutRegistry : IPerspectiveFanOutRegistry {
  /// <summary>Combines the given registries, in the order given.</summary>
  /// <param name="registries">The contributions, latest registered first.</param>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs:AFanOutTwoContributionsList_IsListedOnce_AtTheFirstAsync</tests>
  public CompositePerspectiveFanOutRegistry(IEnumerable<IPerspectiveFanOutRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    var seen = new HashSet<string>(StringComparer.Ordinal);
    Entries = [.. registries.SelectMany(registry => registry.Entries).Where(entry => seen.Add(entry.EntryKey))];
  }

  /// <inheritdoc />
  public IReadOnlyList<PerspectiveFanOutEntry> Entries { get; }
}

/// <summary>
/// Registers one assembly's <see cref="IPerspectiveFanOutRegistry"/> as a contribution to the registry the container
/// serves, and reads the served fan-outs.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs</tests>
public static class PerspectiveFanOutRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:perspective-fan-out-registry-contribution";

  /// <summary>
  /// Adds <typeparamref name="TRegistry"/>'s fan-outs to the <see cref="IPerspectiveFanOutRegistry"/> the container
  /// serves. Idempotent per registry type.
  /// </summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs:TwoAssemblies_TheServedRegistry_ListsBothAssemblysFanOuts_LatestFirstAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Perspectives/FanOutWiringTests.cs:AddPerspectiveFanOutRegistryContribution_RegistersTheValidator_OnceAsync</tests>
  public static IServiceCollection AddPerspectiveFanOutRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IPerspectiveFanOutRegistry {
    ArgumentNullException.ThrowIfNull(services);
    // #2620: a host that declares a fan-out it could not apply is refused at startup, whatever else it registers.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PerspectiveFanOutWiringValidator>(
      static sp => new PerspectiveFanOutWiringValidator(sp)));
    return services.AddContribution<IPerspectiveFanOutRegistry, TRegistry>(
      CONTRIBUTION_KEY,
      static registries => new CompositePerspectiveFanOutRegistry(registries),
      static registry => registry.Entries.Select(CollisionItem));
  }

  /// <summary>The fan-outs the container serves; empty when no assembly declared one.</summary>
  /// <param name="services">The host's service provider.</param>
  /// <returns>Each fan-out once.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs:GetPerspectiveFanOutEntries_IsTheServedRegistrysEntriesAsync</tests>
  public static IReadOnlyList<PerspectiveFanOutEntry> GetPerspectiveFanOutEntries(this IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<IPerspectiveFanOutRegistry>()?.Entries ?? [];
  }

  /// <summary>How a fan-out is named in the startup collisions report: the event and the entry key.</summary>
  /// <param name="entry">The fan-out.</param>
  /// <returns>The event's display name and <see cref="PerspectiveFanOutEntry.EntryKey"/>.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutRegistryTests.cs:CollisionItem_NamesTheEventAndTheEntryKeyAsync</tests>
  public static string CollisionItem(PerspectiveFanOutEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return TypeNameFormatter.DisplayName(entry.EventType) + " -> " + entry.EntryKey;
  }

  /// <summary>
  /// Whether any assembly has contributed a fan-out registry to <paramref name="services"/>: the host declares a
  /// perspective fan-out, so a driver registers what applies one (#2620).
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns><see langword="true"/> when a fan-out registry is contributed.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_AHostThatDeclaresAFanOut_GetsTheCollectiveDispatcher_WithNothingToAddAsync</tests>
  public static bool DeclaresPerspectiveFanOuts(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.Any(static d => d.ServiceType == typeof(IPerspectiveFanOutRegistry)
      && d.IsKeyedService && Equals(d.ServiceKey, CONTRIBUTION_KEY));
  }
}
