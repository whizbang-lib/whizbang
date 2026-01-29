using System.Text.Json;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// All events publish to a single shared topic with metadata.
/// Alternative strategy - single topic for all events.
/// </summary>
/// <docs>core-concepts/routing#shared-topic-outbox</docs>
public sealed class SharedTopicOutboxStrategy : IOutboxRoutingStrategy {
  private readonly string _outboxTopic;
  private readonly ITopicRoutingStrategy _topicResolver;

  /// <summary>
  /// Creates a shared topic outbox strategy with defaults.
  /// </summary>
  public SharedTopicOutboxStrategy()
      : this("whizbang.events", new NamespaceRoutingStrategy()) { }

  /// <summary>
  /// Creates a shared topic outbox strategy with custom topic name.
  /// </summary>
  /// <param name="outboxTopic">The shared outbox topic name.</param>
  public SharedTopicOutboxStrategy(string outboxTopic)
      : this(outboxTopic, new NamespaceRoutingStrategy()) { }

  /// <summary>
  /// Creates a shared topic outbox strategy with custom topic and resolver.
  /// </summary>
  /// <param name="outboxTopic">The shared outbox topic name.</param>
  /// <param name="topicResolver">Strategy for resolving domain from message type.</param>
  public SharedTopicOutboxStrategy(string outboxTopic, ITopicRoutingStrategy topicResolver) {
    _outboxTopic = outboxTopic ?? throw new ArgumentNullException(nameof(outboxTopic));
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

    // Extract domain from message type for routing and metadata
    var domain = _topicResolver.ResolveTopic(messageType, "", null);

    // Compound routing key: domain.typename
    var routingKey = $"{domain}.{messageType.Name.ToLowerInvariant()}";

    // Include domain in metadata for filtering
    // Use JsonDocument.Parse for AOT-safe JSON element creation
    var metadata = new Dictionary<string, JsonElement> {
      ["Domain"] = _createStringElement(domain)
    };

    return new TransportDestination(
      Address: _outboxTopic,
      RoutingKey: routingKey,
      Metadata: metadata
    );
  }

  /// <summary>
  /// Creates a JsonElement from a string value (AOT-safe).
  /// </summary>
  private static JsonElement _createStringElement(string value) {
    using var doc = JsonDocument.Parse($"\"{value}\"");
    return doc.RootElement.Clone();
  }
}
