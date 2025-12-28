namespace Whizbang.Core.Routing;

/// <summary>
/// Reads topic mappings from configuration (appsettings.json or DI).
/// Useful when you can't modify event types (3rd party libraries).
/// </summary>
/// <example>
/// <code>
/// services.AddSingleton&lt;ITopicRegistry&gt;(
///   new ConfigurationTopicRegistry(new Dictionary&lt;Type, string&gt; {
///     [typeof(ProductCreatedEvent)] = "products",
///     [typeof(InventoryRestockedEvent)] = "inventory"
///   })
/// );
/// </code>
/// </example>
public sealed class ConfigurationTopicRegistry : ITopicRegistry {
  private readonly IReadOnlyDictionary<Type, string> _mappings;

  /// <summary>
  /// Creates a new configuration-based topic registry.
  /// </summary>
  /// <param name="mappings">Dictionary mapping message types to base topic names</param>
  /// <exception cref="ArgumentNullException">Thrown if mappings is null</exception>
  public ConfigurationTopicRegistry(IReadOnlyDictionary<Type, string> mappings) {
    ArgumentNullException.ThrowIfNull(mappings);
    _mappings = mappings;
  }

  /// <summary>
  /// Gets the base topic name for a message type from the configured mappings.
  /// </summary>
  /// <param name="messageType">The event or command type</param>
  /// <returns>The base topic name, or null if not configured</returns>
  public string? GetBaseTopic(Type messageType) {
    return _mappings.TryGetValue(messageType, out var topic) ? topic : null;
  }
}
