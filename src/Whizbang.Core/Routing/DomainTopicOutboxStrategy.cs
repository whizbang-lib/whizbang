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
/// <docs>fundamentals/dispatcher/routing#domain-topic-outbox</docs>
/// <remarks>
/// Creates a domain topic outbox strategy with custom topic resolution.
/// </remarks>
/// <param name="topicResolver">Strategy for resolving topic from message type.</param>
public sealed class DomainTopicOutboxStrategy(ITopicRoutingStrategy topicResolver) : IOutboxRoutingStrategy {
  private readonly ITopicRoutingStrategy _topicResolver = topicResolver ?? throw new ArgumentNullException(nameof(topicResolver));

  /// <summary>
  /// Creates a domain topic outbox strategy with default namespace routing.
  /// </summary>
  public DomainTopicOutboxStrategy()
      : this(new NamespaceRoutingStrategy()) { }

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

    if (kind == MessageKind.System) {
      // DORMANT CAPABILITY (topology arc phase 3): no production call site passes System
      // yet. This strategy routes every kind to the namespace topic with the type-name
      // key, and System is handled explicitly to the SAME shape as command routing today
      // — bit-identical by construction. A later phase (dedicated system broadcast inbox,
      // shared-inbox retirement) redirects this branch to the broadcast inbox entity.
      return new TransportDestination(
        Address: ns,
        RoutingKey: routingKey,
        Metadata: null
      );
    }

    return new TransportDestination(
      Address: ns,
      RoutingKey: routingKey,
      Metadata: null
    );
  }
}
