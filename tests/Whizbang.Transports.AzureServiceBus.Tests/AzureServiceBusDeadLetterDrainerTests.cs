using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit coverage for <see cref="AzureServiceBusDeadLetterDrainer"/> — construction guards,
/// the drain loop's import→complete/abandon settlement semantics, and the internal
/// <c>TryBuildImport</c> broker-message → custody-record mapping (raw body, no deserialization).
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusDeadLetterDrainer.cs</code-under-test>
public class AzureServiceBusDeadLetterDrainerTests {

  private static readonly string _id1 = "00000000-0000-0000-0000-000000000001";
  private static readonly string _id2 = "00000000-0000-0000-0000-000000000002";

  private static Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> _noopImport =>
    (_, _) => Task.FromResult(true);

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullClient_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client: null!, topicName: "t", subscriptionName: "s", importAsync: _noopImport,
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_EmptyTopic_ThrowsArgumentExceptionAsync() {
    var client = new FakeDrainClient();
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client, topicName: "", subscriptionName: "s", importAsync: _noopImport,
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_EmptySubscription_ThrowsArgumentExceptionAsync() {
    var client = new FakeDrainClient();
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client, topicName: "t", subscriptionName: " ", importAsync: _noopImport,
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_NullImporter_ThrowsArgumentNullExceptionAsync() {
    var client = new FakeDrainClient();
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client, topicName: "t", subscriptionName: "s", importAsync: null!,
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TransportName_FormatsAsAsbTopicSubAsync() {
    var client = new FakeDrainClient();
    await using var drainer = new AzureServiceBusDeadLetterDrainer(
      client, "orders", "billing", _noopImport, NullLogger<AzureServiceBusDeadLetterDrainer>.Instance);

    await Assert.That(drainer.TransportName).IsEqualTo("asb:orders/billing");
  }

  // ===== TryBuildImport — broker message → custody record mapping =====

  [Test]
  public async Task TryBuildImport_WhizbangMessage_MapsEveryFieldWithoutDeserializingAsync() {
    var enqueued = DateTimeOffset.UtcNow.AddDays(-2);
    var msg = ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString("""{"v":1,"p":{"x":1}}"""),
      messageId: _id1,
      sessionId: _id2,
      deliveryCount: 10,
      enqueuedTime: enqueued,
      properties: new Dictionary<string, object> {
        ["EnvelopeType"] = "Whizbang.Test.Envelope",
        ["DeadLetterReason"] = "MaxDeliveryAttemptsExceeded",
        ["DeadLetterErrorDescription"] = "JsonTypeInfo metadata for type X was not provided",
      });

    var ok = AzureServiceBusDeadLetterDrainer.TryBuildImport(msg, "orders", "billing", out var import);

    await Assert.That(ok).IsTrue();
    await Assert.That(import.MessageId).IsEqualTo(Guid.Parse(_id1));
    await Assert.That(import.StreamId).IsEqualTo(Guid.Parse(_id2));
    await Assert.That(import.MessageType).IsEqualTo("Whizbang.Test.Envelope");
    await Assert.That(import.Destination).IsEqualTo("orders/billing");
    await Assert.That(import.EnvelopeJson).IsEqualTo("""{"v":1,"p":{"x":1}}""")
      .Because("custody is the RAW wire body, verbatim — the import path never deserializes");
    await Assert.That(import.BrokerReason).IsEqualTo("MaxDeliveryAttemptsExceeded")
      .Because("the broker's own reason must be preserved, not discarded");
    await Assert.That(import.BrokerDescription).Contains("JsonTypeInfo");
    await Assert.That(import.EnqueuedAt).IsEqualTo(enqueued);
    await Assert.That(import.DeliveryCount).IsEqualTo(10);
  }

  [Test]
  public async Task TryBuildImport_NonGuidMessageId_ReturnsFalseAsync() {
    var msg = ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString("body"), messageId: "not-a-guid");

    var ok = AzureServiceBusDeadLetterDrainer.TryBuildImport(msg, "t", "s", out _);

    await Assert.That(ok).IsFalse()
      .Because("only Whizbang wire messages (GUID MessageId) are ours to custody — foreign "
             + "messages stay on the broker DLQ for their owner's tooling");
  }

  [Test]
  public async Task TryBuildImport_NoEnvelopeTypeOrSession_MapsNullsAsync() {
    var msg = ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString("body"), messageId: _id1);

    var ok = AzureServiceBusDeadLetterDrainer.TryBuildImport(msg, "t", "s", out var import);

    await Assert.That(ok).IsTrue();
    await Assert.That(import.MessageType).IsNull();
    await Assert.That(import.StreamId).IsNull();
  }

