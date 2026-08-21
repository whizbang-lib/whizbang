using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// THE PHASE-7 RETIREMENT E2E LOCK (RabbitMQ tier): full flip + shared-inbox retirement
/// through the REAL strategies, publish path, and transport — a domain command rides its
/// per-namespace inbox exchange, a framework-reserved (system) command rides the system
/// broadcast inbox (the sole carve-out), and the legacy shared exchange incurs ZERO broker
/// operations: no publish, no declare, no binding, no delivery — asserted on the recording
/// FakeChannel, not by absence of processing.
/// </summary>
public class RabbitMQSharedInboxRetirementE2ELockTests {
  private const string HANDLED_NAMESPACE = "myapp.orders.commands";
  private const string SHARED_INBOX = "inbox";
  private const string SERVICE_NAME = "svc-orders";

  /// <summary>Envelope in the outbox's wire shape: MessageEnvelope&lt;JsonElement&gt; whose payload
  /// is the serialized TestMessage — exactly how the outbox loads rows for publish.</summary>
  private static MessageEnvelope<System.Text.Json.JsonElement> _outboxEnvelope(string content) {
    var payload = System.Text.Json.JsonSerializer.SerializeToElement(
      new TestMessage(content),
      RabbitTestWire.JsonOptions.GetTypeInfo(typeof(TestMessage))!);
    return new MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = []
    };
  }

  private static OutboxWork _commandWork(string messageType, string content) => new() {
    MessageId = Guid.CreateVersion7(),
    Destination = SHARED_INBOX, // the pre-flip stamp; publish-time resolution is the authority
    Envelope = _outboxEnvelope(content),
    EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!,
    MessageType = messageType,
    StreamId = null,
    PartitionNumber = 1,
    Attempts = 0,
    Status = MessageProcessingStatus.Stored,
    Flags = WorkBatchOptions.None
  };

  [Test]
  public async Task Retirement_CommandToNamespaceInbox_SystemToBroadcast_SharedExchangeZeroBrokerOpsAsync() {
    // ---------- Topology: the REAL retirement subscription set ----------
    var routingOptions = new RoutingOptions().RouteAllCommandNamespacesToInbox().RetireSharedInbox();
    var inboxStrategy = new NamespaceInboxStrategy(routingOptions);
    var subscriptions = inboxStrategy.GetSubscriptions(new InboxSubscriptionContext(
      SERVICE_NAME,
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      [new HandledMessageInfo("MyApp.Orders.Commands.PlaceOrderCommand", HANDLED_NAMESPACE, MessageKind.Command)]));
    var flippedEntity = CommandInboxNaming.TopicFor(HANDLED_NAMESPACE);
    var broadcastEntity = CommandInboxNaming.SystemBroadcastTopic;

    // ---------- Consumer: subscribe the FULL retirement set (one channel per subscription
    // so each registered consumer stays addressable for delivery) ----------
    var deliveriesByExchange = new Dictionary<string, int>(StringComparer.Ordinal);
    var consumerChannels = new Dictionary<string, FakeChannel>(StringComparer.Ordinal);
    var subscriptionHandles = new List<IDisposable>();
    try {
      foreach (var subscription in subscriptions) {
        var exchange = subscription.Topic;
        var channel = new FakeChannel { ExistingExchanges = { exchange } };
        var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
        var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
        subscriptionHandles.Add(await transport.SubscribeAsync(
          (_, _, _) => {
            lock (deliveriesByExchange) {
              deliveriesByExchange[exchange] = deliveriesByExchange.GetValueOrDefault(exchange) + 1;
            }
            return Task.CompletedTask;
          },
          RabbitTestWire.Destination(exchange: exchange, routingKey: "#")));
        consumerChannels[exchange] = channel;

        // The consumer's declares stay on ITS entity — never the legacy shared exchange.
        await Assert.That(channel.DeclaredExchanges.Select(e => e.Exchange)).DoesNotContain(SHARED_INBOX);
        await Assert.That(channel.QueueBindings.Select(b => b.Exchange)).DoesNotContain(SHARED_INBOX);
      }

      await _runPublishAndDeliveryAsync(
        routingOptions, flippedEntity, broadcastEntity, deliveriesByExchange, consumerChannels);
    } finally {
      foreach (var handle in subscriptionHandles) {
        handle.Dispose();
      }
    }
  }

  private static async Task _runPublishAndDeliveryAsync(
      RoutingOptions routingOptions,
      string flippedEntity,
      string broadcastEntity,
      Dictionary<string, int> deliveriesByExchange,
      Dictionary<string, FakeChannel> consumerChannels) {
    var subscribedExchanges = consumerChannels.Keys.ToList();
    await Assert.That(subscribedExchanges).Contains(flippedEntity);
    await Assert.That(subscribedExchanges).Contains(broadcastEntity);
    await Assert.That(subscribedExchanges).DoesNotContain(SHARED_INBOX)
      .Because("under retirement the consumer binds NO queue to the legacy shared exchange");

    // ---------- Publisher: the REAL flip publish path ----------
    var publisherChannel = new FakeChannel { ExistingExchanges = { flippedEntity, broadcastEntity } };
    var publisherConnection = new FakeConnection(() => Task.FromResult<IChannel>(publisherChannel));
    var publisherTransport = await RabbitTestWire.NewInitializedTransportAsync(publisherConnection);
    var publishStrategy = new TransportPublishStrategy(
      publisherTransport,
      new DefaultTransportReadinessCheck(),
      SHARED_INBOX,
      namespaceRouting: new NamespaceOutboxStrategy(routingOptions));

    var domainResult = await publishStrategy.PublishAsync(
      _commandWork("MyApp.Orders.Commands.PlaceOrderCommand, MyApp", "domain-command"),
      CancellationToken.None);
    var systemResult = await publishStrategy.PublishAsync(
      _commandWork("Whizbang.Core.Commands.System.RebuildPerspectiveCommand, Whizbang.Core", "system-command"),
      CancellationToken.None);

    await Assert.That(domainResult.Success).IsTrue();
    await Assert.That(systemResult.Success).IsTrue();
    var publishedExchanges = publisherChannel.PublishedMessages.Select(m => m.Exchange).ToList();
    await Assert.That(publishedExchanges).Contains(flippedEntity);
    await Assert.That(publishedExchanges).Contains(broadcastEntity)
      .Because("durable system commands broadcast on inbox.whizbang — the sole carve-out");
    await Assert.That(publishedExchanges).DoesNotContain(SHARED_INBOX)
      .Because("ZERO publish-side broker operations on the legacy shared exchange under retirement");
    await Assert.That(publisherChannel.DeclaredExchanges.Select(e => e.Exchange)).DoesNotContain(SHARED_INBOX)
      .Because("the publisher never declares the retired entity either");

    // ---------- Delivery: each published message reaches its entity's consumer ----------
    foreach (var published in publisherChannel.PublishedMessages) {
      var consumerChannel = consumerChannels[published.Exchange];
      var consumer = (AsyncEventingBasicConsumer)consumerChannel.LastRegisteredConsumer!;
      var (properties, body) = RabbitTestWire.ValidWireMessage("delivered");
      await consumer.HandleBasicDeliverAsync(
        "retirement-consumer", 1UL, false, published.Exchange, published.RoutingKey, properties, body);
    }

    await Assert.That(deliveriesByExchange.GetValueOrDefault(flippedEntity)).IsEqualTo(1)
      .Because("the domain command arrives on its per-namespace inbox");
    await Assert.That(deliveriesByExchange.GetValueOrDefault(broadcastEntity)).IsEqualTo(1)
      .Because("the system command arrives on the broadcast inbox");
    await Assert.That(deliveriesByExchange.ContainsKey(SHARED_INBOX)).IsFalse()
      .Because("NOTHING lands on (or requires) the legacy shared exchange — the retirement end-state");
  }
}
