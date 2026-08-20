using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
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
using Whizbang.Testing.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// THE PHASE-6 E2E ACCEPTANCE LOCKS (RabbitMQ tier) — the per-namespace-command-inboxes
/// spec's §test-cases against a real broker: entity derivation, single-handler exclusivity
/// with a zero-message non-handler queue, multi-handler fan-out, loud unroutable failure,
/// discard-at-receive-boundary on the new entities, same-stream ordering within a namespace,
/// cross-namespace interleave completeness (the deliberate semantic change; the store-side
/// pump remains the ordering authority — its final-state lock lives at the store/harness
/// tier), and per-namespace DLQ + replay.
/// </summary>
/// <remarks>
/// Exchanges are namespace-derived (fixed); isolation on the shared broker comes from
/// unique queue names (per-test SubscriberName), unique message markers/ids, and
/// [NotInParallel("RabbitMQ")]. Publishes ride the REAL flip path: OutboxWork →
/// TransportPublishStrategy(+NamespaceOutboxStrategy) → marked destination → transport
/// (passive existence probe — publishers never declare command inbox exchanges).
/// </remarks>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public sealed class NamespaceInboxFlipE2ELockTests : IAsyncDisposable {
  private const string ORDERS_ENTITY = "inbox.wbtopo.orders.commands";
  private const string BILLING_ENTITY = "inbox.wbtopo.billing.commands";
  private const string FIFO_ENTITY = "inbox.wbtopo.fifo.commands";
  private const string FIFO2_ENTITY = "inbox.wbtopo.fifo2.commands";

  private static readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  private IConnection? _connection;
  private readonly List<IAsyncDisposable> _disposables = [];

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedRabbitMqContainer.InitializeOrSkipAsync();
    var factory = new ConnectionFactory { Uri = new Uri(SharedRabbitMqContainer.ConnectionString) };
    _connection = await factory.CreateConnectionAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() => await DisposeAsync();

  public async ValueTask DisposeAsync() {
    foreach (var disposable in _disposables) {
      try {
        await disposable.DisposeAsync();
      } catch {
        // Best-effort teardown — a torn-down broker connection must not fail the test run.
      }
    }
    _disposables.Clear();
    try {
      if (_connection is not null) {
        await _connection.CloseAsync();
        _connection.Dispose();
      }
    } catch {
      // Ignore close races on shutdown.
    }
    _connection = null;
  }

  // ---------- helpers ----------

  private async Task<RabbitMQTransport> _createTransportAsync(
      RabbitMQOptions? options = null, Whizbang.Core.Routing.IMessageDiscardPolicy? discardPolicy = null) {
    var pool = new RabbitMQChannelPool(_connection!, maxChannels: 5);
    var transport = new RabbitMQTransport(
      _connection!, _jsonOptions, pool, options ?? new RabbitMQOptions(), logger: null, discardPolicy);
    await transport.InitializeAsync();
    _disposables.Add(transport);
    return transport;
  }

  /// <summary>The REAL publish path: TransportPublishStrategy with the flip strategy —
  /// commands resolve to their per-namespace inbox at publish time.</summary>
  private static TransportPublishStrategy _flipPublishStrategy(RabbitMQTransport transport) {
    var routingOptions = new RoutingOptions().RouteAllCommandNamespacesToInbox();
    return new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      namespaceRouting: new NamespaceOutboxStrategy(routingOptions));
  }

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

  private static TransportDestination _subscribeDestination(string exchange, string subscriberName) =>
    new(exchange, "#", new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse($"\"{subscriberName}\"").RootElement.Clone()
    });

  private static string _uniqueService(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

  // ---------- §routing & delivery ----------

  [Test]
  [Timeout(90000)]
  public async Task CommandType_InboxEntityDerivesFromContractNamespace_PublisherAndSubscriberAgreeAsync(CancellationToken ct) {
    _ = ct; // strategy-level test — no broker I/O to cancel
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
  [Timeout(90000)]
  public async Task FlippedCommand_SingleHandler_ExactlyTheHandlingServiceReceives_NonHandlerQueueStaysEmptyAsync(CancellationToken ct) {
    var transport = await _createTransportAsync();
    var handlerService = _uniqueService("svc-orders");
    var idleService = _uniqueService("svc-billing");

    // The handling service subscribes (dark-provisions its exchange+queue+binding).
    var work = _commandWork(new WbTopo.Orders.Commands.PlaceOrder($"single-{Guid.CreateVersion7():N}"));
    var handlerAwaiter = new Whizbang.Testing.Transport.MessageIdAwaiter(work.MessageId.ToString());
    var handlerSubscription = await transport.SubscribeAsync(
      handlerAwaiter.Handler, _subscribeDestination(ORDERS_ENTITY, handlerService), ct);

    // A NON-handling service provisioned-but-idle on ITS OWN namespace: exchange + queue +
    // binding exist, NO consumer — its queue depth after the publish is the broker-op probe.
    await using (var provisionChannel = await _connection!.CreateChannelAsync(cancellationToken: ct)) {
      await provisionChannel.ExchangeDeclareAsync(BILLING_ENTITY, "topic", durable: true, autoDelete: false, cancellationToken: ct);
      await provisionChannel.QueueDeclareAsync($"{idleService}-{BILLING_ENTITY}", durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: ct);
      await provisionChannel.QueueBindAsync($"{idleService}-{BILLING_ENTITY}", BILLING_ENTITY, "#", cancellationToken: ct);
    }

    try {
      var publish = _flipPublishStrategy(transport);
      var result = await publish.PublishAsync(work, ct);
      await Assert.That(result.Success).IsTrue();

      var received = await handlerAwaiter.WaitAsync(TimeSpan.FromSeconds(15), ct);
      await Assert.That(received).IsEqualTo(work.MessageId.ToString());

      // Zero broker operations for the non-handler: its queue never saw the message.
      await using var probeChannel = await _connection!.CreateChannelAsync(cancellationToken: ct);
      var depth = await probeChannel.MessageCountAsync($"{idleService}-{BILLING_ENTITY}", ct);
      await Assert.That(depth).IsEqualTo(0u)
        .Because("a flipped command must incur ZERO broker operations on non-handling services — no copy, no receive, no settle");
    } finally {
      handlerSubscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task FlippedCommand_MultiHandlerNamespace_AllHandlingServicesReceiveAsync(CancellationToken ct) {
    var transport = await _createTransportAsync();
    var work = _commandWork(new WbTopo.Billing.Commands.ChargeCard($"multi-{Guid.CreateVersion7():N}"));

    var awaiterA = new Whizbang.Testing.Transport.MessageIdAwaiter(work.MessageId.ToString());
    var awaiterB = new Whizbang.Testing.Transport.MessageIdAwaiter(work.MessageId.ToString());
    var subscriptionA = await transport.SubscribeAsync(
      awaiterA.Handler, _subscribeDestination(BILLING_ENTITY, _uniqueService("svc-billing-a")), ct);
    var subscriptionB = await transport.SubscribeAsync(
      awaiterB.Handler, _subscribeDestination(BILLING_ENTITY, _uniqueService("svc-billing-b")), ct);

    try {
      var publish = _flipPublishStrategy(transport);
      await publish.PublishAsync(work, ct);

      await Assert.That(await awaiterA.WaitAsync(TimeSpan.FromSeconds(15), ct)).IsEqualTo(work.MessageId.ToString());
      await Assert.That(await awaiterB.WaitAsync(TimeSpan.FromSeconds(15), ct)).IsEqualTo(work.MessageId.ToString())
        .Because("legitimate multi-handler fan-out is preserved: every handling service receives");
    } finally {
      subscriptionA.Dispose();
      subscriptionB.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task UnroutableFlippedCommand_NoExchangeProvisioned_LoudTypedFailureAtPublishAsync(CancellationToken ct) {
    // A namespace nobody EVER provisioned (unique per run — never declared on this broker).
    var transport = await _createTransportAsync();
    var unroutedEntity = $"inbox.wbtopo.unrouted{Guid.NewGuid():N}.commands";
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

    // Never silently created: the passive probe must not have declared the exchange.
    await using var probeChannel = await _connection!.CreateChannelAsync(cancellationToken: ct);
    await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
      await probeChannel.ExchangeDeclarePassiveAsync(unroutedEntity, ct));
  }

  [Test]
  [Timeout(90000)]
  public async Task MisdeliveredMessage_OnFlippedEntity_DiscardedAtReceiveBoundary_NoDlqNoHandlerAsync(CancellationToken ct) {
    // Discard-at-receive-boundary stays the safety belt on the NEW entities: a deliverable,
    // deserializable message whose type this service does NOT consume is acked+dropped —
    // never dead-lettered, never handled. The discard policy consults the receptor registry;
    // this stub consumes PlaceOrder (the sentinel) and nothing else, so the mis-delivered
    // ChargeCard on the orders exchange is exactly a mis-delivery.
    var discardPolicy = new MessageDiscardPolicy(
      new ConsumesOnlyPlaceOrderRegistry(),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageDiscardPolicy>.Instance,
      new System.Diagnostics.Metrics.Meter($"phase6-discard-{Guid.NewGuid():N}"));
    var transport = await _createTransportAsync(discardPolicy: discardPolicy);
    var handlerService = _uniqueService("svc-orders");

    var sentinelWork = _commandWork(new WbTopo.Orders.Commands.PlaceOrder($"sentinel-{Guid.CreateVersion7():N}"));
    var handlerInvocations = 0;
    var sentinelAwaiter = new Whizbang.Testing.Transport.MessageIdAwaiter(sentinelWork.MessageId.ToString());
    var subscription = await transport.SubscribeAsync(
      (envelope, envelopeType, token) => {
        Interlocked.Increment(ref handlerInvocations);
        return sentinelAwaiter.Handler(envelope, envelopeType, token);
      },
      _subscribeDestination(ORDERS_ENTITY, handlerService), ct);

    try {
      var publish = _flipPublishStrategy(transport);

      // Mis-delivered FIRST: a ChargeCard envelope straight onto the ORDERS exchange.
      var misdelivered = _commandWork(new WbTopo.Billing.Commands.ChargeCard("misdelivered"));
      await transport.PublishAsync(
        misdelivered.Envelope, new TransportDestination(ORDERS_ENTITY, "wbtopo.orders.commands.misdelivered"),
        misdelivered.EnvelopeType, cancellationToken: ct);

      // Then the sentinel; single consumer on one queue → strict order → the sentinel's
      // arrival proves the mis-delivered message was already settled.
      await publish.PublishAsync(sentinelWork, ct);
      await sentinelAwaiter.WaitAsync(TimeSpan.FromSeconds(15), ct);

      await Assert.That(Volatile.Read(ref handlerInvocations)).IsEqualTo(1)
        .Because("only the sentinel reaches the handler — the mis-delivered message is dropped at the receive boundary");

      // Safely discarded = acked at the broker, NOT dead-lettered.
      await using var probeChannel = await _connection!.CreateChannelAsync(cancellationToken: ct);
      var dlqDepth = await probeChannel.MessageCountAsync($"{handlerService}-{ORDERS_ENTITY}.dlq", ct);
      await Assert.That(dlqDepth).IsEqualTo(0u)
        .Because("ack+drop must not dead-letter — mis-delivery is discard, not poison");
    } finally {
      subscription.Dispose();
    }
  }

  // ---------- §ordering ----------

  [Test]
  [Timeout(90000)]
  public async Task SameStreamSameNamespace_StrictWireOrderPreservedAsync(CancellationToken ct) {
    const int count = 20;
    var streamId = Guid.CreateVersion7();
    var marker = $"fifo-{Guid.CreateVersion7():N}";
    var transport = await _createTransportAsync(new RabbitMQOptions { EnableSingleActiveConsumer = true });

    var receivedSequences = new List<int>();
    var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var subscription = await transport.SubscribeAsync(
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
      _subscribeDestination(FIFO_ENTITY, _uniqueService("svc-fifo")), ct);

    try {
      var publish = _flipPublishStrategy(transport);
      for (var i = 0; i < count; i++) {
        var result = await publish.PublishAsync(
          _commandWork(new WbTopo.Fifo.Commands.FifoStep(marker, i), streamId), ct);
        await Assert.That(result.Success).IsTrue();
      }

      await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
      List<int> snapshot;
      lock (receivedSequences) {
        snapshot = [.. receivedSequences];
      }
      for (var i = 0; i < count; i++) {
        await Assert.That(snapshot[i]).IsEqualTo(i)
          .Because("same stream + same namespace = strict wire order on the flipped entity (single-active-consumer queue)");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task SameStreamAcrossTwoNamespaces_InterleavedPublish_AllArrive_PerNamespaceOrderPreservedAsync(CancellationToken ct) {
    // THE DELIBERATE SEMANTIC CHANGE: one stream's commands in two namespaces ride two
    // exchanges and may interleave IN WIRE ORDER across them. The lock: nothing is lost
    // and per-namespace order holds. The store-side work pump remains the ordering
    // authority — its final-state lock lives in the MultiService/store-tier suite.
    const int perNamespace = 10;
    var streamId = Guid.CreateVersion7();
    var marker = $"interleave-{Guid.CreateVersion7():N}";
    var transport = await _createTransportAsync(new RabbitMQOptions { EnableSingleActiveConsumer = true });

    var fifoSequences = new List<int>();
    var fifo2Sequences = new List<int>();
    var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var gate = new Lock();

    void _record(List<int> sink, int sequence) {
      lock (gate) {
        sink.Add(sequence);
        if (fifoSequences.Count == perNamespace && fifo2Sequences.Count == perNamespace) {
          allReceived.TrySetResult();
        }
      }
    }

    var subscriptionA = await transport.SubscribeAsync(
      (envelope, _, _) => {
        if (envelope is MessageEnvelope<WbTopo.Fifo.Commands.FifoStep> step && step.Payload.Marker == marker) {
          _record(fifoSequences, step.Payload.Sequence);
        }
        return Task.CompletedTask;
      },
      _subscribeDestination(FIFO_ENTITY, _uniqueService("svc-fifo")), ct);
    var subscriptionB = await transport.SubscribeAsync(
      (envelope, _, _) => {
        if (envelope is MessageEnvelope<WbTopo.Fifo2.Commands.Fifo2Step> step && step.Payload.Marker == marker) {
          _record(fifo2Sequences, step.Payload.Sequence);
        }
        return Task.CompletedTask;
      },
      _subscribeDestination(FIFO2_ENTITY, _uniqueService("svc-fifo2")), ct);

    try {
      var publish = _flipPublishStrategy(transport);
      for (var i = 0; i < perNamespace; i++) {
        await publish.PublishAsync(_commandWork(new WbTopo.Fifo.Commands.FifoStep(marker, i), streamId), ct);
        await publish.PublishAsync(_commandWork(new WbTopo.Fifo2.Commands.Fifo2Step(marker, i), streamId), ct);
      }

      await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
      List<int> fifoSnapshot;
      List<int> fifo2Snapshot;
      lock (gate) {
        fifoSnapshot = [.. fifoSequences];
        fifo2Snapshot = [.. fifo2Sequences];
      }

      await Assert.That(fifoSnapshot.Count).IsEqualTo(perNamespace).Because("no loss across the split");
      await Assert.That(fifo2Snapshot.Count).IsEqualTo(perNamespace).Because("no loss across the split");
      for (var i = 0; i < perNamespace; i++) {
        await Assert.That(fifoSnapshot[i]).IsEqualTo(i).Because("within one namespace, ordering is unchanged");
        await Assert.That(fifo2Snapshot[i]).IsEqualTo(i).Because("within one namespace, ordering is unchanged");
      }
    } finally {
      subscriptionA.Dispose();
      subscriptionB.Dispose();
    }
  }

  // ---------- §DLQ & recovery ----------

  [Test]
  [Timeout(90000)]
  public async Task FlippedNamespaceFailures_LandInThatNamespacesDlq_AndReplayFromTheNewEntityAsync(CancellationToken ct) {
    // Failures on a flipped namespace land in THE FLIPPED QUEUE's DLQ ({queue}.dlq via
    // {exchange}.dlx — per contract area), and dead-letter recovery replays from the new
    // entity. MaxDeliveryAttempts=2: first delivery nacks+requeues, second dead-letters.
    var failureMarker = $"dlq-{Guid.CreateVersion7():N}";
    var handlerService = _uniqueService("svc-orders");
    var transport = await _createTransportAsync(new RabbitMQOptions { MaxDeliveryAttempts = 2 });

    var poisoned = true;
    var replayAwaiter = new Whizbang.Testing.Transport.SignalAwaiter();
    var subscription = await transport.SubscribeAsync(
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
      _subscribeDestination(ORDERS_ENTITY, handlerService), ct);

    try {
      var publish = _flipPublishStrategy(transport);
      var work = _commandWork(new WbTopo.Orders.Commands.PlaceOrder(failureMarker));
      await publish.PublishAsync(work, ct);

      // DLQ arrival IS the completion signal for nack → redeliver → dead-letter — consumed
      // via a dedicated DLQ consumer (signal-based, no polling).
      var dlqQueue = $"{handlerService}-{ORDERS_ENTITY}.dlq";
      await using var dlqChannel = await _connection!.CreateChannelAsync(cancellationToken: ct);
      var deadLetteredTcs = new TaskCompletionSource<(ulong DeliveryTag, IReadOnlyBasicProperties Properties, byte[] Body)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      var dlqConsumer = new AsyncEventingBasicConsumer(dlqChannel);
      dlqConsumer.ReceivedAsync += (_, args) => {
        deadLetteredTcs.TrySetResult((args.DeliveryTag, args.BasicProperties, args.Body.ToArray()));
        return Task.CompletedTask;
      };
      await dlqChannel.BasicConsumeAsync(dlqQueue, autoAck: false, consumer: dlqConsumer, cancellationToken: ct);

      var deadLettered = await deadLetteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

      // REPLAY from the new entity: cure the handler, republish the dead-lettered body to
      // the SAME flipped exchange, ack the DLQ copy.
      Volatile.Write(ref poisoned, false);
      var replayProperties = new BasicProperties {
        MessageId = deadLettered.Properties.MessageId,
        ContentType = deadLettered.Properties.ContentType,
        Persistent = true,
        Headers = deadLettered.Properties.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value)
      };
      await dlqChannel.BasicPublishAsync(
        ORDERS_ENTITY, "wbtopo.orders.commands.placeorder", mandatory: false,
        basicProperties: replayProperties, body: deadLettered.Body, cancellationToken: ct);
      await dlqChannel.BasicAckAsync(deadLettered.DeliveryTag, multiple: false, ct);

      await replayAwaiter.WaitAsync(TimeSpan.FromSeconds(15), ct);
    } finally {
      subscription.Dispose();
    }
  }

  /// <summary>Registry stub for the discard lock: this service consumes PlaceOrder envelopes
  /// only — anything else on its inbox is a mis-delivery the receive boundary must drop.</summary>
  private sealed class ConsumesOnlyPlaceOrderRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => _consumes(messageType);
    public bool HasInboxHandler(string messageType) => _consumes(messageType);
    public bool HasAnyConsumer(string messageType) => _consumes(messageType);
    private static bool _consumes(string messageType) =>
      messageType.Contains("PlaceOrder", StringComparison.Ordinal);
  }
}
