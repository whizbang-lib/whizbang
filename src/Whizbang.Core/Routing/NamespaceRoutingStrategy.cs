#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

namespace Whizbang.Core.Routing;

/// <summary>
/// Routes messages to topics based on their full namespace.
/// Returns the complete namespace as the topic (e.g., "myapp.users.commands").
/// </summary>
/// <remarks>
/// <para>
/// Default behavior returns the full namespace in lowercase:
/// - MyApp.Users.Commands.CreateTenantCommand → "myapp.users.commands"
/// - MyApp.Orders.Events.OrderCreatedEvent → "myapp.orders.events"
/// </para>
/// <para>
/// This enables namespace-based message routing where:
/// - Commands go to shared "inbox" topic with namespace-based routing keys
/// - Events go to namespace-specific topics for pub/sub
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#namespace-routing</docs>
/// <remarks>
/// Creates a namespace routing strategy with custom extraction logic.
/// </remarks>
/// <param name="typeToTopic">Function that extracts topic from message type.
/// Receives the full Type, allowing access to namespace, name, and attributes.</param>
/// <exception cref="ArgumentNullException">Thrown when typeToTopic is null.</exception>
public sealed class NamespaceRoutingStrategy(Func<Type, string> typeToTopic) : ITopicRoutingStrategy {
  private readonly Func<Type, string> _typeToTopic = typeToTopic ?? throw new ArgumentNullException(nameof(typeToTopic));

  /// <summary>
  /// Creates a namespace routing strategy with default extraction.
  /// </summary>
  /// <remarks>
  /// Default: Returns the full namespace in lowercase (e.g., MyApp.Users.Commands → "myapp.users.commands").
  /// </remarks>
  public NamespaceRoutingStrategy()
      : this(DefaultTypeToTopic) { }

  /// <summary>
  /// Resolves the topic for a message type based on its namespace.
  /// </summary>
  /// <param name="messageType">The event or command type being routed.</param>
  /// <param name="baseTopic">The base topic name (ignored by this strategy).</param>
  /// <param name="context">Additional routing context (optional).</param>
  /// <returns>The full namespace in lowercase.</returns>
  /// <exception cref="InvalidOperationException">Thrown when the type has no namespace.</exception>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    return _typeToTopic(messageType);
  }

  /// <summary>
  /// Default extraction logic - returns the full namespace in lowercase.
  /// </summary>
  /// <param name="type">The message type.</param>
  /// <returns>The full namespace in lowercase (e.g., "myapp.users.commands").</returns>
  /// <exception cref="InvalidOperationException">Thrown when the type has no namespace.</exception>
  public static string DefaultTypeToTopic(Type type) {
    return type.Namespace?.ToLowerInvariant()
           ?? throw new InvalidOperationException($"Type {type.Name} has no namespace");
  }
}
