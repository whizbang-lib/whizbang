using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
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

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// THE O(3N) BROKER-OP THROUGHPUT LOCK (topology arc phase 6, the headline regression lock
/// for the per-namespace-command-inboxes proposal): a burst of N single-handler commands
/// through the flipped topology costs O(3N) broker operations — N sends + N deliveries +
/// N settles — NOT O(3N × subscription count). The pre-flip census measured ~42 broker ops
/// per ingress command on the shared inbox (1 send, the broker copying into 14 subscriptions,
/// 14 × session-accept/receive/complete, 13 of them pure discards); the flipped bound is
/// ops/command ≤ 6 (3 measured + headroom for session-accept overhead).
/// </summary>
/// <remarks>
/// Broker fan-out is DERIVED, not hand-wired: deliveries go to exactly the services whose
/// phase-5 subscription set (the REAL <see cref="NamespaceInboxStrategy"/> projection)
/// contains the flipped entity — a per-namespace inbox has no filter, the topic IS the
/// filter, so one copy per subscription is exact broker semantics. If the inbox strategy
/// ever regresses to subscribing non-handlers, or the publisher regresses to fan-out sends,
/// this lock fails.
/// </remarks>
public class AsbBrokerOpsThroughputLockTests {
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
      AsbTransportTestData.CombinedOptions.GetTypeInfo(typeof(TestMessage))!);
    return new MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = Whizbang.Core.ValueObjects.MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = []
    };
  }

  [Test]
  public async Task BurstOfN_SingleHandlerCommands_CostsThreeOpsPerCommand_NotSubscriptionMultipliedAsync() {
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

    // The broker copies one delivery per subscription on the entity — exactly one exists.
    await Assert.That(subscriptionsOnEntity).IsEqualTo(1)
      .Because("non-handlers must hold ZERO subscriptions on the flipped entity — their broker-op count for this namespace is zero by topology");

    // ---------- Publisher: N commands through the REAL publish strategy + transport ----------
    var publisherClient = new RaisableServiceBusClient();
    await using var publisherTransport = new AzureServiceBusTransport(
      publisherClient,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { EnableSessions = false },
      NullLogger<AzureServiceBusTransport>.Instance);
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

    var sends = publisherClient.LastSender!.Sent.Count;
    await Assert.That(sends).IsEqualTo(N).Because("one send per command — no fan-out on the publish side");
    await Assert.That(publisherClient.CreatedSenderTopics.Count).IsEqualTo(1)
      .Because("the whole burst rides ONE sender link");
    await Assert.That(publisherClient.CreatedSenderTopics[0]).IsEqualTo(flippedEntity);

    // ---------- Consumer: deliver each broker copy to the ONE subscribed service ----------
    var consumerClient = new RaisableServiceBusClient();
    await using var consumerTransport = new AzureServiceBusTransport(
      consumerClient,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { EnableSessions = false, AutoProvisionInfrastructure = false },
      NullLogger<AzureServiceBusTransport>.Instance);
    var deliveries = 0;
    using var subscription = await consumerTransport.SubscribeAsync(
      (_, _, _) => {
        deliveries++;
        return Task.CompletedTask;
      },
      new TransportDestination(flippedEntity, "svc-orders-inbox-sub"));

    var receiver = new RecordingTransportReceiver();
    foreach (var sent in publisherClient.LastSender.Sent) {
      var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
        body: sent.Body,
        messageId: sent.MessageId,
        properties: sent.ApplicationProperties);
      await consumerClient.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(received, receiver));
    }

    // ---------- The lock ----------
    var settles = receiver.Completed.Count;
    await Assert.That(deliveries).IsEqualTo(N);
    await Assert.That(settles).IsEqualTo(N);
    await Assert.That(receiver.Abandoned).IsEmpty();
    await Assert.That(receiver.DeadLettered).IsEmpty();

    var totalOps = sends + (deliveries * subscriptionsOnEntity) + settles;
    await Assert.That(totalOps).IsEqualTo(3 * N)
      .Because("O(3N): send + deliver + settle, once each — the shared-inbox multiplier is gone");
    await Assert.That(totalOps / (double)N).IsLessThanOrEqualTo(OPS_PER_COMMAND_BOUND)
      .Because("census-derived bound: ops/command ≤ 6 flipped, vs ~42 measured on the shared inbox");
  }
}
