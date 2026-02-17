using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQTransport PublishAsync implementation.
/// RabbitMQ transport provides reliable pub/sub messaging.
/// </summary>
public class RabbitMQTransportTests {
  [Test]
  public async Task PublishAsync_WithValidMessage_RentsAndReturnsChannelAsync() {
    // Arrange
    var channelUsed = false;
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => {
      channelUsed = true;
      return Task.FromResult<IChannel>(fakeChannel);
    });

    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    // Use reflection-based JSON for unit tests (AOT compatibility tested in integration tests)
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange");

    // Act
    await transport.PublishAsync(envelope, destination);

    // Assert - Verify channel was rented from pool
    await Assert.That(channelUsed).IsTrue();
  }

  [Test]
  public async Task Capabilities_ReturnsPublishSubscribeAndReliableAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    // Use reflection-based JSON for unit tests (AOT compatibility tested in integration tests)
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    // Act
    var capabilities = transport.Capabilities;

    // Assert - RabbitMQ supports PublishSubscribe and Reliable (NOT Ordered in multi-consumer scenarios)
    await Assert.That((capabilities & TransportCapabilities.PublishSubscribe) != 0).IsTrue();
    await Assert.That((capabilities & TransportCapabilities.Reliable) != 0).IsTrue();
  }

  [Test]
  public async Task IsInitialized_ReturnsFalse_BeforeInitializeAsyncAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    // Use reflection-based JSON for unit tests (AOT compatibility tested in integration tests)
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    // Act & Assert
    await Assert.That(transport.IsInitialized).IsFalse();
  }

  [Test]
  public async Task IsInitialized_ReturnsTrue_AfterInitializeAsyncAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    // Use reflection-based JSON for unit tests (AOT compatibility tested in integration tests)
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    // Act
    await transport.InitializeAsync();

    // Assert
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  [Test]
  public async Task SubscribeAsync_CreatesConsumer_AndInvokesHandlerAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    // Use reflection-based JSON for unit tests (AOT compatibility tested in integration tests)
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    // Act
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Assert - Verify subscription created and consumer registered
    await Assert.That(subscription).IsNotNull();
    await Assert.That(fakeChannel.QueueDeclareAsyncCalled).IsTrue();
    await Assert.That(fakeChannel.QueueBindAsyncCalled).IsTrue();
    await Assert.That(fakeChannel.BasicConsumeAsyncCalled).IsTrue();
  }

  [Test]
  public async Task Subscription_InitialState_IsActiveAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    // Act
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Assert
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task Subscription_Pause_SetsIsActiveFalseAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Act
    await subscription.PauseAsync();

    // Assert
    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Subscription_Resume_SetsIsActiveTrueAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    await subscription.PauseAsync();

    // Act
    await subscription.ResumeAsync();

    // Assert
    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task Subscription_Dispose_CancelsConsumerAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Act
    subscription.Dispose();

    // Give the fire-and-forget disposal task time to complete
    await Task.Delay(100);

    // Assert - Verify consumer was cancelled
    await Assert.That(fakeChannel.BasicCancelAsyncCalled).IsTrue();
    await Assert.That(fakeChannel.IsDisposed).IsTrue();
  }

  #region Deterministic Queue Naming Tests

  [Test]
  public async Task SubscribeAsync_WithSubscriberNameMetadata_UsesDeterministicQueueNameAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    // Create metadata with SubscriberName
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"order-service\"").RootElement.Clone()
    };
    var destination = new TransportDestination("inbox", "#", metadata);

    // Act
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Assert - Queue name should be "{SubscriberName}-{exchangeName}"
    await Assert.That(fakeChannel.LastDeclaredQueueName).IsEqualTo("order-service-inbox");
  }

  [Test]
  public async Task SubscribeAsync_WithoutSubscriberName_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    // No metadata - SubscriberName not provided
    var destination = new TransportDestination("inbox");

    // Act & Assert - Should throw because SubscriberName is required
    await Assert.That(async () => await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    )).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task SubscribeAsync_WithDefaultQueueNameOption_UsesOptionOverMetadataAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions {
      DefaultQueueName = "explicit-queue-name"
    };

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    // Has SubscriberName but should be ignored when DefaultQueueName is set
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"order-service\"").RootElement.Clone()
    };
    var destination = new TransportDestination("inbox", "#", metadata);

    // Act
    var subscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Assert - DefaultQueueName takes precedence
    await Assert.That(fakeChannel.LastDeclaredQueueName).IsEqualTo("explicit-queue-name");
  }

  [Test]
  public async Task SubscribeAsync_MultipleCallsWithSameSubscriberName_UseSameQueueNameAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"inventory-worker\"").RootElement.Clone()
    };
    var destination = new TransportDestination("events.inventory", "#", metadata);

    // Act - Subscribe twice (simulating two service instances)
    var subscription1 = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    var firstQueueName = fakeChannel.LastDeclaredQueueName;

    var subscription2 = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    );

    // Assert - Both should use the same deterministic queue name
    await Assert.That(fakeChannel.LastDeclaredQueueName).IsEqualTo(firstQueueName);
    await Assert.That(firstQueueName).IsEqualTo("inventory-worker-events.inventory");
  }

  [Test]
  public async Task SubscribeAsync_WithEmptySubscriberName_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var fakeChannel = new FakeChannel();
    var fakeConnection = new FakeConnection(() => Task.FromResult<IChannel>(fakeChannel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var options = new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      fakeConnection,
      jsonOptions,
      pool,
      options,
      logger: null
    );

    await transport.InitializeAsync();

    // Empty SubscriberName should be treated as missing
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"  \"").RootElement.Clone()
    };
    var destination = new TransportDestination("inbox", "#", metadata);

    // Act & Assert - Should throw because SubscriberName is effectively missing
    await Assert.That(async () => await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => await Task.CompletedTask,
      destination
    )).Throws<InvalidOperationException>();
  }

  #endregion

  // Helper to create a test envelope
  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-content"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }
}