  // ===== DrainDeadLetterQueueAsync — drain loop (mockable SDK fakes, no broker) =====

  [Test]
  public async Task DrainDeadLetterQueueAsync_MaxCountZero_ReturnsZeroWithoutContactingBrokerAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 0);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.CreateReceiverCalls).IsEqualTo(0);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_EmptyDlq_ReturnsZeroAndConfiguresDeadLetterReceiverAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 50);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.CreateReceiverCalls).IsEqualTo(1);
    await Assert.That(client.ReceiverTopic).IsEqualTo("orders");
    await Assert.That(client.ReceiverSubscription).IsEqualTo("billing");
    var options = client.ReceiverOptions;
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.SubQueue).IsEqualTo(SubQueue.DeadLetter);
    await Assert.That(options.ReceiveMode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.RequestedBatchSizes[0]).IsEqualTo(50);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_NullBatch_ReturnsZeroAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.Batches.Enqueue(null);
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(importer.Received).IsEmpty();
    await Assert.That(client.Receiver.Completed).IsEmpty();
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_MessagesAvailable_ImportsAndCompletesEachAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1, body: "payload-1"), _dlqMessage(_id2, body: "payload-2") });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(importer.Received).Count().IsEqualTo(2)
      .Because("every Whizbang DLQ message transfers custody through the import seam");
    await Assert.That(importer.Received[0].MessageId).IsEqualTo(Guid.Parse(_id1));
    await Assert.That(importer.Received[0].EnvelopeJson).IsEqualTo("payload-1");
    await Assert.That(importer.Received[0].MessageType).IsEqualTo("Whizbang.Test.Envelope");
    await Assert.That(importer.Received[1].MessageId).IsEqualTo(Guid.Parse(_id2));
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(2);
    await Assert.That(client.Receiver.Abandoned).IsEmpty();
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_DuplicateImport_StillCompletesAndCountsAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    importer.PlannedOutcomes.Enqueue(false);   // duplicate — custody already exists
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(1)
      .Because("a duplicate means custody already exists — completing removes the broker copy "
             + "instead of re-offering it forever");
    await Assert.That(client.Receiver.Abandoned).IsEmpty();
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_NonWhizbangMessage_AbandonsAndDoesNotImportAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.Batches.Enqueue(new[] {
      ServiceBusModelFactory.ServiceBusReceivedMessage(
        body: BinaryData.FromString("foreign"), messageId: "not-a-guid"),
      _dlqMessage(_id1),
    });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1)
      .Because("only the Whizbang message counts — the foreign one is left for its owner");
    await Assert.That(importer.Received).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.Abandoned[0]).IsEqualTo("not-a-guid");
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_LargeMaxCount_CapsBatchSizeAt100Async() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.Batches.Enqueue(_manyMessages(100, 1000));
    client.Receiver.Batches.Enqueue(_manyMessages(100, 2000));
    client.Receiver.Batches.Enqueue(_manyMessages(50, 3000));
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 250);

    await Assert.That(drained).IsEqualTo(250);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(3);
    await Assert.That(client.Receiver.RequestedBatchSizes[0]).IsEqualTo(100);
    await Assert.That(client.Receiver.RequestedBatchSizes[1]).IsEqualTo(100);
    await Assert.That(client.Receiver.RequestedBatchSizes[2]).IsEqualTo(50);
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(250);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_MaxCountReached_StopsWithoutFurtherReceivesAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1), _dlqMessage(_id2) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 2);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.RequestedBatchSizes[0]).IsEqualTo(2);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_ImportFails_AbandonsMessageAndContinuesAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    importer.PlannedOutcomes.Enqueue(new InvalidOperationException("import failed"));
    importer.PlannedOutcomes.Enqueue(true);
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1), _dlqMessage(_id2) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1)
      .Because("a failed import must NOT settle the broker copy — abandon re-offers it next pass");
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.Abandoned[0]).IsEqualTo(_id1);
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.Completed[0]).IsEqualTo(_id2);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_CompleteFails_AbandonsMessageAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.CompleteException = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(importer.Received).Count().IsEqualTo(1)
      .Because("the import succeeded before the completion failed — the custody row exists and "
             + "the eventual re-drain resolves as a duplicate, which settles");
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_AbandonThrowsServiceBusException_SwallowsExceptionAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    importer.PlannedOutcomes.Enqueue(new InvalidOperationException("import failed"));
    client.Receiver.AbandonException = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1)
      .Because("the abandon attempt is made, then the lock-lost failure is swallowed");
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_AbandonThrowsObjectDisposed_SwallowsExceptionAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    importer.PlannedOutcomes.Enqueue(new InvalidOperationException("import failed"));
    client.Receiver.AbandonException = new ObjectDisposedException("receiver");
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_AbandonThrowsUnexpectedException_PropagatesAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    importer.PlannedOutcomes.Enqueue(new InvalidOperationException("import failed"));
    client.Receiver.AbandonException = new InvalidOperationException("abandon exploded");
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(maxCount: 10))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_CancelledDuringReceive_ThrowsBeforeProcessingBatchAsync() {
    using var cts = new CancellationTokenSource();
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.OnReceive = cts.Cancel;
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(maxCount: 10, cts.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(importer.Received).IsEmpty();
    await Assert.That(client.Receiver.Completed).IsEmpty();
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_CancelledAfterFirstMessage_ReturnsPartialCountAsync() {
    using var cts = new CancellationTokenSource();
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.OnComplete = cts.Cancel;
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id2) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10, cts.Token);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1)
      .Because("the cancelled token must stop the loop before a second receive");
  }

  [Test]
  [Timeout(15_000)]
  public async Task DrainDeadLetterQueueAsync_WholeBatchAbandoned_StopsInsteadOfReOfferingItForeverAsync(
      CancellationToken cancellationToken) {
    // Every import fails — the shape a host with no coordinator (or a database that cannot take
    // custody) produces. The broker re-offers each abandoned message immediately, so the loop's
    // "empty DLQ" exit never arrives: without a no-progress guard this pass runs until
    // cancellation, hammering the broker with receives it will never settle.
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.RedeliverAbandoned = true;
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage(_id1) });
    for (var i = 0; i < 50; i++) {
      importer.PlannedOutcomes.Enqueue(new InvalidOperationException("no custody available"));
    }
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 100, cancellationToken);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1)
      .Because("a pass that settles nothing must stop after one receive — re-receiving hands "
             + "back the same abandoned messages, and nothing that failed resolves mid-pass");
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1)
      .Because("the message is abandoned exactly once, not once per spin");
  }

  [Test]
  [Timeout(15_000)]
  public async Task DrainDeadLetterQueueAsync_PartialProgress_KeepsDrainingUntilNothingSettlesAsync(
      CancellationToken cancellationToken) {
    // A mixed DLQ: one message this host can custody, one foreign message it may never settle.
    // The pass that drains the Whizbang message made progress, so the loop continues; the pass
    // that only re-abandons the foreign one is where it stops.
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    client.Receiver.RedeliverAbandoned = true;
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("not-a-guid"), _dlqMessage(_id1) });
    await using var drainer = _drainerFor(client, importer);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 100, cancellationToken);

    await Assert.That(drained).IsEqualTo(1)
      .Because("the foreign message is not ours to settle, so only the Whizbang one drains");
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(2)
      .Because("the first pass made progress and earns another receive; the second settles "
             + "nothing and ends the pass");
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_SecondCall_ReusesReceiverAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    await using var drainer = _drainerFor(client, importer);

    _ = await drainer.DrainDeadLetterQueueAsync(maxCount: 5);
    _ = await drainer.DrainDeadLetterQueueAsync(maxCount: 5);

    await Assert.That(client.CreateReceiverCalls).IsEqualTo(1);
  }

  [Test]
  public async Task DisposeAsync_AfterDrain_DisposesReceiverOnceAsync() {
    var client = new FakeDrainClient();
    var importer = new FakeImporter();
    var drainer = _drainerFor(client, importer);
    _ = await drainer.DrainDeadLetterQueueAsync(maxCount: 1);

    await drainer.DisposeAsync();
    await drainer.DisposeAsync();

    await Assert.That(client.Receiver.DisposeCount).IsEqualTo(1);
  }

  // ===== Helpers =====

  private static AzureServiceBusDeadLetterDrainer _drainerFor(FakeDrainClient client, FakeImporter importer) =>
    new(client, "orders", "billing", importer.ImportAsync, NullLogger<AzureServiceBusDeadLetterDrainer>.Instance);

  /// <summary>Builds a broker-shaped DLQ message with an EnvelopeType application property.</summary>
  private static ServiceBusReceivedMessage _dlqMessage(string messageId, string body = "dlq-body") =>
    ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString(body),
      messageId: messageId,
      properties: new Dictionary<string, object> {
        ["EnvelopeType"] = "Whizbang.Test.Envelope",
        ["DeadLetterReason"] = "TestReason",
      });

  private static List<ServiceBusReceivedMessage> _manyMessages(int count, int idBase) {
    var messages = new List<ServiceBusReceivedMessage>(count);
    for (var i = 0; i < count; i++) {
      messages.Add(_dlqMessage($"00000000-0000-0000-0000-{idBase + i:d12}"));
    }
    return messages;
  }

  /// <summary>
  /// Recording import seam with per-call outcome planning: <c>true</c>/<c>false</c> plan the
  /// return value, an <see cref="Exception"/> plans a throw, an exhausted queue succeeds.
  /// </summary>
  private sealed class FakeImporter {
    public List<BrokerDeadLetterImport> Received { get; } = [];
    public Queue<object> PlannedOutcomes { get; } = new();

    public Task<bool> ImportAsync(BrokerDeadLetterImport import, CancellationToken ct) {
      Received.Add(import);
      if (PlannedOutcomes.Count == 0) {
        return Task.FromResult(true);
      }
      return PlannedOutcomes.Dequeue() switch {
        bool b => Task.FromResult(b),
        Exception ex => Task.FromException<bool>(ex),
        _ => Task.FromResult(true),
      };
    }
  }

  // ===== Drain-loop test doubles =====

  /// <summary>
  /// Mockable ServiceBusClient that hands out a single recording receiver without opening any
  /// connection. Captures the receiver wiring arguments so tests can lock the DLQ sub-queue
  /// contract.
  /// </summary>
  private sealed class FakeDrainClient : ServiceBusClient {
    public FakeDlqReceiver Receiver { get; } = new();
    public int CreateReceiverCalls { get; private set; }
    public string? ReceiverTopic { get; private set; }
    public string? ReceiverSubscription { get; private set; }
    public ServiceBusReceiverOptions? ReceiverOptions { get; private set; }

    public override ServiceBusReceiver CreateReceiver(
      string topicName, string subscriptionName, ServiceBusReceiverOptions options) {
      CreateReceiverCalls++;
      ReceiverTopic = topicName;
      ReceiverSubscription = subscriptionName;
      ReceiverOptions = options;
      return Receiver;
    }
  }

  /// <summary>
  /// Recording DLQ receiver — serves queued batches (an exhausted queue yields empty
  /// batches), records settlement calls, and can inject settlement failures. Attempts are
  /// recorded before the injected exception is thrown so swallow behavior stays observable.
  /// </summary>
  private sealed class FakeDlqReceiver : ServiceBusReceiver {
    public Queue<IReadOnlyList<ServiceBusReceivedMessage>?> Batches { get; } = new();
    public List<int> RequestedBatchSizes { get; } = [];
    public List<string> Completed { get; } = [];
    public List<string> Abandoned { get; } = [];
    public Exception? CompleteException { get; set; }
    public Exception? AbandonException { get; set; }
    /// <summary>
    /// Models the broker faithfully for the drain loop's termination: abandoning returns a
    /// message to the queue IMMEDIATELY, so the next receive offers it again. Off by default so
    /// the settlement tests stay one-shot.
    /// </summary>
    public bool RedeliverAbandoned { get; set; }
    public Action? OnReceive { get; set; }
    public Action? OnComplete { get; set; }
    public int DisposeCount { get; private set; }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
      int maxMessages, TimeSpan? maxWaitTime = default, CancellationToken cancellationToken = default) {
      RequestedBatchSizes.Add(maxMessages);
      OnReceive?.Invoke();
      if (Batches.Count == 0) {
        return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
      }
      var batch = Batches.Dequeue();
      // A null batch intentionally exercises the drainer's null-guard arm.
      return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch!);
    }

    public override Task CompleteMessageAsync(
      ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) {
      Completed.Add(message.MessageId);
      OnComplete?.Invoke();
      return CompleteException is null ? Task.CompletedTask : Task.FromException(CompleteException);
    }

    public override Task AbandonMessageAsync(
      ServiceBusReceivedMessage message,
      IDictionary<string, object>? propertiesToModify = null,
      CancellationToken cancellationToken = default) {
      Abandoned.Add(message.MessageId);
      if (RedeliverAbandoned) {
        Batches.Enqueue([message]);
      }
      return AbandonException is null ? Task.CompletedTask : Task.FromException(AbandonException);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusReceiver.DisposeAsync() calls CloseAsync which NREs on mocking-constructor instances; this fake only records the call")]
    public override ValueTask DisposeAsync() {
      DisposeCount++;
      return ValueTask.CompletedTask;
    }
  }
}
