namespace Whizbang.Core.Routing;

/// <summary>
/// Routes messages to topics based on namespace patterns.
/// Supports both hierarchical and flat namespace structures.
/// </summary>
/// <remarks>
/// <para>
/// Default extraction logic:
/// 1. For hierarchical namespaces (MyApp.Orders.Events), uses second-to-last segment ("orders")
/// 2. For flat namespaces (MyApp.Contracts.Commands), extracts domain from type name ("CreateOrder" → "order")
/// 3. Skips generic suffixes like "contracts", "commands", "events", "queries", "messages"
/// </para>
/// </remarks>
/// <docs>core-concepts/routing#namespace-routing</docs>
public sealed class NamespaceRoutingStrategy : ITopicRoutingStrategy {
  private static readonly HashSet<string> _genericSegments = new(StringComparer.OrdinalIgnoreCase) {
    "contracts",
    "commands",
    "events",
    "queries",
    "messages"
  };

  // Suffixes ordered by priority - longer/compound suffixes first
  private static readonly string[] _typeSuffixes = [
    "CreatedEvent",
    "UpdatedEvent",
    "DeletedEvent",
    "Command",
    "Event",
    "Query",
    "Message",
    "Handler",
    "Receptor",
    "Created",
    "Updated",
    "Deleted",
    "ById"
  ];

  private static readonly string[] _typePrefixes = [
    "Create",
    "Update",
    "Delete",
    "Get",
    "Set"
  ];

  private readonly Func<Type, string> _typeToTopic;

  /// <summary>
  /// Creates a namespace routing strategy with default extraction.
  /// </summary>
  /// <remarks>
  /// Default: Uses second-to-last namespace segment (e.g., MyApp.Orders.Events → "orders").
  /// Falls back to extracting domain from type name for flat namespace structures.
  /// </remarks>
  public NamespaceRoutingStrategy()
      : this(DefaultTypeToTopic) { }

  /// <summary>
  /// Creates a namespace routing strategy with custom extraction logic.
  /// </summary>
  /// <param name="typeToTopic">Function that extracts topic from message type.
  /// Receives the full Type, allowing access to namespace, name, and attributes.</param>
  /// <exception cref="ArgumentNullException">Thrown when typeToTopic is null.</exception>
  public NamespaceRoutingStrategy(Func<Type, string> typeToTopic) {
    _typeToTopic = typeToTopic ?? throw new ArgumentNullException(nameof(typeToTopic));
  }

  /// <summary>
  /// Resolves the topic for a message type based on its namespace.
  /// </summary>
  /// <param name="messageType">The event or command type being routed.</param>
  /// <param name="baseTopic">The base topic name (ignored by this strategy).</param>
  /// <param name="context">Additional routing context (optional).</param>
  /// <returns>The topic name extracted from the namespace or type name.</returns>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    return _typeToTopic(messageType);
  }

  /// <summary>
  /// Default extraction logic for namespace-based topic routing.
  /// </summary>
  /// <param name="type">The message type.</param>
  /// <returns>The extracted topic name in lowercase.</returns>
  public static string DefaultTypeToTopic(Type type) {
    var ns = type.Namespace ?? "";
    var parts = ns.Split('.');

    // MyApp.Orders.Events.OrderCreated → "orders"
    // MyApp.Contracts.Commands.CreateOrder → extract from type name
    if (parts.Length >= 2) {
      var candidate = parts[^2].ToLowerInvariant();
      // Skip generic suffixes like "contracts", "commands", "events", "queries"
      if (!_genericSegments.Contains(candidate)) {
        return candidate;
      }
    }

    // Fallback: extract domain from type name (CreateOrderCommand → "order")
    return ExtractDomainFromTypeName(type.Name);
  }

  /// <summary>
  /// Extracts the domain name from a type name by removing common prefixes and suffixes.
  /// </summary>
  /// <param name="typeName">The type name (e.g., "CreateOrderCommand").</param>
  /// <returns>The domain name in lowercase (e.g., "order").</returns>
  public static string ExtractDomainFromTypeName(string typeName) {
    var result = typeName;

    // Remove common suffixes: Command, Event, Query, Message, Handler, Receptor
    foreach (var suffix in _typeSuffixes) {
      if (result.EndsWith(suffix, StringComparison.Ordinal)) {
        result = result[..^suffix.Length];
        break;
      }
    }

    // Remove common prefixes: Create, Update, Delete, Get, Set
    foreach (var prefix in _typePrefixes) {
      if (result.StartsWith(prefix, StringComparison.Ordinal)) {
        result = result[prefix.Length..];
        break;
      }
    }

    return result.ToLowerInvariant();
  }
}
