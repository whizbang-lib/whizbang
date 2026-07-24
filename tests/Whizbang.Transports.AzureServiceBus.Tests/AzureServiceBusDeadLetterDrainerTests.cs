using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// v0.502 slice C.8 — unit regression locks for <see cref="AzureServiceBusDeadLetterDrainer"/>.
/// Covers the constructor argument-validation surface, the <c>TransportName</c> format
/// contract (used as the OTEL metric dimension), guard rails on
/// <see cref="AzureServiceBusDeadLetterDrainer.DrainDeadLetterQueueAsync"/>, dispose
/// semantics, and the internal <c>CloneForResend</c> message-mapping logic.
///
/// <para>
/// The guard-rail tests use a real <see cref="ServiceBusClient"/> against the emulator
/// connection string (matches the existing <c>AzureServiceBusTransportUnitTests</c> pattern
/// — no broker traffic is generated when receivers/senders are not actually used).
/// </para>
/// <para>
/// The receive/re-send drain loop itself is exercised without a broker via the Azure SDK's
/// documented mocking surface: a <c>FakeDrainClient</c> hands out a recording
/// <c>FakeDlqReceiver</c> / <c>FakeDrainSender</c> pair, mirroring the fake pattern in
/// <c>AzureServiceBusErrorHandlingTests</c>. End-to-end broker behavior remains covered by
/// the integration suite.
/// </para>
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusDeadLetterDrainerTests {

  // Same emulator endpoint used by AzureServiceBusTransportUnitTests — constructs a real
  // ServiceBusClient without attempting any broker connection.
  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=ZmFrZWtleQ==;UseDevelopmentEmulator=true";

  private static AzureServiceBusDeadLetterDrainer _newDrainer(string? topic = "topic-a", string? sub = "sub-a") {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    return new AzureServiceBusDeadLetterDrainer(
      client,
      topic!,
      sub!,
      NullLogger<AzureServiceBusDeadLetterDrainer>.Instance);
  }

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullClient_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client: null!,
      topicName: "t",
      subscriptionName: "s",
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_EmptyTopic_ThrowsArgumentExceptionAsync() {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client, topicName: "", subscriptionName: "s",
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_WhitespaceTopic_ThrowsArgumentExceptionAsync() {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client, topicName: "   ", subscriptionName: "s",
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_EmptySubscription_ThrowsArgumentExceptionAsync() {
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    await Assert.That(() => new AzureServiceBusDeadLetterDrainer(
      client, topicName: "t", subscriptionName: "",
      logger: NullLogger<AzureServiceBusDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_NullLogger_AcceptedFallsBackToNullLoggerAsync() {
    // Logger is documented as optional; constructor accepts null and substitutes NullLogger.
    var client = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    var drainer = new AzureServiceBusDeadLetterDrainer(
      client, topicName: "t", subscriptionName: "s", logger: null!);
    await Assert.That(drainer).IsNotNull();
    await drainer.DisposeAsync();
  }

  // ===== TransportName =====

  [Test]
  public async Task TransportName_FormatsAsAsbTopicSubAsync() {
    using var _ = await _disposeAtEnd(_newDrainer("orders", "inventory-svc"));
    // The format contract is "asb:{topic}/{subscription}" — this is the OTEL metric
    // dimension that dashboards key on. Locking it.
    await Assert.That(_.Value.TransportName).IsEqualTo("asb:orders/inventory-svc");
  }

  // ===== DrainDeadLetterQueueAsync guards =====

  [Test]
  public async Task DrainDeadLetterQueueAsync_MaxCountZero_ReturnsZeroWithoutContactingBrokerAsync() {
    using var _ = await _disposeAtEnd(_newDrainer());
    var result = await _.Value.DrainDeadLetterQueueAsync(maxCount: 0);
    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_NegativeMaxCount_ReturnsZeroAsync() {
    using var _ = await _disposeAtEnd(_newDrainer());
    var result = await _.Value.DrainDeadLetterQueueAsync(maxCount: -5);
    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_AfterDispose_ThrowsObjectDisposedExceptionAsync() {
    var drainer = _newDrainer();
    await drainer.DisposeAsync();
    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(maxCount: 10))
      .Throws<ObjectDisposedException>();
  }

  // ===== DisposeAsync =====

  [Test]
  public async Task DisposeAsync_CalledTwice_DoesNotThrowAsync() {
    var drainer = _newDrainer();
    await drainer.DisposeAsync();
    await drainer.DisposeAsync(); // second call must be a no-op
  }

  // ===== CloneForResend (internal) =====

  [Test]
  public async Task CloneForResend_CopiesBodyAndRoutingFieldsAsync() {
    var original = new ServiceBusMessage("payload-body") {
      MessageId = "msg-1",
      ContentType = "application/json",
      CorrelationId = "corr-1",
      Subject = "TestSubject",
      To = "to-addr",
      ReplyTo = "reply-addr",
      ReplyToSessionId = "rts-1",
      SessionId = "sess-1",
      PartitionKey = "sess-1",  // PartitionKey must match SessionId when both set
    };
    original.ApplicationProperties["EnvelopeType"] = "Whizbang.Test.Envelope";
    original.ApplicationProperties["customHeader"] = 42;

    // Convert to a ServiceBusReceivedMessage so we can test CloneForResend.
    var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString("payload-body"),
      messageId: "msg-1",
      partitionKey: "sess-1",
      sessionId: "sess-1",
      correlationId: "corr-1",
      subject: "TestSubject",
      to: "to-addr",
      contentType: "application/json",
      replyTo: "reply-addr",
      replyToSessionId: "rts-1",
      properties: new Dictionary<string, object> {
        ["EnvelopeType"] = "Whizbang.Test.Envelope",
        ["customHeader"] = 42,
      });

    var cloned = AzureServiceBusDeadLetterDrainer.CloneForResend(received);

    await Assert.That(cloned.Body.ToString()).IsEqualTo("payload-body");
    await Assert.That(cloned.MessageId).IsEqualTo("msg-1");
    await Assert.That(cloned.ContentType).IsEqualTo("application/json");
    await Assert.That(cloned.CorrelationId).IsEqualTo("corr-1");
    await Assert.That(cloned.Subject).IsEqualTo("TestSubject");
    await Assert.That(cloned.To).IsEqualTo("to-addr");
    await Assert.That(cloned.ReplyTo).IsEqualTo("reply-addr");
    await Assert.That(cloned.ReplyToSessionId).IsEqualTo("rts-1");
    await Assert.That(cloned.SessionId).IsEqualTo("sess-1");
    await Assert.That(cloned.PartitionKey).IsEqualTo("sess-1");
    await Assert.That(cloned.ApplicationProperties["EnvelopeType"]).IsEqualTo("Whizbang.Test.Envelope");
    await Assert.That(cloned.ApplicationProperties["customHeader"]).IsEqualTo(42);
  }

  [Test]
  public async Task CloneForResend_EmptyApplicationProperties_ProducesEmptyAppPropsAsync() {
    var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString("empty-props"),
      messageId: "msg-2");

    var cloned = AzureServiceBusDeadLetterDrainer.CloneForResend(received);

    await Assert.That(cloned.MessageId).IsEqualTo("msg-2");
    await Assert.That(cloned.ApplicationProperties.Count).IsEqualTo(0);
  }

  // ===== DrainDeadLetterQueueAsync — drain loop (mockable SDK fakes, no broker) =====

  /// <summary>
  /// Empty DLQ: the first receive returns an empty batch and the loop exits with zero.
  /// Also locks the receiver wiring contract — the DLQ sub-queue in PeekLock mode against
  /// the configured topic/subscription, with the sender bound to the same topic.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_EmptyDlq_ReturnsZeroAndConfiguresDeadLetterReceiverAsync() {
    var client = new FakeDrainClient();
    await using var drainer = new AzureServiceBusDeadLetterDrainer(
      client, "orders", "billing", NullLogger<AzureServiceBusDeadLetterDrainer>.Instance);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 50);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.CreateReceiverCalls).IsEqualTo(1);
    await Assert.That(client.ReceiverTopic).IsEqualTo("orders");
    await Assert.That(client.ReceiverSubscription).IsEqualTo("billing");
    var options = client.ReceiverOptions;
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.SubQueue).IsEqualTo(SubQueue.DeadLetter);
    await Assert.That(options.ReceiveMode).IsEqualTo(ServiceBusReceiveMode.PeekLock);
    await Assert.That(client.SenderTopic).IsEqualTo("orders");
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.RequestedBatchSizes[0]).IsEqualTo(50);
  }

  /// <summary>A null batch from the receiver takes the same exit arm as an empty batch.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_NullBatch_ReturnsZeroAsync() {
    var client = new FakeDrainClient();
    client.Receiver.Batches.Enqueue(null);
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Sender.Sent).IsEmpty();
    await Assert.That(client.Receiver.Completed).IsEmpty();
  }

  /// <summary>
  /// Happy path: each DLQ message is re-sent onto the topic (body + routing fields cloned)
  /// and then completed so it leaves the DLQ.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_MessagesAvailable_ResendsAndCompletesEachAsync() {
    var client = new FakeDrainClient();
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1", body: "payload-1"), _dlqMessage("m-2", body: "payload-2") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(client.Sender.Sent).Count().IsEqualTo(2);
    await Assert.That(client.Sender.Sent[0].MessageId).IsEqualTo("m-1");
    await Assert.That(client.Sender.Sent[0].Body.ToString()).IsEqualTo("payload-1");
    await Assert.That(client.Sender.Sent[0].ApplicationProperties["EnvelopeType"]).IsEqualTo("Whizbang.Test.Envelope");
    await Assert.That(client.Sender.Sent[1].MessageId).IsEqualTo("m-2");
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(2);
    await Assert.That(client.Receiver.Abandoned).IsEmpty();
  }

  /// <summary>
  /// The per-iteration batch size is capped at 100 and shrinks to the remaining budget on
  /// the final pull; the loop exits exactly at maxCount.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_LargeMaxCount_CapsBatchSizeAt100Async() {
    var client = new FakeDrainClient();
    client.Receiver.Batches.Enqueue(_manyMessages(100, "b1"));
    client.Receiver.Batches.Enqueue(_manyMessages(100, "b2"));
    client.Receiver.Batches.Enqueue(_manyMessages(50, "b3"));
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 250);

    await Assert.That(drained).IsEqualTo(250);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(3);
    await Assert.That(client.Receiver.RequestedBatchSizes[0]).IsEqualTo(100);
    await Assert.That(client.Receiver.RequestedBatchSizes[1]).IsEqualTo(100);
    await Assert.That(client.Receiver.RequestedBatchSizes[2]).IsEqualTo(50);
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(250);
  }

  /// <summary>maxCount below 100 flows straight through as the requested batch size.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_MaxCountReached_StopsWithoutFurtherReceivesAsync() {
    var client = new FakeDrainClient();
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1"), _dlqMessage("m-2") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 2);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.RequestedBatchSizes[0]).IsEqualTo(2);
  }

  /// <summary>
  /// A send failure abandons that message (broker will redeliver it to the DLQ receiver on
  /// a later sweep) and the loop continues with the rest of the batch.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_SendFails_AbandonsMessageAndContinuesAsync() {
    var client = new FakeDrainClient();
    client.Sender.PlannedSendOutcomes.Enqueue(new ServiceBusException("send failed", ServiceBusFailureReason.ServiceBusy));
    client.Sender.PlannedSendOutcomes.Enqueue(null);
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1"), _dlqMessage("m-2") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.Abandoned[0]).IsEqualTo("m-1");
    await Assert.That(client.Receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(client.Receiver.Completed[0]).IsEqualTo("m-2");
  }

  /// <summary>
  /// A completion failure after a successful re-send also routes through the catch arm:
  /// the message is abandoned and not counted as drained.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_CompleteFails_AbandonsMessageAsync() {
    var client = new FakeDrainClient();
    client.Receiver.CompleteException = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Sender.Sent).Count().IsEqualTo(1)
      .Because("the re-send succeeded before the completion failed");
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Abandon failures on a lost lock (ServiceBusException) are swallowed — the broker
  /// re-delivers naturally on the next sweep.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_AbandonThrowsServiceBusException_SwallowsExceptionAsync() {
    var client = new FakeDrainClient();
    client.Sender.PlannedSendOutcomes.Enqueue(new ServiceBusException("send failed", ServiceBusFailureReason.ServiceBusy));
    client.Receiver.AbandonException = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1)
      .Because("the abandon attempt is made, then the lock-lost failure is swallowed");
  }

  /// <summary>Abandon failures on a disposed receiver are swallowed the same way.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_AbandonThrowsObjectDisposed_SwallowsExceptionAsync() {
    var client = new FakeDrainClient();
    client.Sender.PlannedSendOutcomes.Enqueue(new ServiceBusException("send failed", ServiceBusFailureReason.ServiceBusy));
    client.Receiver.AbandonException = new ObjectDisposedException("receiver");
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Receiver.Abandoned).Count().IsEqualTo(1);
  }

  /// <summary>
  /// The abandon swallow filter is narrow: unexpected exception types propagate to the
  /// worker so the failure is visible instead of silently looping.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_AbandonThrowsUnexpectedException_PropagatesAsync() {
    var client = new FakeDrainClient();
    client.Sender.PlannedSendOutcomes.Enqueue(new ServiceBusException("send failed", ServiceBusFailureReason.ServiceBusy));
    client.Receiver.AbandonException = new InvalidOperationException("abandon exploded");
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1") });
    await using var drainer = _drainerFor(client);

    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(maxCount: 10))
      .Throws<InvalidOperationException>();
  }

  /// <summary>
  /// Cancellation observed between receive and settle throws before any message in the
  /// batch is re-sent — no message is half-processed after cancellation.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_CancelledDuringReceive_ThrowsBeforeProcessingBatchAsync() {
    using var cts = new CancellationTokenSource();
    var client = new FakeDrainClient();
    client.Receiver.OnReceive = cts.Cancel;
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1") });
    await using var drainer = _drainerFor(client);

    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(maxCount: 10, cts.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(client.Sender.Sent).IsEmpty();
    await Assert.That(client.Receiver.Completed).IsEmpty();
  }

  /// <summary>
  /// Cancellation raised after a message settles is honored at the loop condition: the
  /// drainer returns the partial count instead of pulling another batch.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_CancelledAfterFirstMessage_ReturnsPartialCountAsync() {
    using var cts = new CancellationTokenSource();
    var client = new FakeDrainClient();
    client.Receiver.OnComplete = cts.Cancel;
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-1") });
    client.Receiver.Batches.Enqueue(new[] { _dlqMessage("m-2") });
    await using var drainer = _drainerFor(client);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10, cts.Token);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(client.Receiver.RequestedBatchSizes).Count().IsEqualTo(1)
      .Because("the cancelled token must stop the loop before a second receive");
  }

  /// <summary>The receiver/sender pair is created once and cached across drain calls.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_SecondCall_ReusesReceiverAndSenderAsync() {
    var client = new FakeDrainClient();
    await using var drainer = _drainerFor(client);

    _ = await drainer.DrainDeadLetterQueueAsync(maxCount: 5);
    _ = await drainer.DrainDeadLetterQueueAsync(maxCount: 5);

    await Assert.That(client.CreateReceiverCalls).IsEqualTo(1);
    await Assert.That(client.CreateSenderCalls).IsEqualTo(1);
  }

  /// <summary>DisposeAsync disposes the cached receiver and sender exactly once.</summary>
  [Test]
  public async Task DisposeAsync_AfterDrain_DisposesReceiverAndSenderAsync() {
    var client = new FakeDrainClient();
    var drainer = _drainerFor(client);
    _ = await drainer.DrainDeadLetterQueueAsync(maxCount: 1);

    await drainer.DisposeAsync();
    await drainer.DisposeAsync();

    await Assert.That(client.Receiver.DisposeCount).IsEqualTo(1);
    await Assert.That(client.Sender.DisposeCount).IsEqualTo(1);
  }

  // ===== Helpers =====

  /// <summary>
  /// Small disposable wrapper that calls DisposeAsync on the drainer when the test scope
  /// ends. Lets us write `using var _ = await _disposeAtEnd(_newDrainer());` without
  /// separate try/finally noise in each test.
  /// </summary>
  private static Task<AsyncDisposer> _disposeAtEnd(AzureServiceBusDeadLetterDrainer drainer)
    => Task.FromResult(new AsyncDisposer(drainer));

  private readonly struct AsyncDisposer(AzureServiceBusDeadLetterDrainer value) : IDisposable {
    public AzureServiceBusDeadLetterDrainer Value { get; } = value;
    public void Dispose() {
      // Sync disposal: block on DisposeAsync's ValueTask. The ASB SDK guarantees the
      // ValueTask completes synchronously when no receiver/sender was created.
      Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
  }

  // ===== Drain-loop helpers =====

  private static AzureServiceBusDeadLetterDrainer _drainerFor(FakeDrainClient client) =>
    new(client, "orders", "billing", NullLogger<AzureServiceBusDeadLetterDrainer>.Instance);

  /// <summary>Builds a broker-shaped DLQ message with an EnvelopeType application property.</summary>
  private static ServiceBusReceivedMessage _dlqMessage(string messageId, string body = "dlq-body") =>
    ServiceBusModelFactory.ServiceBusReceivedMessage(
      body: BinaryData.FromString(body),
      messageId: messageId,
      properties: new Dictionary<string, object> {
        ["EnvelopeType"] = "Whizbang.Test.Envelope",
        ["DeadLetterReason"] = "TestReason",
      });

  private static List<ServiceBusReceivedMessage> _manyMessages(int count, string idPrefix) {
    var messages = new List<ServiceBusReceivedMessage>(count);
    for (var i = 0; i < count; i++) {
      messages.Add(_dlqMessage($"{idPrefix}-{i}"));
    }
    return messages;
  }

  // ===== Drain-loop test doubles =====

  /// <summary>
  /// Mockable ServiceBusClient that hands out a single recording receiver/sender pair
  /// without opening any connection. Captures the receiver wiring arguments so tests can
  /// lock the DLQ sub-queue contract.
  /// </summary>
  private sealed class FakeDrainClient : ServiceBusClient {
    public FakeDlqReceiver Receiver { get; } = new();
    public FakeDrainSender Sender { get; } = new();
    public int CreateReceiverCalls { get; private set; }
    public int CreateSenderCalls { get; private set; }
    public string? ReceiverTopic { get; private set; }
    public string? ReceiverSubscription { get; private set; }
    public ServiceBusReceiverOptions? ReceiverOptions { get; private set; }
    public string? SenderTopic { get; private set; }

    public override ServiceBusReceiver CreateReceiver(
      string topicName, string subscriptionName, ServiceBusReceiverOptions options) {
      CreateReceiverCalls++;
      ReceiverTopic = topicName;
      ReceiverSubscription = subscriptionName;
      ReceiverOptions = options;
      return Receiver;
    }

    public override ServiceBusSender CreateSender(string queueOrTopicName) {
      CreateSenderCalls++;
      SenderTopic = queueOrTopicName;
      return Sender;
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
      return AbandonException is null ? Task.CompletedTask : Task.FromException(AbandonException);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusReceiver.DisposeAsync() calls CloseAsync which NREs on mocking-constructor instances; this fake only records the call")]
    public override ValueTask DisposeAsync() {
      DisposeCount++;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Recording ServiceBusSender with per-send failure injection (a queue of planned
  /// outcomes; null means success, an exhausted queue always succeeds) and dispose tracking.
  /// </summary>
  private sealed class FakeDrainSender : ServiceBusSender {
    public List<ServiceBusMessage> Sent { get; } = [];
    public Queue<Exception?> PlannedSendOutcomes { get; } = new();
    public int DisposeCount { get; private set; }

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default) {
      var outcome = PlannedSendOutcomes.Count > 0 ? PlannedSendOutcomes.Dequeue() : null;
      if (outcome is not null) {
        return Task.FromException(outcome);
      }
      Sent.Add(message);
      return Task.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusSender.DisposeAsync() calls CloseAsync which NREs on mocking-constructor instances; this fake only records the call")]
    public override ValueTask DisposeAsync() {
      DisposeCount++;
      return ValueTask.CompletedTask;
    }
  }
}
