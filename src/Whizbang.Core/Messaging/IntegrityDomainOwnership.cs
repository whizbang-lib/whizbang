// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One service's claim to own a domain, as announced in <see cref="IntegrityCheckpoint.OwnedDomains"/> and persisted
/// by every consumer that received it (#2008).
/// </summary>
/// <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDomainOwnershipTests.cs</tests>
public sealed record IntegrityDomainClaim {
  /// <summary>The claimed domain (a namespace and its children), compared without case.</summary>
  public required string Domain { get; init; }

  /// <summary>The claiming service's stable id.</summary>
  public required Guid OwnerServiceId { get; init; }

  /// <summary>The claiming service's logical name, the directed-message target for requests to it.</summary>
  public required string OwnerServiceName { get; init; }

  /// <summary>The topic the owner consumes directed integrity requests on, when it announced one.</summary>
  public string? RequestTopic { get; init; }
}

/// <summary>
/// The domain to owner map a consumer builds from the claims other services announce on their checkpoints (#2008).
/// It names the owner of a stored event type by the most specific claimed domain that contains the type's namespace,
/// using the same hierarchical rule as routing (<see cref="OwnedNamespaceMatcher"/>). A domain two owners claim has
/// no owner: neither claimant is used for it, and no parent claim stands in for it.
/// </summary>
/// <remarks>
/// Used to re-attribute a store's pre-origin history to the service that wrote it, to direct repair and backfill at
/// the owner, and to ignore an origin's counts for types another service owns (#2032). Immutable: build a new one from
/// the persisted claims whenever it is needed.
/// </remarks>
/// <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDomainOwnershipTests.cs</tests>
public sealed class IntegrityDomainOwnership {
  private readonly Dictionary<string, IntegrityDomainClaim> _owners;
  private readonly HashSet<string> _conflicted;

  /// <summary>A map with no claims: every type has no known owner.</summary>
  public static IntegrityDomainOwnership Empty { get; } = new([]);

