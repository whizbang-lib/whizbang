namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Configuration options for RabbitMQ transport.
/// </summary>
/// <docs>components/transports/rabbitmq</docs>
public class RabbitMQOptions {
  /// <summary>
  /// Maximum number of channels in the pool.
  /// RabbitMQ channels are NOT thread-safe, so pooling is required.
  /// Default: 10
  /// </summary>
  public int MaxChannels { get; set; } = 10;

  /// <summary>
  /// Maximum number of delivery attempts before dead-lettering.
  /// Default: 10
  /// </summary>
  public int MaxDeliveryAttempts { get; set; } = 10;

  /// <summary>
  /// Fallback queue name when no queue is specified.
  /// </summary>
  public string? DefaultQueueName { get; set; }

  /// <summary>
  /// QoS prefetch count for consumers.
  /// Default: 10
  /// </summary>
  public ushort PrefetchCount { get; set; } = 10;

  /// <summary>
  /// Whether to automatically declare dead-letter exchange and queue.
  /// Default: true
  /// </summary>
  public bool AutoDeclareDeadLetterExchange { get; set; } = true;
}
