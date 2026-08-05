namespace Whizbang.Core.Routing;

/// <summary>
/// Inbox subscription configuration returned by IInboxRoutingStrategy.
/// </summary>
/// <param name="Topic">Topic/exchange to subscribe to.</param>
/// <param name="FilterExpression">ASB: CorrelationFilter, RabbitMQ: routing pattern. Null if topic IS the filter.</param>
/// <param name="Metadata">Transport-specific metadata (DestinationFilter, RoutingPattern, etc.).</param>
/// <docs>fundamentals/dispatcher/routing#inbox-subscription</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:InboxSubscription_WithTopicOnly_CreatesValidRecordAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:InboxSubscription_WithFilter_CreatesValidRecordAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:InboxSubscription_WithMetadata_CreatesValidRecordAsync</tests>
public sealed record InboxSubscription(
  string Topic,
  string? FilterExpression = null,
  IReadOnlyDictionary<string, object>? Metadata = null
);
