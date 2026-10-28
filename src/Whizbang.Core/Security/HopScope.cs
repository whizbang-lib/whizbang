// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Builds the scope a message carries on its hop: the one place every dispatch path (send, publish, the
/// outbox, batches, scheduled and deferred messages, local runs, cascades, the transport bridges and the
/// event-store decorator) turns a scope into the <see cref="ScopeDelta"/> stamped on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The hop carries the whole <see cref="PerspectiveScope"/>: tenant, user, customer, organization, allowed
/// principals, extensions and the system markers. A receiving service restores it into its scope context, so
/// <c>IScopeContext.Scope.Extensions</c> there match the request that started the chain.
/// </para>
/// <para>
/// Claims cross only by type, when the context lists the type in
/// <see cref="ImmutableScopeContext.PropagatedClaimTypes"/>; the list is empty unless the application opted
/// in. A claim restored from a hop was listed where the chain started, so it keeps travelling.
/// </para>
/// <para>
/// A scope with no tenant and no user yields no hop scope, as before, so the system and declared-unscoped
/// markers still decide for traffic that carries no authority.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/message-security#hop-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/HopScopeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/HopScopeDispatchParityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/HopScopeTwoHopWireTests.cs</tests>
public static class HopScope {

  /// <summary>
  /// The hop scope of the ambient scope context: its whole scope and the claims it lists for propagation, or
  /// <see langword="null"/> when there is no propagating context or it names no tenant or user.
  /// </summary>
  /// <returns>The hop scope, or null.</returns>
  public static ScopeDelta? FromAmbient() =>
    _propagatingAmbient() is { } ambient ? _withAuthority(ambient.Scope, _propagatedClaims(ambient)) : null;

  /// <summary>
  /// The hop scope of an explicit message context, or <see langword="null"/> when it carries no tenant or user.
  /// </summary>
  /// <remarks>
  /// The context's tenant and user are authoritative. The scope they belong to (the context's own scope
  /// context, or the ambient one when it has none) lends its extensions and listed claims only when it names
  /// the same tenant and user: extensions and claims travel with the identity they were established for.
  /// </remarks>
  /// <param name="context">The message context the message is dispatched under.</param>
  /// <returns>The hop scope, or null.</returns>
  public static ScopeDelta? FromMessageContext(IMessageContext context) {
    ArgumentNullException.ThrowIfNull(context);
    if (string.IsNullOrEmpty(context.TenantId) && string.IsNullOrEmpty(context.UserId)) {
      return null;
    }

    var source = _scopeSource(context.ScopeContext);
    if (source is null || !_sameIdentity(source.Scope, context.TenantId, context.UserId)) {
      return ScopeDelta.ForHop(new PerspectiveScope { TenantId = context.TenantId, UserId = context.UserId }, claims: null);
    }
    return ScopeDelta.ForHop(source.Scope, _propagatedClaims(source));
  }

  /// <summary>
  /// The hop scope carried by a source envelope, with every claim on its hops, or <see langword="null"/> when
  /// it carries no tenant or user.
  /// </summary>
  /// <param name="envelope">The envelope the message descends from, or null.</param>
  /// <returns>The hop scope, or null.</returns>
  public static ScopeDelta? FromEnvelope(IMessageEnvelope? envelope) =>
    envelope?.GetCurrentScope() is { } carried ? _withAuthority(carried.Scope, carried.Claims) : null;

  /// <summary>
  /// The claim types of a scope restored from hops, all of which may travel on: each was listed where the
  /// chain started.
  /// </summary>
  /// <param name="claims">The restored claims.</param>
  /// <returns>Their types.</returns>
  internal static IReadOnlySet<string> ClaimTypesOf(IReadOnlyDictionary<string, string> claims) =>
    claims.Count == 0 ? FrozenSet<string>.Empty : claims.Keys.ToFrozenSet(StringComparer.Ordinal);

  private static ImmutableScopeContext? _propagatingAmbient() =>
    ScopeContextAccessor.CurrentContext is ImmutableScopeContext { ShouldPropagate: true } ambient ? ambient : null;

  // A context's own scope context lends its scope unless it opted out of propagation; with none, the ambient one.
  private static IScopeContext? _scopeSource(IScopeContext? own) => own switch {
    null => _propagatingAmbient(),
    ImmutableScopeContext { ShouldPropagate: false } => null,
    _ => own,
  };

  private static bool _sameIdentity(PerspectiveScope scope, string? tenantId, string? userId) =>
    string.Equals(scope.TenantId ?? string.Empty, tenantId ?? string.Empty, StringComparison.Ordinal)
    && string.Equals(scope.UserId ?? string.Empty, userId ?? string.Empty, StringComparison.Ordinal);

  private static ScopeDelta? _withAuthority(PerspectiveScope scope, IReadOnlyDictionary<string, string>? claims) =>
    string.IsNullOrEmpty(scope.TenantId) && string.IsNullOrEmpty(scope.UserId) ? null : ScopeDelta.ForHop(scope, claims);

  // Only a context that names its propagated claim types can send a claim, and only those.
  private static Dictionary<string, string>? _propagatedClaims(IScopeContext context) {
    if (context is not ImmutableScopeContext { PropagatedClaimTypes.Count: > 0 } immutable) {
      return null;
    }
    Dictionary<string, string>? claims = null;
    foreach (var (type, value) in immutable.Claims) {
      if (immutable.PropagatedClaimTypes.Contains(type)) {
        (claims ??= new Dictionary<string, string>(StringComparer.Ordinal))[type] = value;
      }
    }
    return claims;
  }
}
