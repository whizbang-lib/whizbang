namespace Whizbang.Core.Routing;

/// <summary>
/// Appends a pool suffix to topics for test isolation or load distribution.
/// Example: "products" → "products-01"
/// </summary>
public sealed class PoolSuffixRoutingStrategy : ITopicRoutingStrategy {
  private readonly string _poolSuffix;

  /// <summary>
  /// Creates a new pool suffix routing strategy.
  /// </summary>
  /// <param name="poolSuffix">The suffix to append to topic names (e.g., "01", "02")</param>
  /// <exception cref="ArgumentException">Thrown if poolSuffix is null or whitespace</exception>
  public PoolSuffixRoutingStrategy(string poolSuffix) {
    ArgumentException.ThrowIfNullOrWhiteSpace(poolSuffix);
    _poolSuffix = poolSuffix;
  }

  /// <summary>
  /// Appends the pool suffix to the base topic.
  /// </summary>
  /// <param name="messageType">The event or command type being routed</param>
  /// <param name="baseTopic">The base topic name</param>
  /// <param name="context">Additional routing context (ignored)</param>
  /// <returns>The topic name with pool suffix appended (e.g., "products-01")</returns>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    return $"{baseTopic}-{_poolSuffix}";
  }
}
