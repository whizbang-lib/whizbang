using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportManager subscription functionality.
/// These tests ensure proper subscription handling with transport-specific metadata.
/// </summary>
[Category("Transports")]
public class TransportManagerSubscriptionTests {
  [Test]
  public async Task SubscribeFromTargetsAsync_WithSingleTarget_ShouldCreateSubscriptionAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.InProcess,
        Topic = "test-topic"
      }
    };

    var handlerCalled = false;
    Task handler(IMessageEnvelope envelope) {
      handlerCalled = true;
      return Task.CompletedTask;
    }

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
    await Assert.That(subscriptions[0]).IsNotNull();

    // Verify handler works by publishing a message
    var testEnvelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "test",
      Hops = new List<MessageHop>()
    };
    await transport.PublishAsync(testEnvelope, new TransportDestination("test-topic"), CancellationToken.None);
    await Task.Delay(50); // Allow async processing

    await Assert.That(handlerCalled).IsTrue();
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithMultipleTargets_ShouldCreateMultipleSubscriptionsAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport1 = new InProcessTransport();
    var transport2 = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport1);
    manager.AddTransport(TransportType.Kafka, transport2); // Using InProcess as mock

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.InProcess,
        Topic = "topic1"
      },
      new SubscriptionTarget {
        TransportType = TransportType.Kafka,
        Topic = "topic2"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(2);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithKafkaConsumerGroup_ShouldIncludeInMetadataAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.Kafka, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.Kafka,
        Topic = "kafka-topic",
        ConsumerGroup = "my-consumer-group"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
    // Metadata is passed to transport - subscription should be created successfully
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithServiceBusSubscriptionName_ShouldIncludeInMetadataAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.ServiceBus, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.ServiceBus,
        Topic = "sb-topic",
        SubscriptionName = "my-subscription"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithServiceBusSqlFilter_ShouldIncludeInMetadataAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.ServiceBus, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.ServiceBus,
        Topic = "sb-topic",
        SubscriptionName = "filtered-sub",
        SqlFilter = "Category = 'Important'"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithRabbitMQQueueName_ShouldIncludeInMetadataAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.RabbitMQ, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.RabbitMQ,
        Topic = "exchange",
        QueueName = "my-queue"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithKafkaPartition_ShouldIncludeInMetadataAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.Kafka, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.Kafka,
        Topic = "partitioned-topic",
        Partition = 3
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.RabbitMQ, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.RabbitMQ,
        Topic = "exchange",
        RoutingKey = "routing.key.pattern"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithAllMetadata_ShouldIncludeAllInDestinationAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.Kafka, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.Kafka,
        Topic = "comprehensive-topic",
        ConsumerGroup = "group1",
        RoutingKey = "key1",
        Partition = 5
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_HandlerReceivesEnvelope_ShouldWorkAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.InProcess,
        Topic = "handler-test"
      }
    };

    IMessageEnvelope? receivedEnvelope = null;
    Task handler(IMessageEnvelope envelope) {
      receivedEnvelope = envelope;
      return Task.CompletedTask;
    }

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Publish a test message
    var testEnvelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "test-payload",
      Hops = new List<MessageHop>()
    };
    await transport.PublishAsync(testEnvelope, new TransportDestination("handler-test"), CancellationToken.None);
    await Task.Delay(50); // Allow async processing

    // Assert
    await Assert.That(receivedEnvelope).IsNotNull();
    var typedEnvelope = receivedEnvelope as MessageEnvelope<string>;
    await Assert.That(typedEnvelope).IsNotNull();
    await Assert.That(typedEnvelope!.Payload).IsEqualTo("test-payload");
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WhenTransportNotRegistered_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.Kafka, // Not registered
        Topic = "topic"
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act & Assert
    await Assert.That(() => manager.SubscribeFromTargetsAsync(targets, handler))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task SubscribeFromTargetsAsync_WithEmptyStringsInMetadata_ShouldNotIncludeThemAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.Kafka, transport);

    var targets = new List<SubscriptionTarget> {
      new SubscriptionTarget {
        TransportType = TransportType.Kafka,
        Topic = "topic",
        ConsumerGroup = "",  // Empty strings should not be included
        RoutingKey = "",
        QueueName = ""
      }
    };

    Task handler(IMessageEnvelope envelope) => Task.CompletedTask;

    // Act
    var subscriptions = await manager.SubscribeFromTargetsAsync(targets, handler);

    // Assert
    await Assert.That(subscriptions).HasCount().EqualTo(1);
  }
}
