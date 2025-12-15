using Whizbang.Core.Transports;

namespace Whizbang.Core.Policies;

/// <summary>
/// Configuration for message processing determined by policy matching.
/// Contains routing, execution strategy, and resource configuration.
/// Includes transport publishing and subscription targets.
/// </summary>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:PolicyConfiguration_ShouldSupportMethodChainingAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:PolicyConfiguration_ShouldSupportComplexChainingAsync</tests>
public class PolicyConfiguration {
  /// <summary>
  /// Publishing targets (outbound) - where messages are published when created locally
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_PublishToKafka_ShouldAddPublishTargetAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_PublishToMultipleTransports_ShouldAddAllTargetsAsync</tests>
  public List<PublishTarget> PublishTargets { get; } = [];

  /// <summary>
  /// Subscription targets (inbound) - where to subscribe for messages this service can handle
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromKafka_ShouldAddSubscriptionTargetAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromMultipleSources_ShouldAddAllTargetsAsync</tests>
  public List<SubscriptionTarget> SubscriptionTargets { get; } = [];
  /// <summary>
  /// Topic to route the message to
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseTopic_ShouldSetTopicAsync</tests>
  public string? Topic { get; private set; }

  /// <summary>
  /// Stream key for ordering and partitioning
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseStreamKey_ShouldSetStreamKeyAsync</tests>
  public string? StreamKey { get; private set; }

  /// <summary>
  /// Type of execution strategy to use (e.g., SerialExecutor, ParallelExecutor)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseExecutionStrategy_ShouldSetExecutionStrategyTypeAsync</tests>
  public Type? ExecutionStrategyType { get; private set; }

  /// <summary>
  /// Type of partition router to use (e.g., HashPartitionRouter)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UsePartitionRouter_ShouldSetPartitionRouterTypeAsync</tests>
  public Type? PartitionRouterType { get; private set; }

  /// <summary>
  /// Type of sequence provider to use (e.g., InMemorySequenceProvider)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseSequenceProvider_ShouldSetSequenceProviderTypeAsync</tests>
  public Type? SequenceProviderType { get; private set; }

  /// <summary>
  /// Number of partitions for this stream
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithPartitions_ShouldSetPartitionCountAsync</tests>
  public int? PartitionCount { get; private set; }

  /// <summary>
  /// Maximum concurrency for execution
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithConcurrency_ShouldSetMaxConcurrencyAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseTopic_ShouldSetTopicAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseTopic_ShouldReturnSelfForFluentAPIAsync</tests>
  public PolicyConfiguration UseTopic(string topic) {
    Topic = topic;
    return this;
  }

  /// <summary>
  /// Sets the stream key for ordering and partitioning
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseStreamKey_ShouldSetStreamKeyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseStreamKey_ShouldReturnSelfForFluentAPIAsync</tests>
  public PolicyConfiguration UseStreamKey(string streamKey) {
    StreamKey = streamKey;
    return this;
  }

  /// <summary>
  /// Sets the execution strategy type
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseExecutionStrategy_ShouldSetExecutionStrategyTypeAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseExecutionStrategy_ShouldReturnSelfForFluentAPIAsync</tests>
  public PolicyConfiguration UseExecutionStrategy<TStrategy>() {
    ExecutionStrategyType = typeof(TStrategy);
    return this;
  }

  /// <summary>
  /// Sets the partition router type
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UsePartitionRouter_ShouldSetPartitionRouterTypeAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UsePartitionRouter_ShouldReturnSelfForFluentAPIAsync</tests>
  public PolicyConfiguration UsePartitionRouter<TRouter>() {
    PartitionRouterType = typeof(TRouter);
    return this;
  }

  /// <summary>
  /// Sets the sequence provider type
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseSequenceProvider_ShouldSetSequenceProviderTypeAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:UseSequenceProvider_ShouldReturnSelfForFluentAPIAsync</tests>
  public PolicyConfiguration UseSequenceProvider<TProvider>() {
    SequenceProviderType = typeof(TProvider);
    return this;
  }

  /// <summary>
  /// Sets the number of partitions
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithPartitions_ShouldSetPartitionCountAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithPartitions_ShouldReturnSelfForFluentAPIAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithPartitions_WithZero_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithPartitions_WithNegative_ShouldThrowAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithConcurrency_ShouldSetMaxConcurrencyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithConcurrency_ShouldReturnSelfForFluentAPIAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithConcurrency_WithZero_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs:WithConcurrency_WithNegative_ShouldThrowAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_PublishToKafka_ShouldAddPublishTargetAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_PublishToKafka_ShouldReturnSelfForFluentAPIAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_PublishToServiceBus_ShouldAddPublishTargetAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_PublishToRabbitMQ_ShouldAddPublishTargetAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromKafka_ShouldAddSubscriptionTargetAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromKafka_WithPartition_ShouldStorePartitionAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromKafka_ShouldReturnSelfForFluentAPIAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromServiceBus_ShouldAddSubscriptionTargetAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromServiceBus_WithFilter_ShouldStoreSqlFilterAsync</tests>
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromRabbitMQ_ShouldAddSubscriptionTargetAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:PolicyConfiguration_SubscribeFromRabbitMQ_WithRoutingKey_ShouldStoreRoutingKeyAsync</tests>
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
