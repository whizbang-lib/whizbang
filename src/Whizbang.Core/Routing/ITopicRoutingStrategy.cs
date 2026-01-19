namespace Whizbang.Core.Routing;

/// <summary>
/// Strategy for customizing how event/command types are mapped to transport topics.
/// Enables multi-tenant, multi-region, test isolation, and other routing scenarios.
/// </summary>
public interface ITopicRoutingStrategy {
  /// <summary>
  /// Resolves the final topic name for a message type.
  /// </summary>
  /// <param name="messageType">The event or command type being routed</param>
  /// <param name="baseTopic">The base topic name (from convention or configuration)</param>
  /// <param name="context">Additional routing context (tenant ID, region, etc.)</param>
  /// <returns>The final topic name to use</returns>
  string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null);
}
