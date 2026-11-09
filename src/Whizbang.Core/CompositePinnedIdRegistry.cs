// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>
/// The pinned-identity registry over every contributed <see cref="IPinnedIdRegistry"/>: the registry a host whose
/// pinned types are declared in more than one assembly serves (#2198).
/// </summary>
/// <remarks>
/// A lookup asks each contribution in the order given (the registration hands them over latest registered first)
/// and returns the first pinned id found; <see cref="GetAll"/> lists every contribution's pairs, each type once, at
/// the first contribution that lists it. Whizbang.Core pins types of its own, so every host has at least two
/// contributions: Whizbang.Core's and its own.
/// </remarks>
/// <docs>fundamentals/identity/pinned-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/PinnedIdRegistryContributionTests.cs</tests>
public sealed class CompositePinnedIdRegistry : IPinnedIdRegistry {
  private readonly IPinnedIdRegistry[] _registries;
  private readonly IReadOnlyList<(Type Type, string PinnedId)> _all;

  /// <summary>Combines the given registries, in the order given.</summary>
  /// <param name="registries">The contributions, latest registered first.</param>
  public CompositePinnedIdRegistry(IEnumerable<IPinnedIdRegistry> registries) {
    ArgumentNullException.ThrowIfNull(registries);
    _registries = [.. registries];
    var seen = new HashSet<Type>();
    _all = [.. _registries.SelectMany(registry => registry.GetAll()).Where(pair => seen.Add(pair.Type))];
  }

  /// <inheritdoc />
  public string? GetPinnedId(Type type) {
    foreach (var registry in _registries) {
      if (registry.GetPinnedId(type) is { } pinnedId) {
        return pinnedId;
      }
    }
    return null;
  }

  /// <inheritdoc />
  public IReadOnlyList<(Type Type, string PinnedId)> GetAll() => _all;
}

/// <summary>
/// Registers one assembly's <see cref="IPinnedIdRegistry"/> as a contribution to the registry the container serves.
/// </summary>
/// <remarks>
/// Every assembly that pins types generates a registry and its module initializer registers it here. The single
/// startup slot each one used to fill was overwritten by the next assembly's, and the registry was registered as a
/// plain singleton, so only one assembly's pinned types were ever known (#2198). The served registry is a
/// <see cref="CompositePinnedIdRegistry"/> over every contribution, latest registered first.
/// </remarks>
/// <docs>fundamentals/identity/pinned-identity</docs>
/// <tests>tests/Whizbang.Core.Tests/PinnedIdRegistryContributionTests.cs</tests>
public static class PinnedIdRegistryServiceCollectionExtensions {
  /// <summary>The service key each assembly's registry is registered under as a contribution.</summary>
  public const string CONTRIBUTION_KEY = "whizbang:pinned-id-registry-contribution";

  /// <summary>Adds <typeparamref name="TRegistry"/>'s pinned types to the <see cref="IPinnedIdRegistry"/> the container serves.</summary>
  /// <typeparam name="TRegistry">The contributing registry, itself registered in the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Core.Tests/PinnedIdRegistryContributionTests.cs:TwoAssemblies_EachAssemblysPinnedTypeIsKnownAsync</tests>
  public static IServiceCollection AddPinnedIdRegistryContribution<TRegistry>(this IServiceCollection services)
      where TRegistry : class, IPinnedIdRegistry {
    ArgumentNullException.ThrowIfNull(services);
    return services.AddContribution<IPinnedIdRegistry, TRegistry>(
      CONTRIBUTION_KEY,
      static registries => new CompositePinnedIdRegistry(registries),
      static registry => registry.GetAll().Select(pair => TypeNameFormatter.DisplayName(pair.Type)));
  }
}
