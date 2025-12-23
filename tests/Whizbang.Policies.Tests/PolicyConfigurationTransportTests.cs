using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Policies;
using Whizbang.Core.Transports;

namespace Whizbang.Policies.Tests;

/// <summary>
/// Tests for PolicyConfiguration transport routing features (publishing and subscribing).
/// Following TDD: These tests are written BEFORE implementing transport routing features.
///
/// Phase 1: Transport target records and enums
/// Phase 2: Publishing and subscribing methods on PolicyConfiguration
/// </summary>
public class PolicyConfigurationTransportTests {
  // ========================================
  // PHASE 1: TransportType Enum Tests
  // ========================================

  [Test]
  public async Task TransportType_ShouldHaveKafkaValueAsync() {
    // Arrange & Act
    var transportType = TransportType.Kafka;

    // Assert
    await Assert.That((int)transportType).IsEqualTo(0); // First enum value
  }

  [Test]
  public async Task TransportType_ShouldHaveServiceBusValueAsync() {
    // Arrange & Act
    var transportType = TransportType.ServiceBus;

    // Assert
    await Assert.That(transportType).IsNotEqualTo(TransportType.Kafka);
  }

  [Test]
  public async Task TransportType_ShouldHaveRabbitMQValueAsync() {
    // Arrange & Act
    var transportType = TransportType.RabbitMQ;

    // Assert
    await Assert.That(transportType).IsNotEqualTo(TransportType.Kafka);
    await Assert.That(transportType).IsNotEqualTo(TransportType.ServiceBus);
  }

  [Test]
  public async Task TransportType_ShouldHaveEventStoreValueAsync() {
    // Arrange & Act
    var transportType = TransportType.EventStore;

    // Assert
    await Assert.That(transportType).IsNotEqualTo(TransportType.Kafka);
  }

  [Test]
  public async Task TransportType_ShouldHaveInProcessValueAsync() {
    // Arrange & Act
    var transportType = TransportType.InProcess;

    // Assert
    await Assert.That(transportType).IsNotEqualTo(TransportType.Kafka);
  }

  // ========================================
  // PHASE 1: PublishTarget Record Tests
  // ========================================

  [Test]
  public async Task PublishTarget_ShouldStoreTransportTypeAsync() {
    // Arrange & Act
    var target = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic"
    };

