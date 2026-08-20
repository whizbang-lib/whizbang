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
/// THE PHASE-7 RETIREMENT E2E LOCK (Azure Service Bus tier): full flip + shared-inbox
/// retirement through the REAL strategies, publish path, and transport — a domain command
/// rides its per-namespace inbox, a framework-reserved (system) command rides the system
/// broadcast inbox (the sole carve-out), and the legacy shared topic incurs ZERO broker
/// operations: no sender, no processor, no delivery — asserted on the recording doubles,
/// not by absence of processing.
/// </summary>
public class AsbSharedInboxRetirementE2ELockTests {
  private const string HANDLED_NAMESPACE = "myapp.orders.commands";
  private const string SHARED_INBOX = "inbox";
  private const string SERVICE_NAME = "svc-orders";

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
  public async Task Retirement_CommandToNamespaceInbox_SystemToBroadcast_SharedTopicZeroBrokerOpsAsync() {
    // ---------- Topology: the REAL retirement subscription set ----------
    var routingOptions = new RoutingOptions().RouteAllCommandNamespacesToInbox().RetireSharedInbox();
    var inboxStrategy = new NamespaceInboxStrategy(routingOptions);
    var subscriptions = inboxStrategy.GetSubscriptions(new InboxSubscriptionContext(
      SERVICE_NAME,
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      [new HandledMessageInfo("MyApp.Orders.Commands.PlaceOrderCommand", HANDLED_NAMESPACE, MessageKind.Command)]));
    var flippedEntity = CommandInboxNaming.TopicFor(HANDLED_NAMESPACE);

    // ---------- Consumer: subscribe the FULL retirement set through the real transport ----------
    var consumerClient = new RaisableServiceBusClient();
    await using var consumerTransport = new AzureServiceBusTransport(
      consumerClient,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { EnableSessions = false, AutoProvisionInfrastructure = false },
      NullLogger<AzureServiceBusTransport>.Instance);

    var deliveriesByTopic = new Dictionary<string, int>(StringComparer.Ordinal);
    var subscriptionHandles = new List<IDisposable>();
    try {
      foreach (var subscription in subscriptions) {
        var topic = subscription.Topic;
        subscriptionHandles.Add(await consumerTransport.SubscribeAsync(
          (_, _, _) => {
            lock (deliveriesByTopic) {
              deliveriesByTopic[topic] = deliveriesByTopic.GetValueOrDefault(topic) + 1;
            }
            return Task.CompletedTask;
          },
          new TransportDestination(topic, $"{SERVICE_NAME}-{topic}")));
      }

      var processorTopics = consumerClient.CreatedProcessors.Select(p => p.Topic).ToList();
      await Assert.That(processorTopics).Contains(flippedEntity);
      await Assert.That(processorTopics).Contains(CommandInboxNaming.SystemBroadcastTopic);
      await Assert.That(processorTopics).DoesNotContain(SHARED_INBOX)
        .Because("under retirement the consumer holds NO receive link on the legacy shared topic");

      // ---------- Publisher: the REAL flip publish path ----------
      var publisherClient = new RaisableServiceBusClient();
      await using var publisherTransport = new AzureServiceBusTransport(
        publisherClient,
        AsbTransportTestData.CombinedOptions,
        new AzureServiceBusOptions { EnableSessions = false },
        NullLogger<AzureServiceBusTransport>.Instance);
      var publishStrategy = new TransportPublishStrategy(
        publisherTransport,
        new DefaultTransportReadinessCheck(),
        SHARED_INBOX,
        namespaceRouting: new NamespaceOutboxStrategy(routingOptions));

      var domainResult = await publishStrategy.PublishAsync(
        _commandWork("MyApp.Orders.Commands.PlaceOrderCommand, MyApp", "domain-command"),
        CancellationToken.None);
      var domainSent = publisherClient.LastSender!.Sent.Single();
      var systemResult = await publishStrategy.PublishAsync(
        _commandWork("Whizbang.Core.Commands.System.RebuildPerspectiveCommand, Whizbang.Core", "system-command"),
        CancellationToken.None);
      var systemSent = publisherClient.LastSender!.Sent.Single();

      await Assert.That(domainResult.Success).IsTrue();
      await Assert.That(systemResult.Success).IsTrue();
      await Assert.That(publisherClient.CreatedSenderTopics).Contains(flippedEntity);
      await Assert.That(publisherClient.CreatedSenderTopics).Contains(CommandInboxNaming.SystemBroadcastTopic)
        .Because("durable system commands broadcast on inbox.whizbang — the sole carve-out");
      await Assert.That(publisherClient.CreatedSenderTopics).DoesNotContain(SHARED_INBOX)
        .Because("ZERO publish-side broker operations on the legacy shared topic under retirement");

      // ---------- Delivery: each sent message reaches its entity's processor ----------
      var receiver = new RecordingTransportReceiver();
      foreach (var (senderTopic, sent) in new[] {
        (flippedEntity, domainSent),
        (CommandInboxNaming.SystemBroadcastTopic, systemSent)
      }) {
        var processor = consumerClient.CreatedProcessors.Single(p => p.Topic == senderTopic).Processor;
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
          body: sent.Body,
          messageId: sent.MessageId,
          properties: sent.ApplicationProperties);
        await processor.RaiseMessageAsync(AsbTransportTestData.MessageArgs(received, receiver));
      }

      await Assert.That(deliveriesByTopic.GetValueOrDefault(flippedEntity)).IsEqualTo(1)
        .Because("the domain command arrives on its per-namespace inbox");
      await Assert.That(deliveriesByTopic.GetValueOrDefault(CommandInboxNaming.SystemBroadcastTopic)).IsEqualTo(1)
        .Because("the system command arrives on the broadcast inbox");
      await Assert.That(deliveriesByTopic.ContainsKey(SHARED_INBOX)).IsFalse()
        .Because("NOTHING lands on (or requires) the legacy shared topic — the retirement end-state");
    } finally {
      foreach (var handle in subscriptionHandles) {
        handle.Dispose();
      }
    }
  }
}
