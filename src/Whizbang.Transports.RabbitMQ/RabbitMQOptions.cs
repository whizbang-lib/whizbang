namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Configuration options for RabbitMQ transport.
/// </summary>
/// <docs>messaging/transports/rabbitmq</docs>
public class RabbitMQOptions {
  /// <summary>
  /// Maximum number of RabbitMQ channels in the connection pool.
  /// Channels are lightweight multiplexed connections over a single TCP connection.
  /// RabbitMQ channels are <b>not</b> thread-safe, so each concurrent publish operation
  /// needs its own channel. The pool recycles channels to avoid creating/destroying them per message.
  /// <para>
  /// <b>Example:</b> With <c>MaxChannels = 10</c>, up to 10 messages can be published concurrently.
  /// An 11th concurrent publish waits until a channel is returned to the pool.
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="PrefetchCount"/> — how many messages a <em>consumer</em> buffers locally (receiving, not sending)</item>
  /// </list>
  /// </para>
  /// Default: 10
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#channels</docs>
  public int MaxChannels { get; set; } = 10;

  /// <summary>
  /// How many times a single failing message is redelivered before being moved to the dead-letter queue.
  /// Each time a message handler throws an exception, the message is NACKed and requeued.
  /// A <c>x-delivery-count</c> header tracks how many times the message has been delivered.
  /// Once this limit is reached, the message is NACKed without requeue (sent to the dead-letter exchange).
  /// <para>
  /// <b>Example:</b> With <c>MaxDeliveryAttempts = 10</c>, a message that always fails is retried
  /// 10 times total. On the 10th failure, it moves to the dead-letter exchange/queue
  /// where it can be inspected or reprocessed manually.
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="PrefetchCount"/> — how many messages the client <em>buffers locally</em> for faster processing (throughput optimization)</item>
  ///   <item><see cref="InitialRetryAttempts"/> — how many times the <em>transport connection</em> is retried on startup (not per-message)</item>
  /// </list>
  /// </para>
  /// Default: 10
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#dead-lettering</docs>
  public int MaxDeliveryAttempts { get; set; } = 10;

  /// <summary>
  /// Fallback queue name when no queue is specified.
  /// </summary>
  public string? DefaultQueueName { get; set; }

  /// <summary>
  /// How many messages the client pre-fetches from the broker into a local buffer.
  /// This reduces latency by having messages ready to process immediately when the previous one completes,
  /// instead of waiting for a round-trip to the broker to fetch the next message.
  /// The broker delivers up to this many messages proactively — it does <b>not</b> wait for the full
  /// amount. If only 3 messages are available and PrefetchCount is 100, you get 3 immediately.
  /// <para>
  /// <b>Example:</b> With <c>PrefetchCount = 50</c>, after the consumer connects, the broker pushes
  /// up to 50 messages into the client's local buffer. As messages are acknowledged, the broker
  /// sends more to keep the buffer topped up (sliding window). This eliminates the per-message
  /// fetch round-trip latency.
  /// </para>
  /// <para>
  /// <b>Trade-off:</b> Higher values improve throughput but increase memory usage and can cause
  /// uneven load distribution across multiple consumers (one consumer buffers many messages while
  /// others sit idle).
  /// </para>
  /// <para>
  /// <b>Not to be confused with:</b>
  /// <list type="bullet">
  ///   <item><see cref="MaxDeliveryAttempts"/> — how many times a <em>single failing message</em> is retried before dead-lettering (per-message retry, not buffering)</item>
  ///   <item><see cref="MaxChannels"/> — how many <em>RabbitMQ channels</em> are pooled for publishing (connection pooling, not message buffering)</item>
  /// </list>
  /// </para>
  /// Default: 200. The transport consumer uses batch receive (SubscribeBatchAsync) so higher
  /// values are safe and dramatically improve throughput. Match this to
  /// <see cref="TransportBatchOptions.BatchSize"/> for optimal batching.
  /// </summary>
  /// <docs>messaging/transports/rabbitmq#prefetch</docs>
  public ushort PrefetchCount { get; set; } = 200;

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
