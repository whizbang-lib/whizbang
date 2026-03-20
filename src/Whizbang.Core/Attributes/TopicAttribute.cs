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
public sealed class TopicAttribute(string topicName) : Attribute {
  /// <summary>
  /// Gets the base topic name for this message type.
  /// </summary>
#pragma warning disable S3604 // Validation in primary constructor initializer is intentional
  public string TopicName { get; } = topicName switch {
    null => throw new ArgumentNullException(nameof(topicName)),
    _ when string.IsNullOrWhiteSpace(topicName) => throw new ArgumentException("Topic name cannot be empty or whitespace.", nameof(topicName)),
    _ => topicName
  };
#pragma warning restore S3604
}
