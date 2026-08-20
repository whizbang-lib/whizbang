using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// Determines where this service publishes events (outbox destination).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#outbox-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:Outbox_UseCustomStrategy_SetsCustomStrategyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/OutboxRoutingStrategyTests.cs:DomainTopicOutboxStrategy_GetDestination_ReturnsFullNamespaceAsTopicAsync</tests>
public interface IOutboxRoutingStrategy {
  /// <summary>
  /// Gets the destination for publishing an event.
  /// </summary>
  /// <param name="messageType">The event type being published.</param>
  /// <param name="ownedDomains">Domains this service owns (from OwnDomains() registration).</param>
  /// <param name="kind">Message kind (usually Event).</param>
  /// <returns>Transport destination for publishing.</returns>
  /// <exception cref="ArgumentNullException">Thrown when messageType or ownedDomains is null.</exception>
  TransportDestination GetDestination(
    Type messageType,
    IReadOnlySet<string> ownedDomains,
    MessageKind kind
  );

  /// <summary>
  /// Gets the canonical grouping key for batching/splitting constituents into composite
  /// events. Constituents with the same key MUST share the same transport destination —
  /// the load-bearing invariant is <b>same key ⇔ same destination</b>, because a composite
  /// is published to exactly one destination and every constituent inside it must belong
  /// there.
  /// </summary>
  /// <remarks>
  /// Default interface member deriving the key from <see cref="GetDestination"/> as
  /// <c>Address + "|" + (RoutingKey ?? "")</c> — route, split, subscribe, and provision are
  /// all projections of GetDestination, so the default can never drift from the routing
  /// answer. Override only to group MORE coarsely while preserving the invariant.
  /// </remarks>
  /// <param name="constituentType">The constituent message type being grouped.</param>
  /// <param name="ownedDomains">Domains this service owns (from OwnDomains() registration).</param>
  /// <param name="kind">Message kind of the constituent.</param>
  /// <returns>A canonical, deterministic group key.</returns>
  /// <exception cref="ArgumentNullException">Thrown when constituentType or ownedDomains is null.</exception>
  /// <docs>fundamentals/dispatcher/routing#outbox-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/OutboxRoutingStrategyTests.cs:GetCompositeGroupKey_SameKeyIffSameDestination_AcrossStrategiesTypesAndKindsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/OutboxRoutingStrategyTests.cs:GetCompositeGroupKey_CanonicalShape_AddressPipeRoutingKeyAsync</tests>
  string GetCompositeGroupKey(
    Type constituentType,
    IReadOnlySet<string> ownedDomains,
    MessageKind kind
  ) {
    var destination = GetDestination(constituentType, ownedDomains, kind);
    return $"{destination.Address}|{destination.RoutingKey ?? string.Empty}";
  }
}
