using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// Each domain publishes to its own topic.
/// Default strategy - clear domain separation.
/// </summary>
/// <docs>core-concepts/routing#domain-topic-outbox</docs>
public sealed class DomainTopicOutboxStrategy : IOutboxRoutingStrategy {
  private readonly ITopicRoutingStrategy _topicResolver;

  /// <summary>
  /// Creates a domain topic outbox strategy with default namespace routing.
  /// </summary>
  public DomainTopicOutboxStrategy()
      : this(new NamespaceRoutingStrategy()) { }

  /// <summary>
  /// Creates a domain topic outbox strategy with custom topic resolution.
  /// </summary>
  /// <param name="topicResolver">Strategy for resolving topic from message type.</param>
  public DomainTopicOutboxStrategy(ITopicRoutingStrategy topicResolver) {
    _topicResolver = topicResolver ?? throw new ArgumentNullException(nameof(topicResolver));
  }

  /// <inheritdoc />
  public TransportDestination GetDestination(
    Type messageType,
    IReadOnlySet<string> ownedDomains,
    MessageKind kind
  ) {
    ArgumentNullException.ThrowIfNull(messageType);
    ArgumentNullException.ThrowIfNull(ownedDomains);

    // Extract domain from message type namespace
    var domain = _topicResolver.ResolveTopic(messageType, "", null);

    // Routing key is lowercase type name
    var routingKey = messageType.Name.ToLowerInvariant();

    return new TransportDestination(
      Address: domain,
      RoutingKey: routingKey,
      Metadata: null
    );
  }
}
