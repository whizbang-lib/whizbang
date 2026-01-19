namespace Whizbang.Core.Routing;

/// <summary>
/// Default strategy - returns base topic unchanged.
/// Used when no customization is needed.
/// </summary>
public sealed class PassthroughRoutingStrategy : ITopicRoutingStrategy {
  /// <summary>
  /// Singleton instance of the passthrough strategy.
  /// </summary>
  public static readonly PassthroughRoutingStrategy Instance = new();

  /// <summary>
  /// Returns the base topic unchanged.
  /// </summary>
  /// <param name="messageType">The event or command type being routed</param>
  /// <param name="baseTopic">The base topic name</param>
  /// <param name="context">Additional routing context (ignored)</param>
  /// <returns>The base topic unchanged</returns>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    return baseTopic;
  }
}
