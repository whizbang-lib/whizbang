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

  #region Connection Retry Options

  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying indefinitely but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// Default: 5
  /// </summary>
  /// <docs>components/transports/rabbitmq#connection-retry</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// Default: 1 second
  /// </summary>
  /// <docs>components/transports/rabbitmq#connection-retry</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// Default: 120 seconds
  /// </summary>
  /// <docs>components/transports/rabbitmq#connection-retry</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at MaxRetryDelay).
  /// Default: 2.0
  /// </summary>
  /// <docs>components/transports/rabbitmq#connection-retry</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until connection succeeds or cancellation is requested.
  /// If false, throw after InitialRetryAttempts.
  /// Default: true (critical transport - always retry)
  /// </summary>
  /// <docs>components/transports/rabbitmq#connection-retry</docs>
  public bool RetryIndefinitely { get; set; } = true;

  #endregion
}
