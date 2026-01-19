namespace Whizbang.Core.Routing;

/// <summary>
/// Provides base topic names for event/command types (convention or configuration).
/// This is what gets source-generated.
/// </summary>
public interface ITopicRegistry {
  /// <summary>
  /// Gets the base topic name for a message type, or null if not configured.
  /// </summary>
  /// <param name="messageType">The event or command type</param>
  /// <returns>The base topic name, or null if not configured</returns>
  string? GetBaseTopic(Type messageType);
}
