namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the base topic name for an event or command type.
/// Discovered by TopicRegistryGenerator for AOT-safe lookup.
/// </summary>
/// <example>
/// <code>
/// [Topic("products")]
/// public sealed record ProductCreatedEvent(...) : IEvent;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TopicAttribute : Attribute {
  /// <summary>
  /// Gets the base topic name for this message type.
  /// </summary>
  public string TopicName { get; }

  /// <summary>
  /// Creates a new topic attribute.
  /// </summary>
  /// <param name="topicName">The base topic name (e.g., "products", "inventory")</param>
  /// <exception cref="ArgumentException">Thrown if topicName is null or whitespace</exception>
  public TopicAttribute(string topicName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
    TopicName = topicName;
  }
}