  /// <summary>Builds the map from persisted claims.</summary>
  /// <param name="claims">Every claim on record, from any number of owners.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="claims"/> is null.</exception>
  public IntegrityDomainOwnership(IEnumerable<IntegrityDomainClaim> claims) {
    ArgumentNullException.ThrowIfNull(claims);
    _owners = new Dictionary<string, IntegrityDomainClaim>(StringComparer.OrdinalIgnoreCase);
    _conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var claim in claims) {
      var domain = OwnedNamespaceMatcher.NormalizeDomain(claim.Domain).ToLowerInvariant();
      if (domain.Length == 0 || _conflicted.Contains(domain)) {
        continue;
      }
      if (!_owners.TryGetValue(domain, out var existing)) {
        _owners[domain] = claim with { Domain = domain };
      } else if (existing.OwnerServiceId != claim.OwnerServiceId) {
        _owners.Remove(domain);
        _conflicted.Add(domain);
      }
    }
  }

  /// <summary>The domains with exactly one owner, one claim each.</summary>
  public IReadOnlyList<IntegrityDomainClaim> Owners => [.. _owners.Values];

  /// <summary>The domains two or more owners claim; none of them is used.</summary>
  public IReadOnlyList<string> ConflictedDomains => [.. _conflicted];

  /// <summary>
  /// The owner of <paramref name="storedEventType"/>: the claim of the most specific domain containing the type's
  /// namespace, or null when no domain contains it or the most specific one is contested.
  /// </summary>
  /// <param name="storedEventType">A stored type name (<c>wh_event_store.event_type</c>, the wire form).</param>
  public IntegrityDomainClaim? FindOwner(string storedEventType) {
    var ns = TypeNameFormatter.GetNamespace(storedEventType);
    string? best = null;
    foreach (var domain in _owners.Keys.Concat(_conflicted)) {
      if ((best is null || domain.Length > best.Length) && OwnedNamespaceMatcher.IsOwned(ns, [domain])) {
        best = domain;
      }
    }
    return best is not null && _owners.TryGetValue(best, out var owner) ? owner : null;
  }

  /// <summary>
  /// True when a known owner other than <paramref name="originServiceId"/> owns <paramref name="storedEventType"/>:
  /// that origin's counts and digests for the type are not authoritative, and repair is not asked of it.
  /// </summary>
  /// <param name="storedEventType">A stored type name.</param>
  /// <param name="originServiceId">The origin that advertised the type.</param>
  public bool IsOwnedByAnother(string storedEventType, Guid originServiceId) =>
    FindOwner(storedEventType) is { } owner && owner.OwnerServiceId != originServiceId;

  /// <summary>
  /// True when <paramref name="originServiceId"/>'s counts and digests for <paramref name="storedEventType"/> do not
  /// stand: a known owner other than that origin owns the type, or, when the map names no owner, the type is in one of
  /// this service's own owned domains (a service never announces its own domains to itself, so the map alone cannot
  /// say so). Such an origin only received the type's events; it is not compared against, repair is not asked of it, and
  /// a ledger row recorded under it is forgotten, because its answers leave the type out and the row can never clear.
  /// </summary>
  /// <remarks>
  /// An origin the map names as the type's owner stands even when this service also owns the domain: two services
  /// claiming one domain is a contest the checkpoint reports (<see cref="IntegrityOwnershipConflicts"/>), not a verdict.
  /// </remarks>
  /// <param name="storedEventType">A stored type name.</param>
  /// <param name="originServiceId">The origin that reported the type.</param>
  /// <param name="localOwnedDomains">This service's own owned domains.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="localOwnedDomains"/> is null.</exception>
  /// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDomainOwnershipTests.cs:IsNotAuthoritative_ATypeThisServiceOwns_IsNoOtherOriginsAsync</tests>
  public bool IsNotAuthoritative(string storedEventType, Guid originServiceId, IReadOnlyCollection<string> localOwnedDomains) {
    ArgumentNullException.ThrowIfNull(localOwnedDomains);
    if (FindOwner(storedEventType) is { } owner) {
      return owner.OwnerServiceId != originServiceId;
    }
    return localOwnedDomains.Count > 0
      && OwnedNamespaceMatcher.IsOwned(TypeNameFormatter.GetNamespace(storedEventType), localOwnedDomains);
  }

  /// <summary>
  /// A stable fingerprint of the map together with this service's own owned domains, independent of order and case.
  /// Re-attribution rewalks the store from the start whenever it changes, so history parked as unattributed reaches an
  /// owner learned later.
  /// </summary>
  /// <param name="ownedDomains">This service's own owned domains.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="ownedDomains"/> is null.</exception>
  public string Fingerprint(IReadOnlyCollection<string> ownedDomains) {
    ArgumentNullException.ThrowIfNull(ownedDomains);
    var text = new StringBuilder();
    foreach (var own in ownedDomains.Select(d => OwnedNamespaceMatcher.NormalizeDomain(d).ToLowerInvariant()).Order(StringComparer.Ordinal)) {
      text.Append("own:").Append(own).Append('\n');
    }
    foreach (var (domain, owner) in _owners.OrderBy(o => o.Key, StringComparer.Ordinal)) {
      text.Append(domain).Append('=').Append(owner.OwnerServiceId.ToString("N")).Append('\n');
    }
    foreach (var domain in _conflicted.Order(StringComparer.Ordinal)) {
      text.Append(domain).Append("=*\n");
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
  }
}

/// <summary>
/// Remembers which contested domains have been reported, so a conflict between the same two owners over the same domain
/// is warned and counted once per process however many checkpoints repeat it (#2008). Registered as a singleton.
/// </summary>
/// <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDomainOwnershipTests.cs:Conflicts_WarnOncePerPair_InEitherOrderAsync</tests>
public sealed class IntegrityOwnershipConflicts {
  private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

  /// <summary>True the first time the unordered pair (<paramref name="a"/>, <paramref name="b"/>) is seen contesting
  /// <paramref name="domain"/>; false on every later sighting, in either order.</summary>
  /// <param name="domain">The contested domain, compared without case.</param>
  /// <param name="a">One claimant.</param>
  /// <param name="b">The other claimant.</param>
  public bool FirstSighting(string domain, Guid a, Guid b) {
    ArgumentNullException.ThrowIfNull(domain);
    var (low, high) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    return _seen.TryAdd($"{OwnedNamespaceMatcher.NormalizeDomain(domain).ToLowerInvariant()}|{low:N}|{high:N}", 0);
  }
}
