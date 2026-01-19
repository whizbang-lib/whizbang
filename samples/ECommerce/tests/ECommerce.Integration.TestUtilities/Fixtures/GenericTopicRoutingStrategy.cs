using Whizbang.Core.Routing;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Routes all events to a pool of generic topics for Azure Service Bus Emulator compatibility.
/// The emulator has limitations with certain topic names - generic topics (topic-00, topic-01) work reliably
/// while named topics (products, inventory) have message delivery issues to certain subscriptions.
/// </summary>
public sealed class GenericTopicRoutingStrategy : ITopicRoutingStrategy {
  private readonly int _topicCount;

  /// <summary>
  /// Creates a new generic topic routing strategy.
  /// </summary>
  /// <param name="topicCount">Number of generic topics in the pool (default: 2)</param>
  public GenericTopicRoutingStrategy(int topicCount = 2) {
    if (topicCount <= 0) {
      throw new ArgumentException("Topic count must be positive", nameof(topicCount));
    }
    _topicCount = topicCount;
  }

  /// <summary>
  /// Routes all events to generic topics by hashing the message type name.
  /// This distributes load across the available generic topics.
  /// </summary>
  /// <param name="messageType">The event or command type being routed</param>
  /// <param name="baseTopic">The base topic name (ignored - all events use generic topics)</param>
  /// <param name="context">Additional routing context (ignored)</param>
  /// <returns>Generic topic name (e.g., "topic-00", "topic-01")</returns>
  public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
    // Hash message type name to distribute events across generic topics
    var hash = Math.Abs(messageType.Name.GetHashCode());
    var topicIndex = hash % _topicCount;
    return $"topic-{topicIndex:D2}";  // topic-00, topic-01, etc.
  }
}
