namespace Whizbang.Core.Transports;

/// <summary>
/// Supported transport types for remote messaging.
/// </summary>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveKafkaValueAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveServiceBusValueAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveRabbitMQValueAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveEventStoreValueAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveInProcessValueAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreDifferentTypesAsync</tests>
public enum TransportType {
  /// <summary>
  /// Apache Kafka / Azure Event Hubs transport
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveKafkaValueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithKafkaConsumerGroup_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithKafkaPartition_ShouldIncludeInMetadataAsync</tests>
  Kafka = 0,

  /// <summary>
  /// Azure Service Bus transport
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveServiceBusValueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithServiceBusSubscriptionName_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithServiceBusSqlFilter_ShouldIncludeInMetadataAsync</tests>
  ServiceBus = 1,

  /// <summary>
  /// RabbitMQ transport
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveRabbitMQValueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithRabbitMQQueueName_ShouldIncludeInMetadataAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerSubscriptionTests.cs:SubscribeFromTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync</tests>
  RabbitMQ = 2,

  /// <summary>
  /// Event Store transport
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveEventStoreValueAsync</tests>
  EventStore = 3,

  /// <summary>
  /// In-process transport (for testing and local communication)
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyConfigurationTransportTests.cs:TransportType_ShouldHaveInProcessValueAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:AddTransport_ShouldStoreTransportAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportManagerTests.cs:HasTransport_WhenExists_ShouldReturnTrueAsync</tests>
  InProcess = 4
}
