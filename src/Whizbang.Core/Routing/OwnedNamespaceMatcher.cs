using System;
using System.Collections.Generic;

namespace Whizbang.Core.Routing;

/// <summary>
/// The hierarchical ownership rule, in one place: a namespace is owned when it exactly matches an
/// owned domain or is a <b>child</b> of one (the owned prefix followed by a <c>.</c> boundary).
/// Matching is case-insensitive, and a trailing dot on the declared domain is tolerated.
/// </summary>
/// <remarks>
/// Event-subscription discovery uses this to keep a service from subscribing to the events it
/// publishes, and the owned-and-subscribed guard uses it to refuse a manual subscription that the
/// discovery would silently discard. Both must agree, which is why neither carries its own copy.
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#owned-namespace-matching</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/OwnedNamespaceMatcherTests.cs</tests>
public static class OwnedNamespaceMatcher {
  /// <summary>
  /// Returns <c>true</c> when <paramref name="candidate"/> exactly matches, or is a child of, any
  /// domain in <paramref name="ownedDomains"/>.
  /// </summary>
  /// <param name="candidate">The namespace to test. <c>null</c> or empty is never owned.</param>
  /// <param name="ownedDomains">The declared owned domains (see <see cref="RoutingOptions.OwnedDomains"/>).</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="ownedDomains"/> is null.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/OwnedNamespaceMatcherTests.cs:IsOwned_ChildNamespace_IsOwnedAsync</tests>
  public static bool IsOwned(string? candidate, IEnumerable<string> ownedDomains)
    => FindOwner(candidate, ownedDomains) is not null;

  /// <summary>
  /// Returns the declared owned domain that claims <paramref name="candidate"/> (exactly or as a
  /// parent), or <c>null</c> when no domain does. The returned value is the domain as declared, so a
  /// diagnostic can name the declaration the operator wrote.
  /// </summary>
  /// <param name="candidate">The namespace to test. <c>null</c> or empty is never owned.</param>
  /// <param name="ownedDomains">The declared owned domains (see <see cref="RoutingOptions.OwnedDomains"/>).</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="ownedDomains"/> is null.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/OwnedNamespaceMatcherTests.cs:IsOwned_SiblingSharingThePrefixWithoutADotBoundary_IsNotOwnedAsync</tests>
  public static string? FindOwner(string? candidate, IEnumerable<string> ownedDomains) {
    ArgumentNullException.ThrowIfNull(ownedDomains);
    if (string.IsNullOrEmpty(candidate)) {
      return null;
    }

    foreach (var owned in ownedDomains) {
      // "app.contracts.bff." and "app.contracts.bff" declare the same domain.
      var domain = owned.EndsWith('.') ? owned[..^1] : owned;
      if (domain.Length == 0) {
        continue;
      }

      if (candidate.Equals(domain, StringComparison.OrdinalIgnoreCase)) {
        return owned;
      }

      // A child: the domain, then a '.' boundary, then at least one more segment. A shared textual
      // prefix without the boundary ("app.contracts.ordersarchive" vs "app.contracts.orders") is not
      // ownership.
      if (candidate.Length > domain.Length
          && candidate[domain.Length] == '.'
          && candidate.StartsWith(domain, StringComparison.OrdinalIgnoreCase)) {
        return owned;
      }
    }

    return null;
  }
}
