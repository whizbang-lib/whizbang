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

  public TopicAttribute(string topicName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
    TopicName = topicName;
  }
}
