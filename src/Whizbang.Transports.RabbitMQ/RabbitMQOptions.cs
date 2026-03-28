namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Configuration options for RabbitMQ transport.
/// </summary>
/// <docs>messaging/transports/rabbitmq</docs>
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

  #region FIFO / Single Active Consumer

  /// <summary>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQFifoIntegrationTests.cs:EnableSingleActiveConsumer_DefaultsToFalseAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQFifoIntegrationTests.cs:SAC_Capabilities_IncludesOrderedAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQFifoIntegrationTests.cs:NonSAC_Capabilities_ExcludesOrderedAsync</tests>
  /// If true, queues are declared with x-single-active-consumer = true.
  /// This ensures only one consumer processes messages at a time, guaranteeing FIFO ordering.
  /// RabbitMQ guarantees per-publisher per-channel ordering, so with SAC, ordering is preserved.
  /// Default: false (backward compatible)
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#single-active-consumer</docs>
  public bool EnableSingleActiveConsumer { get; set; }

  #endregion

  #region Connection Retry Options

  /// <summary>
  /// Number of initial retry attempts before switching to indefinite retry mode.
  /// During initial retries, each failure is logged as a warning.
  /// After initial retries, the system continues retrying indefinitely but logs less frequently.
  /// Set to 0 to skip initial warning phase and go directly to indefinite retry.
  /// Default: 5
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#connection-retry</docs>
  public int InitialRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Initial delay before the first retry attempt.
  /// Default: 1 second
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#connection-retry</docs>
  public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum delay between retry attempts (caps the exponential backoff).
  /// Once this delay is reached, retries continue at this interval indefinitely.
  /// Default: 120 seconds
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#connection-retry</docs>
  public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(120);

  /// <summary>
  /// Multiplier for exponential backoff between retries.
  /// Each retry delay = previous delay * multiplier (capped at MaxRetryDelay).
  /// Default: 2.0
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#connection-retry</docs>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// If true, retry indefinitely until connection succeeds or cancellation is requested.
  /// If false, throw after InitialRetryAttempts.
  /// Default: true (critical transport - always retry)
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#connection-retry</docs>
  public bool RetryIndefinitely { get; set; } = true;

  #endregion
}
