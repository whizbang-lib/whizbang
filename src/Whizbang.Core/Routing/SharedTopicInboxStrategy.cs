namespace Whizbang.Core.Routing;

/// <summary>
/// All commands route to a single shared "inbox" topic with namespace-based filtering.
/// Services filter by owned command namespaces using routing key patterns.
/// </summary>
/// <remarks>
/// <para>
/// Routing key format: "{namespace}.{typename}" (e.g., "myapp.users.commands.createtenantcommand")
/// </para>
/// <para>
/// All services automatically subscribe to system commands (whizbang.core.commands.system.*)
/// for framework-level operations like perspective rebuilds.
/// </para>
/// <para>
/// ASB: Uses CorrelationFilter on routing key patterns.
/// RabbitMQ: Uses routing key pattern matching with wildcards.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#shared-topic-inbox</docs>
/// <remarks>
/// Creates a shared topic inbox strategy with custom topic name.
/// </remarks>
/// <param name="inboxTopic">The shared inbox topic name.</param>
public sealed class SharedTopicInboxStrategy(string inboxTopic) : IInboxRoutingStrategy {
  /// <summary>
  /// The system command namespace that all services automatically subscribe to.
  /// </summary>
  private const string SYSTEM_COMMAND_NAMESPACE = "whizbang.core.commands.system";

  /// <summary>
  /// The integrity control-plane namespace (checkpoint-driven manifest / redelivery / drill-down
  /// requests and responses) every service must admit — the messages are DIRECTED, so non-targets
  /// still discard locally; without this pattern the shared-inbox broker filter silently drops
  /// every one (no logs, no dead-letter).
  /// </summary>
  private const string CONTROL_PLANE_NAMESPACE = "whizbang.core.messaging";

  /// <summary>
  /// Gets the system command namespace that all services automatically subscribe to.
  /// </summary>
  public static string SystemCommandNamespace => SYSTEM_COMMAND_NAMESPACE;

  private readonly string _inboxTopic = inboxTopic ?? throw new ArgumentNullException(nameof(inboxTopic));

  /// <summary>
  /// Creates a shared topic inbox strategy with default topic name.
  /// </summary>
  public SharedTopicInboxStrategy()
      : this("inbox") { }

  /// <inheritdoc />
  public InboxSubscription GetSubscription(
    IReadOnlySet<string> ownedDomains,
    string serviceName,
    MessageKind kind
  ) {
    ArgumentNullException.ThrowIfNull(ownedDomains);

    // Build routing patterns from owned namespaces
    var routingPatterns = _buildRoutingPatterns(ownedDomains);

    // Build filter expression - comma-separated list of routing patterns
    var filterExpression = string.Join(",", routingPatterns);

    // Build metadata for transport-specific configuration
    var metadata = new Dictionary<string, object> {
      ["RoutingPatterns"] = routingPatterns  // For transport layer to create bindings
    };

    return new InboxSubscription(
      Topic: _inboxTopic,
      FilterExpression: filterExpression,
      Metadata: metadata
    );
  }

  /// <summary>
  /// Explicit plural override (topology arc phase 3): the shared-topic strategy has exactly
  /// one subscription — the shared inbox with the namespace filter — so the plural surface
  /// returns that same single subscription. Overridden (rather than inherited from the
  /// default interface member) so the seam is exercised by the built-in strategies;
  /// bit-identical output is locked by tests.
  /// </summary>
  /// <param name="context">Service identity, owned domains, and handled-message enumeration
  /// (unused here — the shared inbox filters by owned domains only).</param>
  /// <returns>A one-element list containing today's singular subscription.</returns>
  /// <exception cref="ArgumentNullException">Thrown when context is null.</exception>
  /// <docs>fundamentals/dispatcher/routing#shared-topic-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:SharedTopicInboxStrategy_GetSubscriptions_BitIdenticalToSingularAsync</tests>
  public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return [GetSubscription(context.OwnedDomains, context.ServiceName, MessageKind.Command)];
  }

  /// <summary>
  /// Builds routing patterns for namespace-based filtering.
  /// Always includes system commands namespace.
  /// </summary>
  /// <param name="ownedNamespaces">Namespaces owned by this service (e.g., "myapp.users.commands").</param>
  /// <returns>List of routing patterns for broker binding.</returns>
  private static List<string> _buildRoutingPatterns(IReadOnlySet<string> ownedNamespaces) {
    var patterns = new List<string> {
      // All services receive system commands
      $"{SYSTEM_COMMAND_NAMESPACE}.#",
      // …and the directed integrity control plane (manifest / redelivery / drill-down traffic).
      $"{CONTROL_PLANE_NAMESPACE}.#"
    };

    // Add service-specific command namespace patterns
    foreach (var ns in ownedNamespaces) {
      if (ns.EndsWith(".*", StringComparison.Ordinal)) {
        // Wildcard: "myapp.users.*" → "myapp.users.#"
        patterns.Add(ns.Replace(".*", ".#"));
      } else {
        // Exact namespace: "myapp.users.commands" → "myapp.users.commands.#"
        patterns.Add($"{ns}.#");
      }
    }

    return patterns;
  }
}
