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
/// The actual receive/send loop exercises a real broker and is covered by the integration
/// suite. These unit tests use a real <see cref="ServiceBusClient"/> against the emulator
/// connection string (matches the existing <c>AzureServiceBusTransportUnitTests</c> pattern
/// — no broker traffic is generated when receivers/senders are not actually used).
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
}
