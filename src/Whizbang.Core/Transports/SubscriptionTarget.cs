namespace Whizbang.Core.Transports;

/// <summary>
/// Defines where a service should subscribe to receive messages.
/// Used by PolicyConfiguration to specify subscription sources.
/// </summary>
public record SubscriptionTarget {
  /// <summary>
  /// Type of transport to subscribe from
  /// </summary>
  public required TransportType TransportType { get; init; }

  /// <summary>
  /// Topic/Exchange to subscribe to
  /// </summary>
  public required string Topic { get; init; }

  /// <summary>
  /// Kafka consumer group for load balancing
  /// </summary>
  public string? ConsumerGroup { get; init; }

  /// <summary>
  /// Azure Service Bus subscription name
  /// </summary>
  public string? SubscriptionName { get; init; }

  /// <summary>
  /// RabbitMQ queue name
  /// </summary>
  public string? QueueName { get; init; }

  /// <summary>
  /// RabbitMQ routing key for binding
  /// </summary>
  public string? RoutingKey { get; init; }

  /// <summary>
  /// Azure Service Bus SQL filter expression
  /// </summary>
  public string? SqlFilter { get; init; }

  /// <summary>
  /// Kafka partition (optional, for specific partition subscription)
  /// </summary>
  public int? Partition { get; init; }
}
