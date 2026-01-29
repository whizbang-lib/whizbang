namespace Whizbang.Core.Routing;

/// <summary>
/// All commands route to a single shared topic with broker-side filtering.
/// Default strategy - minimizes topic count.
/// </summary>
/// <remarks>
/// ASB: Uses CorrelationFilter on Destination property.
/// RabbitMQ: Uses routing key pattern matching.
/// </remarks>
/// <docs>core-concepts/routing#shared-topic-inbox</docs>
public sealed class SharedTopicInboxStrategy : IInboxRoutingStrategy {
  private readonly string _inboxTopic;

  /// <summary>
  /// Creates a shared topic inbox strategy with default topic name.
  /// </summary>
  public SharedTopicInboxStrategy()
      : this("whizbang.inbox") { }

  /// <summary>
  /// Creates a shared topic inbox strategy with custom topic name.
  /// </summary>
  /// <param name="inboxTopic">The shared inbox topic name.</param>
  public SharedTopicInboxStrategy(string inboxTopic) {
    _inboxTopic = inboxTopic ?? throw new ArgumentNullException(nameof(inboxTopic));
  }

  /// <inheritdoc />
  public InboxSubscription GetSubscription(
    IReadOnlySet<string> ownedDomains,
    string serviceName,
    MessageKind kind
  ) {
    ArgumentNullException.ThrowIfNull(ownedDomains);

    // Build filter expression - comma-separated list of owned domains
    var filterExpression = string.Join(",", ownedDomains);

    // Build routing pattern for RabbitMQ
    var routingPattern = _buildRoutingPattern(ownedDomains);

    // Build metadata for transport-specific configuration
    var metadata = new Dictionary<string, object> {
      ["DestinationFilter"] = filterExpression,  // ASB CorrelationFilter
      ["RoutingPattern"] = routingPattern         // RabbitMQ routing key pattern
    };

    return new InboxSubscription(
      Topic: _inboxTopic,
      FilterExpression: filterExpression,
      Metadata: metadata
    );
  }

  /// <summary>
  /// Builds routing pattern for RabbitMQ topic exchange.
  /// </summary>
  private static string _buildRoutingPattern(IReadOnlySet<string> domains) {
    // For single domain: "orders.#"
    // For multiple domains: "#" (catch-all, rely on message filtering)
    // Note: Multiple bindings for multi-domain would be handled by transport layer
    if (domains.Count == 1) {
      return $"{domains.First()}.#";
    }

    return "#";
  }
}
