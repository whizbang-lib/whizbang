// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Routing;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Names the true owner of an event recorded under an origin that does not own its type (#2116). A redelivery bundle
/// names the service that answered it as every child's origin; a service that answered with events it had only received
/// shipped another service's events as its own. The owner is the one this receiver's ownership map names
/// (<see cref="IntegrityDomainOwnership"/>, built from the domains other services announce on their checkpoints), by the
/// same hierarchical rule as everything else that reads ownership.
/// </summary>
/// <remarks>
/// The receive path asks this for every child of a redelivery bundle, and the misattributed-origin correction asks it for
/// every type it finds recorded under an origin. A type with no known owner, a contested one, a type the named origin owns
/// and a type this service owns itself all answer null: the record is left as it is.
/// </remarks>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MisattributedOriginTests.cs</tests>
public static class MisattributedOrigin {
  /// <summary>
  /// The owner of <paramref name="storedEventType"/> when it is a known service other than
  /// <paramref name="namedOrigin"/>; otherwise null.
  /// </summary>
  /// <param name="storedEventType">The stored or wire type name of the event.</param>
  /// <param name="namedOrigin">The origin the event is recorded under, or that the bundle carrying it names.</param>
  /// <param name="ownership">This receiver's ownership map.</param>
  /// <param name="localOwnedDomains">This service's own owned domains: a type in one of them is this service's, and its
  /// record is not this check's to change.</param>
  /// <returns>The true owner's service id, or null when the record stands.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="ownership"/> or
  /// <paramref name="localOwnedDomains"/> is null.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/MisattributedOriginTests.cs:TrueOwner_ATypeAnotherServiceOwns_IsThatOwnerAsync</tests>
  public static Guid? TrueOwner(
      string storedEventType, Guid namedOrigin, IntegrityDomainOwnership ownership, IReadOnlyCollection<string> localOwnedDomains) {
    ArgumentNullException.ThrowIfNull(ownership);
    ArgumentNullException.ThrowIfNull(localOwnedDomains);
    if (ownership.FindOwner(storedEventType) is not { } owner || owner.OwnerServiceId == namedOrigin) {
      return null;
    }
    if (localOwnedDomains.Count > 0 && OwnedNamespaceMatcher.IsOwned(TypeNameFormatter.GetNamespace(storedEventType), localOwnedDomains)) {
      return null;
    }
    return owner.OwnerServiceId;
  }

  /// <summary>
  /// A lookup for the children of one redelivery bundle: each wire type's true owner when it is not
  /// <paramref name="bundleOrigin"/>, or null when the child's origin stands. Null itself when the map knows no owner at
  /// all, so the caller does no per-child work.
  /// </summary>
  /// <param name="bundleOrigin">The origin the bundle names.</param>
  /// <param name="ownership">This receiver's ownership map.</param>
  /// <param name="localOwnedDomains">This service's own owned domains.</param>
  /// <returns>The lookup, or null when no owner is known.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="ownership"/> or
  /// <paramref name="localOwnedDomains"/> is null.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/MisattributedOriginTests.cs:ForBundle_WithNoKnownOwner_IsNullAsync</tests>
  public static Func<string, Guid?>? ForBundle(
      Guid bundleOrigin, IntegrityDomainOwnership ownership, IReadOnlyCollection<string> localOwnedDomains) {
    ArgumentNullException.ThrowIfNull(ownership);
    ArgumentNullException.ThrowIfNull(localOwnedDomains);
    if (ownership.Owners.Count == 0) {
      return null;
    }
    return wireType => TrueOwner(wireType, bundleOrigin, ownership, localOwnedDomains);
  }
}
