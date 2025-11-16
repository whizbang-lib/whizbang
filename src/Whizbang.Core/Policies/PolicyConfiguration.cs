using Whizbang.Core.Transports;

namespace Whizbang.Core.Policies;

/// <summary>
/// Configuration for message processing determined by policy matching.
/// Contains routing, execution strategy, and resource configuration.
/// Includes transport publishing and subscription targets.
/// </summary>
public class PolicyConfiguration {
  /// <summary>
  /// Publishing targets (outbound) - where messages are published when created locally
  /// </summary>
  public List<PublishTarget> PublishTargets { get; } = new();

  /// <summary>
  /// Subscription targets (inbound) - where to subscribe for messages this service can handle
  /// </summary>
  public List<SubscriptionTarget> SubscriptionTargets { get; } = new();
  /// <summary>
  /// Topic to route the message to
  /// </summary>
  public string? Topic { get; private set; }

  /// <summary>
  /// Stream key for ordering and partitioning
  /// </summary>
  public string? StreamKey { get; private set; }

  /// <summary>
  /// Type of execution strategy to use (e.g., SerialExecutor, ParallelExecutor)
  /// </summary>
  public Type? ExecutionStrategyType { get; private set; }

  /// <summary>
  /// Type of partition router to use (e.g., HashPartitionRouter)
  /// </summary>
  public Type? PartitionRouterType { get; private set; }

  /// <summary>
  /// Type of sequence provider to use (e.g., InMemorySequenceProvider)
  /// </summary>
  public Type? SequenceProviderType { get; private set; }

  /// <summary>
  /// Number of partitions for this stream
  /// </summary>
  public int? PartitionCount { get; private set; }

  /// <summary>
  /// Maximum concurrency for execution
  /// </summary>
  public int? MaxConcurrency { get; private set; }

  /// <summary>
  /// Maximum allowed size for data JSONB column (bytes). Default: 7000 (7KB TOAST externalization threshold).
  /// Used by JsonbSizeValidator to check event_data and model_data sizes.
  /// </summary>
  public int? MaxDataSizeBytes { get; private set; }

  /// <summary>
  /// Whether to suppress size warnings for this message/perspective type.
  /// When true, size validation is skipped entirely.
  /// </summary>
  public bool SuppressSizeWarnings { get; private set; }

  /// <summary>
  /// Whether to throw exception if size exceeds threshold.
  /// When true, persistence will fail if MaxDataSizeBytes is exceeded.
  /// </summary>
  public bool ThrowOnSizeExceeded { get; private set; }

  /// <summary>
  /// Sets the topic for message routing
  /// </summary>
  public PolicyConfiguration UseTopic(string topic) {
    Topic = topic;
    return this;
  }

  /// <summary>
  /// Sets the stream key for ordering and partitioning
  /// </summary>
  public PolicyConfiguration UseStreamKey(string streamKey) {
    StreamKey = streamKey;
    return this;
  }

  /// <summary>
  /// Sets the execution strategy type
  /// </summary>
  public PolicyConfiguration UseExecutionStrategy<TStrategy>() {
    ExecutionStrategyType = typeof(TStrategy);
    return this;
  }

  /// <summary>
  /// Sets the partition router type
  /// </summary>
  public PolicyConfiguration UsePartitionRouter<TRouter>() {
    PartitionRouterType = typeof(TRouter);
    return this;
  }

  /// <summary>
  /// Sets the sequence provider type
  /// </summary>
  public PolicyConfiguration UseSequenceProvider<TProvider>() {
    SequenceProviderType = typeof(TProvider);
    return this;
  }

  /// <summary>
  /// Sets the number of partitions
  /// </summary>
  public PolicyConfiguration WithPartitions(int count) {
    if (count <= 0) {
      throw new ArgumentOutOfRangeException(nameof(count), "Partition count must be greater than zero");
    }
    PartitionCount = count;
    return this;
  }

  /// <summary>
  /// Sets the maximum concurrency
  /// </summary>
  public PolicyConfiguration WithConcurrency(int maxConcurrency) {
    if (maxConcurrency <= 0) {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be greater than zero");
    }
    MaxConcurrency = maxConcurrency;
    return this;
  }

  /// <summary>
  /// Configures persistence size limits for JSONB columns.
  /// Size is calculated in C# and validated before persistence.
  /// Threshold violations are logged and optionally added to metadata.
  /// </summary>
  /// <param name="maxDataSizeBytes">Maximum size for data column (default: 7000 bytes = 7KB TOAST externalization threshold)</param>
  /// <param name="suppressWarnings">Whether to suppress size validation warnings</param>
  /// <param name="throwOnExceeded">Whether to throw exception if threshold exceeded</param>
  /// <returns>This policy configuration for method chaining</returns>
  public PolicyConfiguration WithPersistenceSize(
    int maxDataSizeBytes = 7000,
    bool suppressWarnings = false,
    bool throwOnExceeded = false
  ) {
    if (maxDataSizeBytes <= 0) {
      throw new ArgumentOutOfRangeException(nameof(maxDataSizeBytes), "Max data size must be greater than zero");
    }
    MaxDataSizeBytes = maxDataSizeBytes;
    SuppressSizeWarnings = suppressWarnings;
    ThrowOnSizeExceeded = throwOnExceeded;
    return this;
  }

  // ========================================
  // PUBLISHING (Outbound)
  // ========================================

  /// <summary>
  /// Publish to Kafka topic
  /// </summary>
  public PolicyConfiguration PublishToKafka(string topic) {
    PublishTargets.Add(new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = topic
    });
    return this;
  }

  /// <summary>
  /// Publish to Azure Service Bus topic
  /// </summary>
  public PolicyConfiguration PublishToServiceBus(string topic) {
    PublishTargets.Add(new PublishTarget {
      TransportType = TransportType.ServiceBus,
      Destination = topic
    });
    return this;
  }

  /// <summary>
  /// Publish to RabbitMQ exchange with routing key
  /// </summary>
  public PolicyConfiguration PublishToRabbitMQ(string exchange, string routingKey) {
    PublishTargets.Add(new PublishTarget {
      TransportType = TransportType.RabbitMQ,
      Destination = exchange,
      RoutingKey = routingKey
    });
    return this;
  }

  // ========================================
  // SUBSCRIBING (Inbound)
  // ========================================

  /// <summary>
  /// Subscribe from Kafka topic with consumer group
  /// </summary>
  public PolicyConfiguration SubscribeFromKafka(
    string topic,
    string consumerGroup,
    int? partition = null
  ) {
    SubscriptionTargets.Add(new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = topic,
      ConsumerGroup = consumerGroup,
      Partition = partition
    });
    return this;
  }

  /// <summary>
  /// Subscribe from Azure Service Bus topic with subscription name
  /// </summary>
  public PolicyConfiguration SubscribeFromServiceBus(
    string topic,
    string subscriptionName,
    string? sqlFilter = null
  ) {
    SubscriptionTargets.Add(new SubscriptionTarget {
      TransportType = TransportType.ServiceBus,
      Topic = topic,
      SubscriptionName = subscriptionName,
      SqlFilter = sqlFilter
    });
    return this;
  }

  /// <summary>
  /// Subscribe from RabbitMQ exchange with queue and optional routing key
  /// </summary>
  public PolicyConfiguration SubscribeFromRabbitMQ(
    string exchange,
    string queueName,
    string? routingKey = null
  ) {
    SubscriptionTargets.Add(new SubscriptionTarget {
      TransportType = TransportType.RabbitMQ,
      Topic = exchange,
      QueueName = queueName,
      RoutingKey = routingKey
    });
    return this;
  }
}
