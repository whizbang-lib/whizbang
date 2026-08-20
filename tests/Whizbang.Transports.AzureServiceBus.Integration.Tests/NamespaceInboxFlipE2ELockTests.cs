using System.Text.Json;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// THE PHASE-6 E2E ACCEPTANCE LOCKS (Azure Service Bus tier) — the per-namespace-command-
/// inboxes spec's §test-cases run against the real broker protocol (emulator): entity
/// derivation, single-handler exclusivity with zero non-handler operations, multi-handler
/// fan-out, loud unroutable failure, discard-at-receive-boundary on the new entities,
/// same-stream ordering within a namespace, cross-namespace interleave completeness (the
/// deliberate semantic change; the store-side pump remains the ordering authority — its
/// final-state lock lives at the store/harness tier), and per-namespace DLQ + replay.
/// </summary>
/// <remarks>
/// Entities are pre-provisioned by the emulator's Config.json (the emulator has no admin
/// plane): the flipped inboxes carry ONE subscription per handling service — the topology
/// the phase-5 dark provisioning creates in production. Publishes ride the REAL flip path:
/// OutboxWork → TransportPublishStrategy(+NamespaceOutboxStrategy) → marked destination →
/// transport. Isolation on the fixed entities: unique message ids/markers per test, drains
/// before each broker test, [NotInParallel("ServiceBus")].
/// </remarks>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public sealed class NamespaceInboxFlipE2ELockTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  private const string ORDERS_ENTITY = "inbox.wbtopo.orders.commands";
  private const string BILLING_ENTITY = "inbox.wbtopo.billing.commands";
  private const string FIFO_ENTITY = "inbox.wbtopo.fifo.commands";
  private const string FIFO2_ENTITY = "inbox.wbtopo.fifo2.commands";

  private static readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  // ---------- helpers ----------

  private AzureServiceBusTransport _createTransport(
      bool enableSessions = false, int? maxDeliveryAttempts = null, int? maxConcurrentCalls = null, int? prefetchCount = null) =>
    new(_fixture.Client, _jsonOptions, new AzureServiceBusOptions {
      EnableSessions = enableSessions,
      MaxDeliveryAttempts = maxDeliveryAttempts ?? 10,
      MaxConcurrentCalls = maxConcurrentCalls ?? 200,
      PrefetchCount = prefetchCount ?? 50
    });

  /// <summary>The REAL publish path: TransportPublishStrategy with the flip strategy —
  /// commands resolve to their per-namespace inbox at publish time.</summary>
  private static TransportPublishStrategy _flipPublishStrategy(AzureServiceBusTransport transport) {
    var routingOptions = new RoutingOptions().RouteAllCommandNamespacesToInbox();
    return new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      jsonOptions: null, namespaceRouting: new NamespaceOutboxStrategy(routingOptions));
  }

  /// <summary>Builds the outbox-shaped work item for a command payload (envelope stored as
  /// MessageEnvelope&lt;JsonElement&gt;, exactly how the outbox loads rows for publish).</summary>
  private static OutboxWork _commandWork<TPayload>(TPayload payload, Guid? streamId = null)
      where TPayload : notnull {
    var messageId = Guid.CreateVersion7();
    var payloadElement = JsonSerializer.SerializeToElement(payload, _jsonOptions.GetTypeInfo(typeof(TPayload))!);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = payloadElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "phase6-flip-lock",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
    return new OutboxWork {
      MessageId = messageId,
      Destination = "inbox", // the pre-flip stamp; publish-time resolution is the authority
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<TPayload>).AssemblyQualifiedName!,
      MessageType = typeof(TPayload).AssemblyQualifiedName!,
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  private async Task _drainAsync(string topicName, string subscriptionName) {
    await using var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    for (var i = 0; i < 100; i++) {
      var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(200));
      if (message is null) {
        break;
      }
      await receiver.CompleteMessageAsync(message);
    }
  }

  private async Task _drainSessionAsync(string topicName, string subscriptionName) {
    // Session subscriptions: drain whatever sessions still hold messages (bounded).
    for (var i = 0; i < 10; i++) {
      ServiceBusSessionReceiver receiver;
      try {
        receiver = await _fixture.Client.AcceptNextSessionAsync(
          topicName, subscriptionName, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
      } catch (OperationCanceledException) {
        return; // no session with messages — drained
      } catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.ServiceTimeout) {
        return;
      }
      await using (receiver) {
        while (await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(200)) is { } message) {
          await receiver.CompleteMessageAsync(message);
        }
      }
    }
  }

  private async Task _drainDlqAsync(string topicName, string subscriptionName) {
    await using var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    for (var i = 0; i < 100; i++) {
      var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(200));
      if (message is null) {
        break;
      }
      await receiver.CompleteMessageAsync(message);
    }
  }

  /// <summary>Peeks a subscription's ACTIVE messages (bounded, broker-state read — the
  /// deterministic "zero deliveries buffered here" probe).</summary>
  private async Task<int> _peekActiveCountAsync(string topicName, string subscriptionName) {
    await using var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    var peeked = await receiver.PeekMessagesAsync(100, fromSequenceNumber: 0);
    return peeked.Count;
  }

  // ---------- §routing & delivery ----------

  [Test]
  public async Task CommandType_InboxEntityDerivesFromContractNamespace_PublisherAndSubscriberAgreeAsync() {
    // Derivation lock at broker-tier config fidelity: the strategy-derived entity name IS
    // the pre-provisioned entity the handling service subscribes to.
    var routingOptions = new RoutingOptions().RouteCommandNamespaceToInbox("WbTopo.Orders.Commands");
    var outbox = new NamespaceOutboxStrategy(routingOptions);
    var destination = outbox.GetDestination(
      typeof(WbTopo.Orders.Commands.PlaceOrder),
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      MessageKind.Command);

    await Assert.That(destination.Address).IsEqualTo(ORDERS_ENTITY);

    var inbox = new NamespaceInboxStrategy();
    var context = new InboxSubscriptionContext(
      "svc-orders",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      [new HandledMessageInfo("WbTopo.Orders.Commands.PlaceOrder", "wbtopo.orders.commands", MessageKind.Command)]);
    var ownedInbox = inbox.GetSubscriptions(context)
      .First(s => s.Metadata?.ContainsKey(NamespaceInboxStrategy.OwnedCommandInboxMetadataKey) == true);

    await Assert.That(ownedInbox.Topic).IsEqualTo(destination.Address)
      .Because("publisher and subscriber derive the SAME entity name from the contract namespace");
  }

  [Test]
  public async Task FlippedCommand_SingleHandler_ExactlyTheHandlingServiceReceives_NonHandlerIncursZeroOpsAsync(CancellationToken ct) {
    await _drainAsync(ORDERS_ENTITY, "svc-orders");
    await _drainAsync(BILLING_ENTITY, "svc-billing-a");

    var transport = _createTransport();
    await transport.InitializeAsync(ct);
    var publish = _flipPublishStrategy(transport);

    var work = _commandWork(new WbTopo.Orders.Commands.PlaceOrder($"single-{Guid.CreateVersion7():N}"));
    var handlerAwaiter = new Whizbang.Testing.Transport.MessageIdAwaiter(work.MessageId.ToString());
    using var handlerSubscription = await transport.SubscribeAsync(
      handlerAwaiter.Handler, new TransportDestination(ORDERS_ENTITY, "svc-orders"), ct);

    var nonHandlerDeliveries = 0;
    var nonHandlerTransport = _createTransport();
    await nonHandlerTransport.InitializeAsync(ct);
    using var nonHandlerSubscription = await nonHandlerTransport.SubscribeAsync(
      (_, _, _) => {
        Interlocked.Increment(ref nonHandlerDeliveries);
        return Task.CompletedTask;
      },
      new TransportDestination(BILLING_ENTITY, "svc-billing-a"), ct);

    var result = await publish.PublishAsync(work, ct);
    await Assert.That(result.Success).IsTrue();

    // The handling service receives — this arrival is also the ordering fence proving the
    // broker fully routed the publish before we probe the non-handler's state.
    var received = await handlerAwaiter.WaitAsync(TimeSpan.FromSeconds(30), ct);
    await Assert.That(received).IsEqualTo(work.MessageId.ToString());

    // Zero non-handler operations: no delivery reached its handler AND nothing is buffered
    // on its subscription — the command never existed for that service at the broker.
    await Assert.That(Volatile.Read(ref nonHandlerDeliveries)).IsEqualTo(0);
    await Assert.That(await _peekActiveCountAsync(BILLING_ENTITY, "svc-billing-a")).IsEqualTo(0)
      .Because("a flipped command must incur ZERO broker operations on non-handling services");
  }

  [Test]
  public async Task FlippedCommand_MultiHandlerNamespace_AllHandlingServicesReceiveAsync(CancellationToken ct) {
    await _drainAsync(BILLING_ENTITY, "svc-billing-a");
    await _drainAsync(BILLING_ENTITY, "svc-billing-b");

    var transport = _createTransport();
    await transport.InitializeAsync(ct);
    var publish = _flipPublishStrategy(transport);

    var work = _commandWork(new WbTopo.Billing.Commands.ChargeCard($"multi-{Guid.CreateVersion7():N}"));
    var awaiterA = new Whizbang.Testing.Transport.MessageIdAwaiter(work.MessageId.ToString());
    var awaiterB = new Whizbang.Testing.Transport.MessageIdAwaiter(work.MessageId.ToString());
    using var subscriptionA = await transport.SubscribeAsync(
      awaiterA.Handler, new TransportDestination(BILLING_ENTITY, "svc-billing-a"), ct);
    var transportB = _createTransport();
    await transportB.InitializeAsync(ct);
    using var subscriptionB = await transportB.SubscribeAsync(
      awaiterB.Handler, new TransportDestination(BILLING_ENTITY, "svc-billing-b"), ct);

    var result = await publish.PublishAsync(work, ct);
    await Assert.That(result.Success).IsTrue();

    await Assert.That(await awaiterA.WaitAsync(TimeSpan.FromSeconds(30), ct)).IsEqualTo(work.MessageId.ToString());
    await Assert.That(await awaiterB.WaitAsync(TimeSpan.FromSeconds(30), ct)).IsEqualTo(work.MessageId.ToString())
      .Because("legitimate multi-handler fan-out is preserved: every handling service receives");
  }

  [Test]
  public async Task UnroutableFlippedCommand_NoEntityProvisioned_LoudTypedFailureAtPublishAsync(CancellationToken ct) {
    // WbTopo.Unrouted.Commands has NO entity in Config.json — no subscriber ever
    // provisioned it. The publish must fail LOUDLY with the entity name, never silently
    // drop. (Publishers never create command inbox entities — phase 6's core guarantee.)
    var transport = _createTransport();
    await transport.InitializeAsync(ct);

    var unroutedEntity = "inbox.wbtopo.unrouted.commands";
    var marked = new TransportDestination(
      unroutedEntity,
      "wbtopo.unrouted.commands.lostcommand",
      new Dictionary<string, JsonElement> {
        [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = JsonDocument.Parse("true").RootElement.Clone()
      });
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("unroutable"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = []
    };

    var exception = await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(envelope, marked, cancellationToken: ct));

    await Assert.That(exception!.EntityName).IsEqualTo(unroutedEntity)
      .Because("the typed failure carries the entity name — the operator's provision-or-rollback pointer");
  }

  [Test]
  public async Task MisdeliveredMessage_OnFlippedEntity_DiscardsSafely_NoDlqNoHandlerAsync(CancellationToken ct) {
    // Discard-at-receive-boundary stays the safety belt on the NEW entities: a message
    // whose envelope type has no JSON type info is completed (ack+drop), never
    // dead-lettered, and never reaches the handler.
    await _drainAsync(ORDERS_ENTITY, "svc-orders");
    await _drainDlqAsync(ORDERS_ENTITY, "svc-orders");

    // MaxConcurrentCalls=1 + PrefetchCount=0: strict processing order (the sentinel's
    // arrival PROVES the mis-delivered message settled first) and no outstanding link
    // credit (the emulator hangs its 60s drain timeout on processor close otherwise).
    var transport = _createTransport(maxConcurrentCalls: 1, prefetchCount: 0);
    await transport.InitializeAsync(ct);

    // Sentinel awaiter: the valid message published AFTER the mis-delivered one; its
    // arrival proves the mis-delivered message was already settled — no timing waits.
    var sentinelWork = _commandWork(new WbTopo.Orders.Commands.PlaceOrder($"sentinel-{Guid.CreateVersion7():N}"));
    var handlerInvocations = 0;
    var sentinelAwaiter = new Whizbang.Testing.Transport.MessageIdAwaiter(sentinelWork.MessageId.ToString());
    using var subscription = await transport.SubscribeAsync(
      (envelope, envelopeType, token) => {
        Interlocked.Increment(ref handlerInvocations);
        return sentinelAwaiter.Handler(envelope, envelopeType, token);
      },
      new TransportDestination(ORDERS_ENTITY, "svc-orders"), ct);

    // Mis-delivered: unknown envelope type on the orders inbox.
    var misdeliveredId = Guid.CreateVersion7().ToString();
    await using (var sender = _fixture.Client.CreateSender(ORDERS_ENTITY)) {
      var misdelivered = new ServiceBusMessage("{\"phantom\":true}") {
        MessageId = misdeliveredId,
        ContentType = "application/json"
      };
      misdelivered.ApplicationProperties["EnvelopeType"] = "WbTopo.Nowhere.PhantomCommand, WbTopo.Nowhere";
      await sender.SendMessageAsync(misdelivered, ct);
    }

    var publish = _flipPublishStrategy(transport);
    await publish.PublishAsync(sentinelWork, ct);
    await sentinelAwaiter.WaitAsync(TimeSpan.FromSeconds(30), ct);

    await Assert.That(Volatile.Read(ref handlerInvocations)).IsEqualTo(1)
      .Because("only the sentinel reaches the handler — the mis-delivered message is dropped at the receive boundary");

    // Safely discarded = completed at the broker, NOT dead-lettered. PEEK (credit-free) —
    // a timed-out receive would leave link credit and hang the emulator's drain on close.
    await using var dlqReceiver = _fixture.Client.CreateReceiver(ORDERS_ENTITY, "svc-orders",
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    var deadLettered = await dlqReceiver.PeekMessageAsync(fromSequenceNumber: 0, ct);
    await Assert.That(deadLettered).IsNull()
      .Because("ack+drop must not dead-letter — mis-delivery is discard, not poison");
  }

  // ---------- §ordering ----------

  [Test]
  public async Task SameStreamSameNamespace_StrictWireOrderPreservedAsync(CancellationToken ct) {
    await _drainSessionAsync(FIFO_ENTITY, "svc-fifo");

    const int count = 20;
    var streamId = Guid.CreateVersion7();
    var marker = $"fifo-{Guid.CreateVersion7():N}";

    var transport = _createTransport(enableSessions: true);
    await transport.InitializeAsync(ct);
    var publish = _flipPublishStrategy(transport);

    var receivedSequences = new List<int>();
    var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = await transport.SubscribeAsync(
      (envelope, _, _) => {
        if (envelope is MessageEnvelope<WbTopo.Fifo.Commands.FifoStep> step && step.Payload.Marker == marker) {
          lock (receivedSequences) {
            receivedSequences.Add(step.Payload.Sequence);
            if (receivedSequences.Count == count) {
              allReceived.TrySetResult();
            }
          }
        }
        return Task.CompletedTask;
      },
      new TransportDestination(FIFO_ENTITY, "svc-fifo"), ct);

    for (var i = 0; i < count; i++) {
      var result = await publish.PublishAsync(
        _commandWork(new WbTopo.Fifo.Commands.FifoStep(marker, i), streamId), ct);
      await Assert.That(result.Success).IsTrue();
    }

    await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    lock (receivedSequences) {
      for (var i = 0; i < count; i++) {
        if (receivedSequences[i] != i) {
          Assert.Fail($"Wire order broken within one namespace: position {i} carried sequence {receivedSequences[i]} — sessions must preserve same-stream order on the flipped entity.");
        }
      }
    }
  }

  [Test]
  public async Task SameStreamAcrossTwoNamespaces_InterleavedPublish_AllArrive_PerNamespaceOrderPreservedAsync(CancellationToken ct) {
    // THE DELIBERATE SEMANTIC CHANGE: one stream's commands in two namespaces ride two
    // entities and may interleave IN WIRE ORDER across them. The lock here: nothing is
    // lost and per-namespace order holds. The store-side work pump remains the ordering
    // authority (per-stream claim + ordering guard re-serialize regardless of arrival
    // order) — its final-state lock lives in the MultiService/store-tier suite.
    await _drainSessionAsync(FIFO_ENTITY, "svc-fifo");
    await _drainSessionAsync(FIFO2_ENTITY, "svc-fifo");

    const int perNamespace = 10;
    var streamId = Guid.CreateVersion7();
    var marker = $"interleave-{Guid.CreateVersion7():N}";

    var transport = _createTransport(enableSessions: true);
    await transport.InitializeAsync(ct);
    var publish = _flipPublishStrategy(transport);

    var fifoSequences = new List<int>();
    var fifo2Sequences = new List<int>();
    var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    void _record(List<int> sink, int sequence) {
      lock (allReceived) {
        sink.Add(sequence);
        if (fifoSequences.Count == perNamespace && fifo2Sequences.Count == perNamespace) {
          allReceived.TrySetResult();
        }
      }
    }

    using var subscriptionA = await transport.SubscribeAsync(
      (envelope, _, _) => {
        if (envelope is MessageEnvelope<WbTopo.Fifo.Commands.FifoStep> step && step.Payload.Marker == marker) {
          _record(fifoSequences, step.Payload.Sequence);
        }
        return Task.CompletedTask;
      },
      new TransportDestination(FIFO_ENTITY, "svc-fifo"), ct);
    var transport2 = _createTransport(enableSessions: true);
    await transport2.InitializeAsync(ct);
    using var subscriptionB = await transport2.SubscribeAsync(
      (envelope, _, _) => {
        if (envelope is MessageEnvelope<WbTopo.Fifo2.Commands.Fifo2Step> step && step.Payload.Marker == marker) {
          _record(fifo2Sequences, step.Payload.Sequence);
        }
        return Task.CompletedTask;
      },
      new TransportDestination(FIFO2_ENTITY, "svc-fifo"), ct);

    // Interleave at publish: A0 B0 A1 B1 … — same stream, two namespaces, two entities.
    for (var i = 0; i < perNamespace; i++) {
      await publish.PublishAsync(_commandWork(new WbTopo.Fifo.Commands.FifoStep(marker, i), streamId), ct);
      await publish.PublishAsync(_commandWork(new WbTopo.Fifo2.Commands.Fifo2Step(marker, i), streamId), ct);
    }

    await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
    List<int> fifoSnapshot;
    List<int> fifo2Snapshot;
    lock (allReceived) {
      fifoSnapshot = [.. fifoSequences];
      fifo2Snapshot = [.. fifo2Sequences];
    }

    await Assert.That(fifoSnapshot.Count).IsEqualTo(perNamespace).Because("no loss across the split");
    await Assert.That(fifo2Snapshot.Count).IsEqualTo(perNamespace).Because("no loss across the split");
    for (var i = 0; i < perNamespace; i++) {
      await Assert.That(fifoSnapshot[i]).IsEqualTo(i).Because("within one namespace, ordering is unchanged");
      await Assert.That(fifo2Snapshot[i]).IsEqualTo(i).Because("within one namespace, ordering is unchanged");
    }
  }

  // ---------- §DLQ & recovery ----------

  [Test]
  public async Task FlippedNamespaceFailures_LandInThatNamespacesDlq_AndReplayFromTheNewEntityAsync(CancellationToken ct) {
    // Failures on a flipped namespace land in THE FLIPPED ENTITY's per-subscription DLQ
    // (per contract area — the untangling the proposal promises), and dead-letter recovery
    // replays from the new entity. DeliveryCount shape per the spike: the transport
    // dead-letters at MaxDeliveryAttempts=2 (below the broker's MaxDeliveryCount=3), so
    // DeliveryCount on the dead-lettered message is 2.
    await _drainAsync(ORDERS_ENTITY, "svc-orders");
    await _drainDlqAsync(ORDERS_ENTITY, "svc-orders");

    var failureMarker = $"dlq-{Guid.CreateVersion7():N}";
    var transport = _createTransport(maxDeliveryAttempts: 2);
    await transport.InitializeAsync(ct);
    var publish = _flipPublishStrategy(transport);

    var poisoned = true;
    var replayAwaiter = new Whizbang.Testing.Transport.SignalAwaiter();
    using var subscription = await transport.SubscribeAsync(
      (envelope, _, _) => {
        if (envelope is MessageEnvelope<WbTopo.Orders.Commands.PlaceOrder> order
            && order.Payload.Marker == failureMarker) {
          if (Volatile.Read(ref poisoned)) {
            throw new InvalidOperationException(failureMarker);
          }
          replayAwaiter.Signal();
        }
        return Task.CompletedTask;
      },
      new TransportDestination(ORDERS_ENTITY, "svc-orders"), ct);

    var work = _commandWork(new WbTopo.Orders.Commands.PlaceOrder(failureMarker));
    await publish.PublishAsync(work, ct);

    // DLQ arrival IS the completion signal for abandon → redeliver → dead-letter.
    ServiceBusReceivedMessage? deadLettered = null;
    await using (var dlqReceiver = _fixture.Client.CreateReceiver(ORDERS_ENTITY, "svc-orders",
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter })) {
      var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
      while (deadLettered is null && DateTimeOffset.UtcNow < deadline) {
        var candidate = await dlqReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), ct);
        if (candidate is null) {
          continue;
        }
        if (candidate.MessageId == work.MessageId.ToString()) {
          deadLettered = candidate;
          await Assert.That(candidate.DeadLetterReason).IsEqualTo("MaxDeliveryAttemptsExceeded");
          await Assert.That(candidate.DeliveryCount).IsEqualTo(2)
            .Because("the transport dead-letters on the delivery that reaches MaxDeliveryAttempts (spike-verified count shape)");

          // REPLAY from the new entity: cure the handler, republish the dead-lettered
          // body to the SAME flipped entity, complete the DLQ copy.
          Volatile.Write(ref poisoned, false);
          await using (var replaySender = _fixture.Client.CreateSender(ORDERS_ENTITY)) {
            var replayMessage = new ServiceBusMessage(candidate.Body) {
              MessageId = candidate.MessageId,
              ContentType = candidate.ContentType,
              Subject = candidate.Subject
            };
            foreach (var (key, value) in candidate.ApplicationProperties) {
              replayMessage.ApplicationProperties[key] = value;
            }
            await replaySender.SendMessageAsync(replayMessage, ct);
          }
          await dlqReceiver.CompleteMessageAsync(candidate, ct);
        } else {
          await dlqReceiver.CompleteMessageAsync(candidate, ct); // stale residue — drop
        }
      }
    }

    await Assert.That(deadLettered).IsNotNull()
      .Because("failures on a flipped namespace land in that namespace's DLQ");
    await replayAwaiter.WaitAsync(TimeSpan.FromSeconds(30), ct);
  }
}
