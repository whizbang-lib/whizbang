namespace Whizbang.Core.Routing;

/// <summary>
/// Determines where this service receives commands (inbox subscription).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#inbox-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:Inbox_UseCustomStrategy_SetsCustomStrategyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:SharedTopicInboxStrategy_GetSubscription_ReturnsDefaultInboxTopicAsync</tests>
public interface IInboxRoutingStrategy {
  /// <summary>
  /// Gets the subscription configuration for receiving commands.
  /// </summary>
  /// <param name="ownedDomains">Domains this service owns (from OwnDomains() registration).</param>
  /// <param name="serviceName">Name of this service.</param>
  /// <param name="kind">Message kind (Command or Query).</param>
  /// <returns>Subscription configuration with topic and filter.</returns>
  /// <exception cref="ArgumentNullException">Thrown when ownedDomains is null.</exception>
  InboxSubscription GetSubscription(
    IReadOnlySet<string> ownedDomains,
    string serviceName,
    MessageKind kind
  );
}
