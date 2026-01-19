namespace Whizbang.Core.Routing;

/// <summary>
/// Chains multiple routing strategies together.
/// Example: BaseTopic → TenantPrefix → PoolSuffix → "tenant-A-products-01"
/// </summary>
public sealed class CompositeTopicRoutingStrategy : ITopicRoutingStrategy {
  private readonly IReadOnlyList<ITopicRoutingStrategy> _strategies;

  /// <summary>
  /// Creates a new composite routing strategy.
  /// </summary>
  /// <param name="strategies">The strategies to chain together (applied in order)</param>
  /// <exception cref="ArgumentNullException">Thrown if strategies is null</exception>
  /// <exception cref="ArgumentException">Thrown if strategies is empty</exception>
  public CompositeTopicRoutingStrategy(IReadOnlyList<ITopicRoutingStrategy> strategies) {
    ArgumentNullException.ThrowIfNull(strategies);
    if (strategies.Count == 0) {
      throw new ArgumentException("At least one strategy must be provided", nameof(strategies));
    }
    _strategies = strategies;
  }

  /// <summary>
  /// Creates a new composite routing strategy from params array.
  /// </summary>
  /// <param name="strategies">The strategies to chain together (applied in order)</param>
  public CompositeTopicRoutingStrategy(params ITopicRoutingStrategy[] strategies)
    : this((IReadOnlyList<ITopicRoutingStrategy>)strategies) {
  }

  /// <summary>
  /// Applies all strategies in sequence.
  /// </summary>
  /// <param name="messageType">The event or command type being routed</param>
  /// <param name="baseTopic">The base topic name</param>
  /// <param name="context">Additional routing context</param>
  /// <returns>The topic name after all transformations</returns>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    var topic = baseTopic;
    foreach (var strategy in _strategies) {
      topic = strategy.ResolveTopic(messageType, topic, context);
    }
    return topic;
  }
}
