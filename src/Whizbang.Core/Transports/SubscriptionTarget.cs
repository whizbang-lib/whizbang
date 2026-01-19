namespace Whizbang.Core.Transports;

/// <summary>
/// Defines where a service should subscribe to receive messages.
/// Used by PolicyConfiguration to specify subscription sources.
/// </summary>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreTransportTypeAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreTopicAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreConsumerGroupAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreSubscriptionNameAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreQueueNameAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreRoutingKeyAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreSqlFilterAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStorePartitionAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_Equality_WithSameValues_ShouldBeEqualAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_Equality_WithDifferentValues_ShouldNotBeEqualAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_WithExpression_ShouldCreateNewInstanceAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ToString_ShouldContainPropertyValuesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithSingleTarget_ShouldCreateSubscriptionAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithMultipleTargets_ShouldCreateMultipleSubscriptionsAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithAllMetadata_ShouldIncludeAllInDestinationAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithEmptyStringsInMetadata_ShouldNotIncludeThemAsync</tests>
public record SubscriptionTarget {
  /// <summary>
  /// Type of transport to subscribe from
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreTransportTypeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithSingleTarget_ShouldCreateSubscriptionAsync</tests>
  public required TransportType TransportType { get; init; }

  /// <summary>
  /// Topic/Exchange to subscribe to
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreTopicAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithSingleTarget_ShouldCreateSubscriptionAsync</tests>
  public required string Topic { get; init; }

  /// <summary>
  /// Kafka consumer group for load balancing
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreConsumerGroupAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithKafkaConsumerGroup_ShouldIncludeInMetadataAsync</tests>
  public string? ConsumerGroup { get; init; }

  /// <summary>
  /// Azure Service Bus subscription name
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreSubscriptionNameAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithServiceBusSubscriptionName_ShouldIncludeInMetadataAsync</tests>
  public string? SubscriptionName { get; init; }

  /// <summary>
  /// RabbitMQ queue name
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreQueueNameAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithRabbitMQQueueName_ShouldIncludeInMetadataAsync</tests>
  public string? QueueName { get; init; }

  /// <summary>
  /// RabbitMQ routing key for binding
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreRoutingKeyAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync</tests>
  public string? RoutingKey { get; init; }

  /// <summary>
  /// Azure Service Bus SQL filter expression
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStoreSqlFilterAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithServiceBusSqlFilter_ShouldIncludeInMetadataAsync</tests>
  public string? SqlFilter { get; init; }

  /// <summary>
  /// Kafka partition (optional, for specific partition subscription)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:SubscriptionTarget_ShouldStorePartitionAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithKafkaPartition_ShouldIncludeInMetadataAsync</tests>
  public int? Partition { get; init; }
}
