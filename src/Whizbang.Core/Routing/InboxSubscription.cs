namespace Whizbang.Core.Routing;

/// <summary>
/// Inbox subscription configuration returned by IInboxRoutingStrategy.
/// </summary>
/// <param name="Topic">Topic/exchange to subscribe to.</param>
/// <param name="FilterExpression">ASB: CorrelationFilter, RabbitMQ: routing pattern. Null if topic IS the filter.</param>
/// <param name="Metadata">Transport-specific metadata (DestinationFilter, RoutingPattern, etc.).</param>
/// <docs>core-concepts/routing#inbox-subscription</docs>
public sealed record InboxSubscription(
  string Topic,
  string? FilterExpression = null,
  IReadOnlyDictionary<string, object>? Metadata = null
);
