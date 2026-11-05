// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// <summary>
  /// Returns <c>true</c> when a stored event type belongs to this service's stream-integrity local lane: the service
  /// registered no owned domains (every event it holds without an origin is its own, the behavior before ownership was
  /// consulted), or the type's namespace is owned. An event of a type whose namespace is not owned was authored by
  /// another service, whatever its origin column says, so it is never advertised, redelivered or counted as this
  /// service's own.
  /// </summary>
  /// <param name="storedEventType">The stored type name (<c>wh_event_store.event_type</c>, the wire form).</param>
  /// <param name="ownedDomains">The declared owned domains (see <see cref="RoutingOptions.OwnedDomains"/>).</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="ownedDomains"/> is null.</exception>
  /// <docs>resilience/stream-integrity#the-local-lane-follows-ownership</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/OwnedNamespaceMatcherLocalLaneTests.cs</tests>
  public static bool IsLocalEventType(string? storedEventType, IReadOnlyCollection<string> ownedDomains) {
    ArgumentNullException.ThrowIfNull(ownedDomains);
    if (ownedDomains.Count == 0) {
      return true;
    }
    return !string.IsNullOrEmpty(storedEventType)
      && IsOwned(TypeNameFormatter.GetNamespace(storedEventType), ownedDomains);
  }

  /// <summary>
  /// The form a declared domain is exchanged in: trimmed, without a trailing dot. <c>"app.orders."</c> and
  /// <c>"app.orders"</c> declare the same domain; case is left as declared and compared without it.
  /// </summary>
  /// <param name="domain">A declared owned domain.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="domain"/> is null.</exception>
  /// <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/OwnedNamespaceMatcherLocalLaneTests.cs:NormalizeDomain_TrimsWhitespaceAndATrailingDotAsync</tests>
  public static string NormalizeDomain(string domain) {
    ArgumentNullException.ThrowIfNull(domain);
    return domain.Trim().TrimEnd('.');
  }
}
