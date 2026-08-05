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
}
