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
/// Integration tests for IInboxRoutingStrategy and IOutboxRoutingStrategy implementations
/// with real Azure Service Bus transport. Verifies that inbox/outbox strategies correctly route
/// messages through Service Bus topics and subscriptions.
///
/// Note: Azure Service Bus requires topics to be pre-provisioned. The emulator only has
/// topic-00 and topic-01 available, so some routing strategy tests are adapted to use
/// these predefined topics rather than dynamic topic names.
/// </summary>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)] // 240s timeout for integration tests using shared emulator fixture
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public sealed class InboxOutboxRoutingIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  // ========================================
  // SHARED TOPIC OUTBOX STRATEGY TESTS
  // ========================================

  [Test]
  public async Task SharedTopicOutboxStrategy_PublishesToSharedTopicAsync() {
    // Arrange - Use topic-00 as shared topic (pre-provisioned in emulator)
    var outboxStrategy = new SharedTopicOutboxStrategy("topic-00");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Verify destination uses shared topic
    await Assert.That(destination.Address).IsEqualTo("topic-00");
    await Assert.That(destination.RoutingKey).IsEqualTo("orders.ordercreated");

    // Drain any existing messages
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Create transport and set up consumer with warmup detection using harnesses
    var transport = await _createTransportAsync();
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var (warmupAwaiter, testAwaiter, handler) = SubscriptionWarmup.CreateDiscriminatingAwaiters<TestMessage>(
      warmupId,
      msg => msg.Content
    );

    var subscription = await transport.SubscribeAsync(
      handler,
      new TransportDestination("topic-00", "sub-00-a")
    );

    try {
      // Warmup subscription using harness
      await SubscriptionWarmup.WarmupAsync(
        transport,
        destination,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupAwaiter
      );

      // Act: Now publish the actual test message
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(envelope, destination);

      // Assert - Message should arrive at shared topic
      var receivedEnvelope = await testAwaiter.WaitAsync(TimeSpan.FromSeconds(30));
      await Assert.That(receivedEnvelope).IsNotNull();
    } finally {
      subscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  // ========================================
  // SHARED TOPIC INBOX STRATEGY TESTS
  // ========================================

  [Test]
  public async Task SharedTopicInboxStrategy_SubscribesToSharedTopicAsync() {
    // Arrange - Use topic-01 as shared inbox topic
    var inboxStrategy = new SharedTopicInboxStrategy("topic-01");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "inventory" };

    var subscriptionInfo = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Verify subscription is correct
    await Assert.That(subscriptionInfo.Topic).IsEqualTo("topic-01");
    await Assert.That(subscriptionInfo.FilterExpression).IsEqualTo("orders,inventory");

    // Drain any existing messages
    await _drainMessagesAsync("topic-01", "sub-01-a");

    // Create transport and set up consumer with warmup detection using harnesses
    var transport = await _createTransportAsync();
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var (warmupAwaiter, testAwaiter, handler) = SubscriptionWarmup.CreateDiscriminatingAwaiters<TestMessage>(
      warmupId,
      msg => msg.Content
    );

    var transportSubscription = await transport.SubscribeAsync(
      handler,
      new TransportDestination("topic-01", "sub-01-a")
    );

    try {
      // Warmup subscription using harness
      var publishDestination = new TransportDestination(subscriptionInfo.Topic);
      await SubscriptionWarmup.WarmupAsync(
        transport,
        publishDestination,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupAwaiter
      );

      // Act: Publish command to shared inbox
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(envelope, publishDestination);

      // Assert
      var receivedEnvelope = await testAwaiter.WaitAsync(TimeSpan.FromSeconds(30));
      await Assert.That(receivedEnvelope).IsNotNull();
    } finally {
      transportSubscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  // ========================================
  // END-TO-END STRATEGY COMBINATION TESTS
  // ========================================

  [Test]
  public async Task SharedOutbox_ToSharedInbox_EndToEndAsync() {
    // Arrange - Both use shared topic strategy with topic-00
    var sharedTopic = "topic-00";
    var outboxStrategy = new SharedTopicOutboxStrategy(sharedTopic);
    var inboxStrategy = new SharedTopicInboxStrategy(sharedTopic);
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    var subscriptionInfo = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Verify both strategies use the same shared topic
    await Assert.That(destination.Address).IsEqualTo(subscriptionInfo.Topic);

    // Drain any existing messages
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Create transport and set up consumer with warmup detection using harnesses
    var transport = await _createTransportAsync();
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var warmupAwaiter = new SignalAwaiter();
    var awaiter = new MessageIdAwaiter();

    var transportSubscription = await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        // Check if this is the warmup message or the actual test message
        if (envelope is MessageEnvelope<TestMessage> testEnvelope &&
            testEnvelope.Payload.Content.Contains(warmupId)) {
          warmupAwaiter.Signal();
        } else {
          await awaiter.Handler(envelope, envelopeType, ct);
        }
      },
      new TransportDestination(subscriptionInfo.Topic, "sub-00-a")
    );

    try {
      // Warmup subscription using harness
      await SubscriptionWarmup.WarmupAsync(
        transport,
        destination,
        () => _createTestEnvelopeWithContent(warmupId),
        warmupAwaiter
      );

      // Act: Publish the actual test message
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(envelope, destination);

      // Assert
      var receivedMessageId = await awaiter.WaitAsync(TimeSpan.FromSeconds(30));
      await Assert.That(receivedMessageId).IsEqualTo(envelope.MessageId.ToString());
    } finally {
      transportSubscription.Dispose();
      await transport.DisposeAsync();
    }
  }

  // ========================================
  // ROUTING STRATEGY UNIT TESTS
  // ========================================

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_ReturnsDomainTopicAsync() {
    // Arrange
    var outboxStrategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var destination = outboxStrategy.GetDestination(
      typeof(TestNamespaces.MyApp.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Verify routing logic (not transport delivery)
    await Assert.That(destination.Address).IsEqualTo("orders");
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");
  }

  [Test]
  public async Task DomainTopicInboxStrategy_GetSubscription_ReturnsDomainInboxAsync() {
    // Arrange
    var inboxStrategy = new DomainTopicInboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Assert - Verify routing logic
    await Assert.That(subscription.Topic).IsEqualTo("orders.inbox");
    await Assert.That(subscription.FilterExpression).IsNull();
  }

  [Test]
  public async Task DomainTopicInboxStrategy_WithCustomSuffix_ReturnsCorrectTopicAsync() {
    // Arrange
    var inboxStrategy = new DomainTopicInboxStrategy(".in");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders" };

    // Act
    var subscription = inboxStrategy.GetSubscription(
      ownedDomains,
      "order-service",
      MessageKind.Command
    );

    // Assert
    await Assert.That(subscription.Topic).IsEqualTo("orders.in");
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
    return _createTestEnvelopeWithContent("test-inbox-outbox-content");
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithContent(string content) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
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
