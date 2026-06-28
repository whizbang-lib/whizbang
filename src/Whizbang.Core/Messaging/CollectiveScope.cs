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
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$scopeKind")]
[JsonDerivedType(typeof(TenantCollectiveScope), "tenant")]
public abstract record CollectiveScope : ICollectiveScope {
  /// <inheritdoc/>
  public abstract string ScopeKind { get; }
}
