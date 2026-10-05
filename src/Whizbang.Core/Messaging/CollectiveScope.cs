// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Serializable polymorphic base for the scope payload an
/// <see cref="ICollectiveEvent"/> carries. A collective event is a
/// first-class persisted event (<see cref="IEvent"/>), so its
/// <see cref="ICollectiveEvent.Scope"/> must round-trip through the
/// AOT-strict, reflection-free message-serialization pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an abstract class and not the <see cref="ICollectiveScope"/>
/// interface.</strong> The AOT serializability analyzer (WHIZ062) rejects a
/// bare non-generic interface property on a command/event type — an interface
/// has no concrete shape to source-generate a serializer for. The Whizbang
/// pattern for a single polymorphic value on a serializable type is an abstract
/// base class with <see cref="JsonDerivedTypeAttribute"/> discriminators (the
/// same shape as <c>AbstractFieldSettings</c>), which the message JSON-context
/// generator discovers and emits a polymorphic <c>$type</c> serializer for.
/// </para>
/// <para>
/// <see cref="CollectiveScope"/> still implements <see cref="ICollectiveScope"/>
/// so the resolver surface (<see cref="Perspectives.ICollectiveScopeResolver"/>,
/// <c>EnterContext</c>, <c>ScopeFilter</c>) is unchanged — the interface remains
/// the behavioral contract; this class is the serialization base.
/// </para>
/// <para>
/// Built-in scopes register here via <see cref="JsonDerivedTypeAttribute"/>.
/// Consumer-defined scopes derive from <see cref="CollectiveScope"/> and are
/// registered for polymorphic serialization the same way other open-set
/// derived types are (see the collective-events doc).
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:TenantCollectiveScope_CarriesTenantIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/TenantCollectiveScopeResolverTests.cs:TenantCollectiveScope_ScopeKind_IsTenantAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CollectiveScopeBaseRoutingTests.cs:CollectiveScope_RoundTripsPolymorphically_ViaScopeKindDiscriminatorAsync</tests>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$scopeKind")]
[JsonDerivedType(typeof(TenantCollectiveScope), "tenant")]
public abstract record CollectiveScope : ICollectiveScope {
  /// <inheritdoc/>
  public abstract string ScopeKind { get; }

  /// <inheritdoc/>
  /// <remarks>
  /// Abstract here rather than defaulted, deliberately. The interface defaults it to the kind for a
  /// scope with no instance data, and a scope that derives from this base almost always has some --
  /// so stating it is the point. A tenant scope that inherited the default put every tenant on one
  /// advisory lock per table, and nothing said so: applies still completed, one tenant at a time,
  /// until the waiters hit a command timeout. A scope with genuinely nothing to add returns
  /// <see cref="ScopeKind"/>, and says as much where it does.
  /// </remarks>
  public abstract string ScopeIdentity { get; }

  /// <summary>The discriminator, which is also what a scope reads as in logs and diagnostics.</summary>
  /// <remarks>
  /// The kind alone, not the scope's identity. Do not build a key from this -- see
  /// <see cref="ScopeIdentity"/>, which exists because a key was built from this.
  /// </remarks>
  public sealed override string ToString() => ScopeKind;
}
