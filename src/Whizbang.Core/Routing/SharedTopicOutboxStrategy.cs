using System.Text.Json;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// Unified outbox routing strategy for namespace-based message routing.
/// </summary>
/// <remarks>
/// <para>
/// Commands are routed to a shared "inbox" topic with namespace-based routing keys
/// for broker-side filtering. Routing key format: "{namespace}.{typename}".
/// Example: "myapp.users.commands.createtenantcommand"
/// </para>
/// <para>
/// Events are routed to namespace-specific topics for direct subscription.
/// Topic is the full namespace, routing key is the type name.
/// Example: Topic "myapp.users.events", routing key "tenantcreatedevent"
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#shared-topic-outbox</docs>
/// <remarks>
/// Creates a shared topic outbox strategy with custom inbox topic and resolver.
/// </remarks>
/// <param name="inboxTopic">The shared inbox topic name for commands.</param>
/// <param name="topicResolver">Strategy for resolving namespace from message type.</param>
public sealed class SharedTopicOutboxStrategy(string inboxTopic, ITopicRoutingStrategy topicResolver)
    : IOutboxRoutingStrategy, ICommandInboxAddressResolver {
  /// <summary>
  /// The default inbox topic name for commands.
  /// </summary>
  private const string DEFAULT_INBOX_TOPIC = "inbox";

  /// <summary>
  /// Gets the default inbox topic name for commands.
  /// </summary>
  public static string DefaultInboxTopic => DEFAULT_INBOX_TOPIC;

  /// <summary>
  /// Gets the configured inbox topic name for this strategy instance.
  /// </summary>
  public string InboxTopic { get; } = inboxTopic ?? throw new ArgumentNullException(nameof(inboxTopic));
  private readonly ITopicRoutingStrategy _topicResolver = topicResolver ?? throw new ArgumentNullException(nameof(topicResolver));

  /// <summary>
  /// The command-inbox seam (topology arc phase 7): the shared-topic strategy's default is
  /// its configured inbox topic — the one address every command rides.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/CommandInboxAddressResolverTests.cs:SharedTopicOutboxStrategy_ImplementsTheSeam_DefaultIsInboxTopic_NeverFlipsAsync</tests>
  public string DefaultCommandInboxAddress => InboxTopic;

  /// <summary>
  /// The shared-topic strategy never flips a namespace: always null, so the publish-time
  /// caller keeps today's shared-inbox wire shape byte-identically for every command.
  /// </summary>
  /// <param name="contractNamespace">Ignored.</param>
  /// <returns>Always null.</returns>
  public string? ResolveFlippedCommandInboxAddress(string? contractNamespace) => null;

  /// <summary>
  /// Creates a shared topic outbox strategy with defaults.
  /// </summary>
  public SharedTopicOutboxStrategy()
      : this(DEFAULT_INBOX_TOPIC, new NamespaceRoutingStrategy()) { }

  /// <summary>
  /// Creates a shared topic outbox strategy with custom inbox topic name.
  /// </summary>
  /// <param name="inboxTopic">The shared inbox topic name for commands.</param>
  public SharedTopicOutboxStrategy(string inboxTopic)
      : this(inboxTopic, new NamespaceRoutingStrategy()) { }

  /// <inheritdoc />
  public TransportDestination GetDestination(
    Type messageType,
    IReadOnlySet<string> ownedDomains,
    MessageKind kind
  ) {
    ArgumentNullException.ThrowIfNull(messageType);
    ArgumentNullException.ThrowIfNull(ownedDomains);

    // Get the full namespace from the message type
    var ns = _topicResolver.ResolveTopic(messageType, "", null);
    var typeName = messageType.Name.ToLowerInvariant();

    if (kind == MessageKind.Command) {
      // Commands go to shared inbox topic with namespace-based routing key
      // This allows services to filter by owned command namespaces
      var routingKey = $"{ns}.{typeName}";  // "myapp.users.commands.createtenantcommand"

      return new TransportDestination(
        Address: InboxTopic,
        RoutingKey: routingKey,
        Metadata: _createMetadata(ns, kind)
      );
    }

    if (kind == MessageKind.System) {
      // DORMANT CAPABILITY (topology arc phase 3): no production call site passes System
      // yet. System traffic routes to the SAME shared inbox commands use today, with the
      // same "{namespace}.{typename}" routing-key shape — bit-identical to command routing
      // by construction. A later phase (dedicated system broadcast inbox, shared-inbox
      // retirement) redirects this branch to the broadcast inbox entity.
      var systemRoutingKey = $"{ns}.{typeName}";  // "whizbang.core.commands.system.rebuildperspectivecommand"

      return new TransportDestination(
        Address: InboxTopic,
        RoutingKey: systemRoutingKey,
        Metadata: _createMetadata(ns, kind)
      );
    }

    // Events go to namespace-specific topic
    // Subscribers bind directly to namespace topics they care about
    return new TransportDestination(
      Address: ns,           // "myapp.users.events"
      RoutingKey: typeName,  // "tenantcreatedevent"
      Metadata: _createMetadata(ns, kind)
    );
  }

  /// <summary>
  /// Creates metadata for transport-specific features.
  /// </summary>
  private static Dictionary<string, JsonElement> _createMetadata(string ns, MessageKind kind) {
    return new Dictionary<string, JsonElement> {
      ["Namespace"] = _createStringElement(ns),
      ["Kind"] = _createStringElement(kind.ToString())
    };
  }

  /// <summary>
  /// Creates a JsonElement from a string value (AOT-safe).
  /// </summary>
  private static JsonElement _createStringElement(string value) {
    using var doc = JsonDocument.Parse($"\"{value}\"");
    return doc.RootElement.Clone();
  }
}
