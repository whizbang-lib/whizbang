using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// Publishes messages to namespace-specific topics.
/// Topic is the full namespace, routing key is the type name.
/// </summary>
/// <remarks>
/// <para>
/// Example for MyApp.Users.Events.TenantCreatedEvent:
/// - Topic: "myapp.users.events"
/// - Routing Key: "tenantcreatedevent"
/// </para>
/// <para>
/// This enables direct subscription to event namespaces:
/// services subscribe to namespaces they care about.
/// </para>
/// </remarks>
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

    // Topic = full namespace (e.g., "myapp.users.events")
    var ns = _topicResolver.ResolveTopic(messageType, "", null);

    // Routing key = type name (e.g., "tenantcreatedevent")
    var routingKey = messageType.Name.ToLowerInvariant();

    return new TransportDestination(
      Address: ns,
      RoutingKey: routingKey,
      Metadata: null
    );
  }
}
