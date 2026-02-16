using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Transport;
using Whizbang.Transports.AzureServiceBus.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Integration tests for NamespaceRoutingStrategy with real Azure Service Bus transport.
/// Verifies that messages are routed to correct topics based on namespace patterns.
///
/// Note: Azure Service Bus requires topics to be pre-provisioned. These tests verify
/// the routing strategy logic and test transport delivery using predefined topics
/// (topic-00, topic-01) from the emulator configuration.
/// </summary>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)] // 240s timeout for integration tests using shared emulator fixture
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public sealed class NamespaceRoutingTransportIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  // ========================================
  // NAMESPACE ROUTING STRATEGY UNIT TESTS
  // ========================================

  [Test]
  public async Task NamespaceRoutingStrategy_HierarchicalNamespace_ReturnsFullNamespaceAsync() {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Act
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Assert - Now returns full namespace
    await Assert.That(topic).IsEqualTo("testnamespaces.myapp.orders.events");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_CommandNamespace_ReturnsFullNamespaceAsync() {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Contracts.Commands.CreateOrder);

    // Act
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Assert - Now returns full namespace
    await Assert.That(topic).IsEqualTo("testnamespaces.myapp.contracts.commands");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_WithCustomResolver_UsesCustomLogicAsync() {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy(
      type => "custom-topic-" + type.Name.ToLowerInvariant()
    );
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Act
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Assert
    await Assert.That(topic).IsEqualTo("custom-topic-ordercreated");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_EventsNamespace_ReturnsFullNamespaceAsync() {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Contracts.Events.OrderCreated);

    // Act
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Assert - Now returns full namespace
    await Assert.That(topic).IsEqualTo("testnamespaces.myapp.contracts.events");
  }

  [Test]
  public async Task NamespaceRoutingStrategy_TopicIsLowercaseAsync() {
    // Arrange
    var routingStrategy = new NamespaceRoutingStrategy();
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Act
    var topic = routingStrategy.ResolveTopic(messageType, "");

    // Assert
    await Assert.That(topic).IsEqualTo(topic.ToLowerInvariant());
    await Assert.That(topic).IsEqualTo("testnamespaces.myapp.orders.events");
  }

  // ========================================
  // COMPOSITE ROUTING STRATEGY TESTS
  // ========================================

  [Test]
  public async Task CompositeRoutingStrategy_ChainsStrategiesCorrectlyAsync() {
    // Arrange - Chain NamespaceRoutingStrategy with pool suffix
    var composite = new CompositeTopicRoutingStrategy(
      new NamespaceRoutingStrategy(),
      new TestPoolSuffixRoutingStrategy("-01")
    );
    var messageType = typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated);

    // Act
    var topic = composite.ResolveTopic(messageType, "");

    // Assert - Full namespace + suffix
    await Assert.That(topic).IsEqualTo("testnamespaces.myapp.orders.events-01");
  }

  // ========================================
  // REAL TRANSPORT INTEGRATION TESTS
  // ========================================

  [Test]
  public async Task PublishAsync_WithPredefinedTopic_DeliversMessageAsync() {
    // Arrange - Use topic-00 which exists in the emulator
    var transport = await _createTransportAsync();

    // Drain any existing messages
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Use MessageIdAwaiter harness (internally uses RunContinuationsAsynchronously)
    var awaiter = new MessageIdAwaiter();
    var subscription = await transport.SubscribeAsync(
      awaiter.Handler,
      new TransportDestination("topic-00", "sub-00-a")
    );

    try {
      await Task.Delay(500);

      // Publish message
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(envelope, new TransportDestination("topic-00"));

      // Assert - harness handles timeout with proper exception
      var receivedMessageId = await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
      await Assert.That(receivedMessageId).IsNotNull();
    } finally {
      subscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  [Test]
  public async Task PublishAsync_MultipleMessages_AllDeliveredAsync() {
    // Arrange
    var transport = await _createTransportAsync();

    // Drain existing messages
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Use CountingMessageAwaiter harness (internally uses RunContinuationsAsynchronously)
    var awaiter = new CountingMessageAwaiter(expectedCount: 3);

    var subscription = await transport.SubscribeAsync(
      awaiter.Handler,
      new TransportDestination("topic-00", "sub-00-a")
    );

    try {
      await Task.Delay(500);

      // Publish multiple messages
      for (int i = 0; i < awaiter.ExpectedCount; i++) {
        var envelope = _createTestEnvelope();
        await transport.PublishAsync(envelope, new TransportDestination("topic-00"));
      }

      // Assert - harness handles timeout with diagnostic message
      await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
      await Assert.That(awaiter.ReceivedCount).IsEqualTo(awaiter.ExpectedCount);
    } finally {
      subscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  [Test]
  public async Task PublishAsync_ToDifferentTopics_RoutesCorrectlyAsync() {
    // Arrange
    var transport = await _createTransportAsync();

    // Drain both topics
    await _drainMessagesAsync("topic-00", "sub-00-a");
    await _drainMessagesAsync("topic-01", "sub-01-a");

    // Use MessageIdAwaiter harnesses (internally use RunContinuationsAsynchronously)
    var awaiter00 = new MessageIdAwaiter();
    var awaiter01 = new MessageIdAwaiter();

    var subscription00 = await transport.SubscribeAsync(
      awaiter00.Handler,
      new TransportDestination("topic-00", "sub-00-a")
    );

    var subscription01 = await transport.SubscribeAsync(
      awaiter01.Handler,
      new TransportDestination("topic-01", "sub-01-a")
    );

    try {
      await Task.Delay(500);

      // Publish to both topics
      var envelope00 = _createTestEnvelope();
      var envelope01 = _createTestEnvelope();

      await transport.PublishAsync(envelope00, new TransportDestination("topic-00"));
      await transport.PublishAsync(envelope01, new TransportDestination("topic-01"));

      // Assert - harnesses handle timeout with proper exception
      var received00 = await awaiter00.WaitAsync(TimeSpan.FromSeconds(30));
      var received01 = await awaiter01.WaitAsync(TimeSpan.FromSeconds(30));

      await Assert.That(received00).IsEqualTo(envelope00.MessageId.ToString());
      await Assert.That(received01).IsEqualTo(envelope01.MessageId.ToString());
    } finally {
      subscription00.Dispose();
      subscription01.Dispose();
      await transport.DisposeAsync();
    }
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private async Task<AzureServiceBusTransport> _createTransportAsync() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      jsonOptions
    );

    await transport.InitializeAsync();
    return transport;
  }

  private async Task _drainMessagesAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      for (var i = 0; i < 100; i++) {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (msg == null) {
          break;
        }
        await receiver.CompleteMessageAsync(msg);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-routing-content"),
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

  /// <summary>
  /// Pool suffix routing strategy for testing composite strategies.
  /// </summary>
  private sealed class TestPoolSuffixRoutingStrategy(string suffix) : ITopicRoutingStrategy {
    public string ResolveTopic(Type messageType, string baseTopic, IReadOnlyDictionary<string, object>? context = null) {
      return baseTopic + suffix;
    }
  }
}
