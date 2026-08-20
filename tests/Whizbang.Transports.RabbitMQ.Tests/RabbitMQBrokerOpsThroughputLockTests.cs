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
/// THE O(3N) BROKER-OP THROUGHPUT LOCK (topology arc phase 6), RabbitMQ side: a burst of N
/// single-handler commands through the flipped topology costs O(3N) broker operations —
/// N publishes + N deliveries + N acks — NOT O(3N × queue count). The pre-flip census
/// measured ~42 broker ops per ingress command on the shared inbox (the broker copying into
/// every service's queue, each consumer paying receive + settle only to discard); the
/// flipped bound is ops/command ≤ 6.
/// </summary>
/// <remarks>
/// Broker fan-out is DERIVED, not hand-wired: a RabbitMQ topic exchange delivers one copy
/// per BOUND QUEUE — so deliveries go to exactly the services whose phase-5 subscription set
/// (<see cref="NamespaceInboxStrategy"/>) binds a queue to the flipped exchange. Exactly one
/// service does; the others' broker-op count for this namespace is zero by topology.
/// </remarks>
public class RabbitMQBrokerOpsThroughputLockTests {
  private const int N = 25;
  private const double OPS_PER_COMMAND_BOUND = 6.0;
  private const string HANDLED_NAMESPACE = "myapp.orders.commands";

  private static InboxSubscriptionContext _serviceContext(string serviceName, params HandledMessageInfo[] handled) =>
    new(serviceName, new HashSet<string>(StringComparer.OrdinalIgnoreCase), handled);

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

  [Test]
  public async Task BurstOfN_SingleHandlerCommands_CostsThreeOpsPerCommand_NotQueueMultipliedAsync() {
    // ---------- Topology: three services, ONE handles the namespace (phase-5 projection) ----------
    var inboxStrategy = new NamespaceInboxStrategy();
    var handlerSubscriptions = inboxStrategy.GetSubscriptions(_serviceContext(
      "svc-orders",
      new HandledMessageInfo("MyApp.Orders.Commands.PlaceOrderCommand", HANDLED_NAMESPACE, MessageKind.Command)));
    var nonHandlerA = inboxStrategy.GetSubscriptions(_serviceContext(
      "svc-billing",
      new HandledMessageInfo("MyApp.Billing.Commands.ChargeCardCommand", "myapp.billing.commands", MessageKind.Command)));
    var nonHandlerB = inboxStrategy.GetSubscriptions(_serviceContext("svc-audit"));

    var flippedEntity = CommandInboxNaming.TopicFor(HANDLED_NAMESPACE);
    var subscriptionsOnEntity = new[] { handlerSubscriptions, nonHandlerA, nonHandlerB }
      .SelectMany(set => set)
      .Count(s => s.Topic == flippedEntity);
    await Assert.That(subscriptionsOnEntity).IsEqualTo(1)
      .Because("non-handlers bind ZERO queues to the flipped exchange — their broker-op count for this namespace is zero by topology");

    // ---------- Publisher: N commands through the REAL publish strategy + transport ----------
    var publisherChannel = new FakeChannel { ExistingExchanges = { flippedEntity } };
    var publisherConnection = new FakeConnection(() => Task.FromResult<IChannel>(publisherChannel));
    var publisherTransport = await RabbitTestWire.NewInitializedTransportAsync(publisherConnection);
    var routingOptions = new RoutingOptions().RouteCommandNamespaceToInbox(HANDLED_NAMESPACE);
    var publishStrategy = new TransportPublishStrategy(
      publisherTransport,
      new DefaultTransportReadinessCheck(),
      "inbox",
      namespaceRouting: new NamespaceOutboxStrategy(routingOptions));

    for (var i = 0; i < N; i++) {
      var envelope = _outboxEnvelope($"burst-{i}");
      var result = await publishStrategy.PublishAsync(new OutboxWork {
        MessageId = Guid.CreateVersion7(),
        Destination = "inbox", // pre-flip stamp; publish-time resolution is the authority
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!,
        MessageType = "MyApp.Orders.Commands.PlaceOrderCommand, MyApp",
        StreamId = null,
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchOptions.None
      }, CancellationToken.None);
      await Assert.That(result.Success).IsTrue();
    }

    var sends = publisherChannel.PublishedMessages.Count;
    await Assert.That(sends).IsEqualTo(N).Because("one publish per command — no fan-out on the publish side");
    foreach (var published in publisherChannel.PublishedMessages) {
      await Assert.That(published.Exchange).IsEqualTo(flippedEntity);
    }

    // ---------- Consumer: the ONE bound queue receives each broker copy ----------
    var consumerChannel = new FakeChannel { ExistingExchanges = { flippedEntity } };
    var consumerConnection = new FakeConnection(() => Task.FromResult<IChannel>(consumerChannel));
    var consumerTransport = await RabbitTestWire.NewInitializedTransportAsync(consumerConnection);
    var deliveries = 0;
    using var subscription = await consumerTransport.SubscribeAsync(
      (_, _, _) => {
        deliveries++;
        return Task.CompletedTask;
      },
      RabbitTestWire.Destination(exchange: flippedEntity, routingKey: "#"));

    await Assert.That(consumerChannel.QueueBindings.Count(b => b.Exchange == flippedEntity)).IsEqualTo(1)
      .Because("the handling service binds exactly one queue to the flipped exchange");

    var consumer = (AsyncEventingBasicConsumer)consumerChannel.LastRegisteredConsumer!;
    for (var i = 0; i < N; i++) {
      var (properties, body) = RabbitTestWire.ValidWireMessage($"burst-{i}");
      await consumer.HandleBasicDeliverAsync(
        "test-consumer", (ulong)(i + 1), false, flippedEntity, "#", properties, body);
    }

    // ---------- The lock ----------
    var settles = consumerChannel.BasicAckCount;
    await Assert.That(deliveries).IsEqualTo(N);
    await Assert.That(settles).IsEqualTo(N);
    await Assert.That(consumerChannel.BasicNackCount).IsEqualTo(0);

    var totalOps = sends + (deliveries * subscriptionsOnEntity) + settles;
    await Assert.That(totalOps).IsEqualTo(3 * N)
      .Because("O(3N): publish + deliver + ack, once each — the shared-inbox multiplier is gone");
    await Assert.That(totalOps / (double)N).IsLessThanOrEqualTo(OPS_PER_COMMAND_BOUND)
      .Because("census-derived bound: ops/command ≤ 6 flipped, vs ~42 measured on the shared inbox");
  }
}