    // Assert
    await Assert.That(target.TransportType).IsEqualTo(TransportType.Kafka);
  }

  [Test]
  public async Task PublishTarget_ShouldStoreDestinationAsync() {
    // Arrange & Act
    var target = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic"
    };

    // Assert
    await Assert.That(target.Destination).IsEqualTo("orders-topic");
  }

  [Test]
  public async Task PublishTarget_ShouldStoreRoutingKeyAsync() {
    // Arrange & Act
    var target = new PublishTarget {
      TransportType = TransportType.RabbitMQ,
      Destination = "orders-exchange",
      RoutingKey = "order.created"
    };

    // Assert
    await Assert.That(target.RoutingKey).IsEqualTo("order.created");
  }

  [Test]
  public async Task PublishTarget_ShouldAllowNullRoutingKeyAsync() {
    // Arrange & Act
    var target = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic",
      RoutingKey = null
    };

    // Assert
    await Assert.That(target.RoutingKey).IsNull();
  }

  [Test]
  public async Task PublishTarget_Equality_WithSameValues_ShouldBeEqualAsync() {
    // Arrange
    var target1 = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic",
      RoutingKey = "order.created"
    };

    var target2 = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic",
      RoutingKey = "order.created"
    };

    // Assert
    await Assert.That(target1).IsEqualTo(target2);
    await Assert.That(target1.GetHashCode()).IsEqualTo(target2.GetHashCode());
  }

  [Test]
  public async Task PublishTarget_Equality_WithDifferentValues_ShouldNotBeEqualAsync() {
    // Arrange
    var target1 = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic"
    };

    var target2 = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "inventory-topic"
    };

    // Assert
    await Assert.That(target1).IsNotEqualTo(target2);
  }

  [Test]
  public async Task PublishTarget_WithExpression_ShouldCreateNewInstanceAsync() {
    // Arrange
    var original = new PublishTarget {
      TransportType = TransportType.Kafka,
      Destination = "orders-topic",
      RoutingKey = "order.created"
    };

    // Act
    var modified = original with { Destination = "inventory-topic" };

    // Assert
    await Assert.That(modified.TransportType).IsEqualTo(TransportType.Kafka);
    await Assert.That(modified.Destination).IsEqualTo("inventory-topic");
    await Assert.That(modified.RoutingKey).IsEqualTo("order.created");
    await Assert.That(modified).IsNotEqualTo(original);
  }

  [Test]
  public async Task PublishTarget_ToString_ShouldContainPropertyValuesAsync() {
    // Arrange
    var target = new PublishTarget {
      TransportType = TransportType.RabbitMQ,
      Destination = "orders-exchange",
      RoutingKey = "order.created"
    };

    // Act
    var result = target.ToString();

    // Assert
    await Assert.That(result).Contains("RabbitMQ");
    await Assert.That(result).Contains("orders-exchange");
    await Assert.That(result).Contains("order.created");
  }

  // ========================================
  // PHASE 1: SubscriptionTarget Record Tests
  // ========================================

  [Test]
  public async Task SubscriptionTarget_ShouldStoreTransportTypeAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service"
    };

    // Assert
    await Assert.That(target.TransportType).IsEqualTo(TransportType.Kafka);
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStoreTopicAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service"
    };

    // Assert
    await Assert.That(target.Topic).IsEqualTo("orders-topic");
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStoreConsumerGroupAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service"
    };

    // Assert
    await Assert.That(target.ConsumerGroup).IsEqualTo("inventory-service");
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStoreSubscriptionNameAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.ServiceBus,
      Topic = "orders-topic",
      SubscriptionName = "inventory-sub"
    };

    // Assert
    await Assert.That(target.SubscriptionName).IsEqualTo("inventory-sub");
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStoreQueueNameAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.RabbitMQ,
      Topic = "orders-exchange",
      QueueName = "inventory-queue"
    };

    // Assert
    await Assert.That(target.QueueName).IsEqualTo("inventory-queue");
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStoreRoutingKeyAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.RabbitMQ,
      Topic = "orders-exchange",
      QueueName = "inventory-queue",
      RoutingKey = "order.created"
    };

    // Assert
    await Assert.That(target.RoutingKey).IsEqualTo("order.created");
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStoreSqlFilterAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.ServiceBus,
      Topic = "orders-topic",
      SubscriptionName = "high-priority-orders",
      SqlFilter = "Priority > 5"
    };

    // Assert
    await Assert.That(target.SqlFilter).IsEqualTo("Priority > 5");
  }

  [Test]
  public async Task SubscriptionTarget_ShouldStorePartitionAsync() {
    // Arrange & Act
    var target = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service",
      Partition = 2
    };

    // Assert
    await Assert.That(target.Partition).IsEqualTo(2);
  }

  [Test]
  public async Task SubscriptionTarget_Equality_WithSameValues_ShouldBeEqualAsync() {
    // Arrange
    var target1 = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service",
      Partition = 2
    };

    var target2 = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service",
      Partition = 2
    };

    // Assert
    await Assert.That(target1).IsEqualTo(target2);
    await Assert.That(target1.GetHashCode()).IsEqualTo(target2.GetHashCode());
  }

  [Test]
  public async Task SubscriptionTarget_Equality_WithDifferentValues_ShouldNotBeEqualAsync() {
    // Arrange
    var target1 = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service"
    };

    var target2 = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "shipping-service"
    };

    // Assert
    await Assert.That(target1).IsNotEqualTo(target2);
  }

  [Test]
  public async Task SubscriptionTarget_WithExpression_ShouldCreateNewInstanceAsync() {
    // Arrange
    var original = new SubscriptionTarget {
      TransportType = TransportType.Kafka,
      Topic = "orders-topic",
      ConsumerGroup = "inventory-service",
      Partition = 2
    };

    // Act
    var modified = original with { ConsumerGroup = "shipping-service" };

    // Assert
    await Assert.That(modified.TransportType).IsEqualTo(TransportType.Kafka);
    await Assert.That(modified.Topic).IsEqualTo("orders-topic");
    await Assert.That(modified.ConsumerGroup).IsEqualTo("shipping-service");
    await Assert.That(modified.Partition).IsEqualTo(2);
    await Assert.That(modified).IsNotEqualTo(original);
  }

  [Test]
  public async Task SubscriptionTarget_ToString_ShouldContainPropertyValuesAsync() {
    // Arrange
    var target = new SubscriptionTarget {
      TransportType = TransportType.RabbitMQ,
      Topic = "orders-exchange",
      QueueName = "inventory-queue",
      RoutingKey = "order.created"
    };

    // Act
    var result = target.ToString();

    // Assert
    await Assert.That(result).Contains("RabbitMQ");
    await Assert.That(result).Contains("orders-exchange");
    await Assert.That(result).Contains("inventory-queue");
    await Assert.That(result).Contains("order.created");
  }

  // ========================================
  // PHASE 2: PolicyConfiguration Publishing Tests
  // ========================================

  [Test]
  public async Task PolicyConfiguration_PublishToKafka_ShouldAddPublishTargetAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.PublishToKafka("orders-topic");

    // Assert
    await Assert.That(config.PublishTargets).Count().IsEqualTo(1);
    await Assert.That(config.PublishTargets[0].TransportType).IsEqualTo(TransportType.Kafka);
    await Assert.That(config.PublishTargets[0].Destination).IsEqualTo("orders-topic");
  }

  [Test]
  public async Task PolicyConfiguration_PublishToServiceBus_ShouldAddPublishTargetAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.PublishToServiceBus("orders-topic");

    // Assert
    await Assert.That(config.PublishTargets).Count().IsEqualTo(1);
    await Assert.That(config.PublishTargets[0].TransportType).IsEqualTo(TransportType.ServiceBus);
    await Assert.That(config.PublishTargets[0].Destination).IsEqualTo("orders-topic");
  }

  [Test]
  public async Task PolicyConfiguration_PublishToRabbitMQ_ShouldAddPublishTargetAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.PublishToRabbitMQ("orders-exchange", "order.created");

    // Assert
    await Assert.That(config.PublishTargets).Count().IsEqualTo(1);
    await Assert.That(config.PublishTargets[0].TransportType).IsEqualTo(TransportType.RabbitMQ);
    await Assert.That(config.PublishTargets[0].Destination).IsEqualTo("orders-exchange");
    await Assert.That(config.PublishTargets[0].RoutingKey).IsEqualTo("order.created");
  }

  [Test]
  public async Task PolicyConfiguration_PublishToMultipleTransports_ShouldAddAllTargetsAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config
      .PublishToKafka("orders-topic")
      .PublishToServiceBus("orders-topic");

    // Assert
    await Assert.That(config.PublishTargets).Count().IsEqualTo(2);
    await Assert.That(config.PublishTargets[0].TransportType).IsEqualTo(TransportType.Kafka);
    await Assert.That(config.PublishTargets[1].TransportType).IsEqualTo(TransportType.ServiceBus);
  }

  [Test]
  public async Task PolicyConfiguration_PublishToKafka_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.PublishToKafka("orders-topic");

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }

  // ========================================
  // PHASE 2: PolicyConfiguration Subscribing Tests
  // ========================================

  [Test]
  public async Task PolicyConfiguration_SubscribeFromKafka_ShouldAddSubscriptionTargetAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.SubscribeFromKafka("orders-topic", "inventory-service");

    // Assert
    await Assert.That(config.SubscriptionTargets).Count().IsEqualTo(1);
    await Assert.That(config.SubscriptionTargets[0].TransportType).IsEqualTo(TransportType.Kafka);
    await Assert.That(config.SubscriptionTargets[0].Topic).IsEqualTo("orders-topic");
    await Assert.That(config.SubscriptionTargets[0].ConsumerGroup).IsEqualTo("inventory-service");
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromKafka_WithPartition_ShouldStorePartitionAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.SubscribeFromKafka("orders-topic", "inventory-service", partition: 2);

    // Assert
    await Assert.That(config.SubscriptionTargets[0].Partition).IsEqualTo(2);
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromServiceBus_ShouldAddSubscriptionTargetAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.SubscribeFromServiceBus("orders-topic", "inventory-sub");

    // Assert
    await Assert.That(config.SubscriptionTargets).Count().IsEqualTo(1);
    await Assert.That(config.SubscriptionTargets[0].TransportType).IsEqualTo(TransportType.ServiceBus);
    await Assert.That(config.SubscriptionTargets[0].Topic).IsEqualTo("orders-topic");
    await Assert.That(config.SubscriptionTargets[0].SubscriptionName).IsEqualTo("inventory-sub");
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromServiceBus_WithFilter_ShouldStoreSqlFilterAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.SubscribeFromServiceBus("orders-topic", "high-priority-orders", sqlFilter: "Priority > 5");

    // Assert
    await Assert.That(config.SubscriptionTargets[0].SqlFilter).IsEqualTo("Priority > 5");
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromRabbitMQ_ShouldAddSubscriptionTargetAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.SubscribeFromRabbitMQ("orders-exchange", "inventory-queue");

    // Assert
    await Assert.That(config.SubscriptionTargets).Count().IsEqualTo(1);
    await Assert.That(config.SubscriptionTargets[0].TransportType).IsEqualTo(TransportType.RabbitMQ);
    await Assert.That(config.SubscriptionTargets[0].Topic).IsEqualTo("orders-exchange");
    await Assert.That(config.SubscriptionTargets[0].QueueName).IsEqualTo("inventory-queue");
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromRabbitMQ_WithRoutingKey_ShouldStoreRoutingKeyAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config.SubscribeFromRabbitMQ("orders-exchange", "inventory-queue", routingKey: "order.created");

    // Assert
    await Assert.That(config.SubscriptionTargets[0].RoutingKey).IsEqualTo("order.created");
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromMultipleSources_ShouldAddAllTargetsAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    config
      .SubscribeFromKafka("orders-topic", "inventory-service")
      .SubscribeFromServiceBus("orders-topic", "inventory-sub");

    // Assert
    await Assert.That(config.SubscriptionTargets).Count().IsEqualTo(2);
    await Assert.That(config.SubscriptionTargets[0].TransportType).IsEqualTo(TransportType.Kafka);
    await Assert.That(config.SubscriptionTargets[1].TransportType).IsEqualTo(TransportType.ServiceBus);
  }

  [Test]
  public async Task PolicyConfiguration_SubscribeFromKafka_ShouldReturnSelfForFluentAPIAsync() {
    // Arrange
    var config = new PolicyConfiguration();

    // Act
    var result = config.SubscribeFromKafka("orders-topic", "inventory-service");

    // Assert
    await Assert.That(result).IsSameReferenceAs(config);
  }
}
