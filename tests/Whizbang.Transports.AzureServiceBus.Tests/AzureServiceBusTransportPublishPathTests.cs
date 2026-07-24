using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport's publish-side paths: the pre-serialized-bytes
/// hint, correlation/causation projection, the SendTimeout hang guard, the debug-diagnostic
/// blocks, and the PublishBatchAsync batching pipeline (_createServiceBusMessage /
/// _sendAndRecordBatchAsync) — all driven through the Azure SDK's mocking constructors with
/// recording senders, no broker.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusTransportPublishPathTests {

  // ========================================
  // PublishAsync SINGLE-MESSAGE PATHS
  // ========================================

  /// <summary>
  /// A pre-serialized hint from the publish strategy MUST be used verbatim as the wire body —
  /// the transport must not re-serialize (that would undo post-serialize hook substitutions).
  /// </summary>
  [Test]
  public async Task PublishAsync_WithPreSerializedBytes_SendsHintBytesAsWireBodyAsync() {
    var (transport, client) = _createTransport();
    var envelope = AsbTransportTestData.CreateEnvelope();
    const string hintJson = """{"id":"pre-serialized-hint","p":{"content":"substituted"}}""";
    var hintBytes = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(hintJson));

    await transport.PublishAsync(
      envelope,
      _destination(),
      envelopeType: typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
      preSerializedBytes: hintBytes);

    var sender = client.LastSender!;
    await Assert.That(sender.Sent).Count().IsEqualTo(1);
    await Assert.That(sender.Sent[0].Body.ToString()).IsEqualTo(hintJson);
  }

  /// <summary>
  /// With a debug-enabled logger the publish path emits the serialization / subject /
  /// message-id diagnostics and the post-send confirmation that NullLogger-based tests skip.
  /// </summary>
  [Test]
  public async Task PublishAsync_DebugLoggingEnabled_EmitsPublishDiagnosticsAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(logger: logger);
    var envelope = AsbTransportTestData.CreateEnvelope();

    await transport.PublishAsync(envelope, _destination());

    await Assert.That(client.LastSender!.Sent).Count().IsEqualTo(1);
    await Assert.That(logger.Contains(LogLevel.Debug, "DIAGNOSTIC [Publish]: Serialized envelope")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Setting Subject=")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Created ServiceBusMessage with MessageId=")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Published message")).IsTrue();
    await Assert.That(logger.Contains(LogLevel.Debug, "Created sender for topic")).IsTrue();
  }

  /// <summary>
  /// Bodies longer than 500 characters take the truncated-preview branch of the publish
  /// diagnostic without affecting what goes on the wire.
  /// </summary>
  [Test]
  public async Task PublishAsync_DebugLoggingWithLargeBody_TruncatesJsonPreviewAsync() {
    var logger = new RecordingTransportLogger();
    var (transport, client) = _createTransport(logger: logger);
    var largeContent = new string('x', 600);
    var envelope = AsbTransportTestData.CreateEnvelope(content: largeContent);

    await transport.PublishAsync(envelope, _destination());

    var sentBody = client.LastSender!.Sent[0].Body.ToString();
    // Only the diagnostic preview is truncated — the wire body must stay complete.
    await Assert.That(sentBody).Contains(largeContent);
    await Assert.That(logger.Contains(LogLevel.Debug, "DIAGNOSTIC [Publish]: Serialized envelope")).IsTrue();
  }

  /// <summary>
  /// Correlation and causation ids on the first hop are projected onto the wire message
  /// (CorrelationId property + CausationId application property).
  /// </summary>
  [Test]
  public async Task PublishAsync_EnvelopeWithCorrelationAndCausation_ProjectsWirePropertiesAsync() {
    var (transport, client) = _createTransport();
    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var envelope = AsbTransportTestData.CreateEnvelope(
      correlationId: correlationId,
      causationId: causationId);

    await transport.PublishAsync(envelope, _destination());

    var message = client.LastSender!.Sent[0];
    var expectedCorrelation = correlationId.Value.ToString();
    var expectedCausation = causationId.Value.ToString();
    await Assert.That(message.CorrelationId).IsEqualTo(expectedCorrelation);
    await Assert.That(message.ApplicationProperties["CausationId"]).IsEqualTo(expectedCausation);
  }

  /// <summary>
  /// When SendMessageAsync hangs past the configured SendTimeout, the transport surfaces a
  /// TimeoutException instead of hanging the publisher. The fake sender never completes but
  /// observes the cancellation token, so the test tears down deterministically.
  /// </summary>
  [Test]
  public async Task PublishAsync_SendNeverCompletes_ThrowsTimeoutExceptionAsync() {
    var client = new RaisableServiceBusClient { HangOnSend = true };
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = false,
      SendTimeout = TimeSpan.FromMilliseconds(50)
    };
    var (transport, _) = _createTransport(options: options, client: client);
    using var cts = new CancellationTokenSource();

    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await transport.PublishAsync(
        AsbTransportTestData.CreateEnvelope(),
        _destination(),
        cancellationToken: cts.Token));

    await Assert.That(ex!.Message).Contains("timed out");
    await cts.CancelAsync();
  }

  // ========================================
  // PublishBatchAsync PIPELINE
  // ========================================

  /// <summary>
  /// Items across multiple streams (and a null-stream group) are batched per stream group,
  /// sent successfully, and stamped with SessionId + EnvelopeType on the wire.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_MixedStreamItems_SendsPerStreamBatchesSuccessfullyAsync() {
    var (transport, client) = _createTransport();
    var streamA = Guid.CreateVersion7();
    var streamB = Guid.CreateVersion7();
    var itemA = _item(AsbTransportTestData.CreateEnvelope(), streamId: streamA);
    var itemB = _item(AsbTransportTestData.CreateEnvelope(), streamId: streamB);
    var itemNoStream = _item(AsbTransportTestData.CreateEnvelope());

    var results = await transport.PublishBatchAsync([itemA, itemB, itemNoStream], _destination());

    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    var sender = client.LastSender!;
    await Assert.That(sender.SentBatchCounts).Count().IsEqualTo(3)
      .Because("each stream group (A, B, null) sends exactly one batch");
    var messageA = _findBatchedMessage(sender, itemA.MessageId);
    var expectedSessionId = streamA.ToString();
    await Assert.That(messageA.SessionId).IsEqualTo(expectedSessionId);
    await Assert.That(messageA.ApplicationProperties["EnvelopeType"])
      .IsEqualTo(typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName);
    var messageNoStream = _findBatchedMessage(sender, itemNoStream.MessageId);
    await Assert.That(string.IsNullOrEmpty(messageNoStream.SessionId)).IsTrue();
  }

  /// <summary>
  /// Shared destination metadata is applied to every item, and per-item metadata overrides
  /// destination metadata for the same key — that's the documented contract.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_DestinationAndPerItemMetadata_PerItemWinsAsync() {
    var (transport, client) = _createTransport();
    var destinationMetadata = new Dictionary<string, JsonElement> {
      ["shared-key"] = AsbTransportTestData.Json("\"from-destination\""),
      ["tenant"] = AsbTransportTestData.Json("\"acme\"")
    };
    var perItemMetadata = new Dictionary<string, JsonElement> {
      ["shared-key"] = AsbTransportTestData.Json("\"from-item\"")
    };
    var item = _item(AsbTransportTestData.CreateEnvelope(), perItemMetadata: perItemMetadata);
    var destination = new TransportDestination("bulk-topic", "orders.created", destinationMetadata);

    var results = await transport.PublishBatchAsync([item], destination);

    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Success).IsTrue();
    var message = _findBatchedMessage(client.LastSender!, item.MessageId);
    await Assert.That(message.ApplicationProperties["shared-key"]).IsEqualTo("from-item")
      .Because("per-item metadata overrides destination metadata for the same key");
    await Assert.That(message.ApplicationProperties["tenant"]).IsEqualTo("acme");
  }

  /// <summary>Per-item pre-serialized hints are used verbatim as the batched wire body.</summary>
  [Test]
  public async Task PublishBatchAsync_ItemWithPreSerializedBytes_UsesHintBytesAsync() {
    var (transport, client) = _createTransport();
    const string hintJson = """{"id":"batched-hint","p":{"content":"claim-envelope"}}""";
    var item = _item(
      AsbTransportTestData.CreateEnvelope(),
      preSerializedBytes: new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(hintJson)));

    var results = await transport.PublishBatchAsync([item], _destination());

    await Assert.That(results[0].Success).IsTrue();
    var message = _findBatchedMessage(client.LastSender!, item.MessageId);
    await Assert.That(message.Body.ToString()).IsEqualTo(hintJson);
  }

  /// <summary>
  /// A batch-level send failure marks every item that was in the failed batch as unsuccessful,
  /// carrying the exception type and message.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_BatchSendFails_ReportsFailureForAllBatchItemsAsync() {
    var client = new RaisableServiceBusClient {
      SendBatchException = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy)
    };
    var (transport, _) = _createTransport(client: client);
    var streamId = Guid.CreateVersion7();
    var item1 = _item(AsbTransportTestData.CreateEnvelope(), streamId: streamId);
    var item2 = _item(AsbTransportTestData.CreateEnvelope(), streamId: streamId);

    var results = await transport.PublishBatchAsync([item1, item2], _destination());

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results.All(r => !r.Success)).IsTrue();
    await Assert.That(results[0].Error).Contains("ServiceBusException");
    await Assert.That(results[0].Error).Contains("busy");
  }

  /// <summary>
  /// A message rejected by a fresh batch (too large even alone) is reported as a per-item
  /// size failure rather than failing the whole publish.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_MessageNeverFitsBatch_ReportsSizeErrorAsync() {
    var client = new RaisableServiceBusClient { TryAddCallback = static _ => false };
    var (transport, _) = _createTransport(client: client);
    var item = _item(AsbTransportTestData.CreateEnvelope());

    var results = await transport.PublishBatchAsync([item], _destination());

    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Success).IsFalse();
    await Assert.That(results[0].Error).Contains("exceeds maximum batch message size");
    await Assert.That(client.LastSender!.SentBatchCounts).IsEmpty()
      .Because("empty batches are never sent to the broker");
  }

  /// <summary>
  /// When the current batch fills mid-stream, it is sent and a new batch is started for the
  /// remaining items — both items succeed across two batch sends.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_BatchFillsMidStream_SendsAndContinuesInNewBatchAsync() {
    var tryAddCalls = 0;
    var client = new RaisableServiceBusClient {
      // Second TryAddMessage call reports "batch full" — forcing send + new batch.
      TryAddCallback = _ => Interlocked.Increment(ref tryAddCalls) != 2
    };
    var (transport, _) = _createTransport(client: client);
    var streamId = Guid.CreateVersion7();
    var item1 = _item(AsbTransportTestData.CreateEnvelope(), streamId: streamId);
    var item2 = _item(AsbTransportTestData.CreateEnvelope(), streamId: streamId);

    var results = await transport.PublishBatchAsync([item1, item2], _destination());

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    var sender = client.LastSender!;
    await Assert.That(sender.SentBatchCounts).Count().IsEqualTo(2)
      .Because("the full batch is flushed and the overflow item goes into a second batch");
    await Assert.That(sender.SentBatchCounts.All(count => count == 1)).IsTrue();
  }

  /// <summary>
  /// A per-item serialization failure (envelope type not registered in any JSON context) is
  /// reported for that item only; sibling items still publish.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_ItemSerializationFails_ReportsItemFailureAsync() {
    var (transport, client) = _createTransport();
    var goodItem = _item(AsbTransportTestData.CreateEnvelope());
    var badEnvelope = _createUnregisteredEnvelope();
    var badItem = new BulkPublishItem {
      Envelope = badEnvelope,
      EnvelopeType = null,
      MessageId = badEnvelope.MessageId.Value
    };

    var results = await transport.PublishBatchAsync([goodItem, badItem], _destination());

    await Assert.That(results).Count().IsEqualTo(2);
    var goodResult = results.Single(r => r.MessageId == goodItem.MessageId);
    var badResult = results.Single(r => r.MessageId == badItem.MessageId);
    await Assert.That(goodResult.Success).IsTrue();
    await Assert.That(badResult.Success).IsFalse();
    await Assert.That(badResult.Error).IsNotNull();
    await Assert.That(client.LastSender!.SentBatchCounts).Count().IsEqualTo(1)
      .Because("the surviving item is still sent in its batch");
  }

  // ========================================
  // HELPERS
  // ========================================

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport(
    AzureServiceBusOptions? options = null,
    RecordingTransportLogger? logger = null,
    RaisableServiceBusClient? client = null) {
    client ??= new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options ?? new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      (ILogger<AzureServiceBusTransport>?)logger ?? NullLogger<AzureServiceBusTransport>.Instance);
    return (transport, client);
  }

  private static TransportDestination _destination() => new("bulk-topic", "orders.created");

  private static BulkPublishItem _item(
    MessageEnvelope<TestMessage> envelope,
    Guid? streamId = null,
    ReadOnlyMemory<byte>? preSerializedBytes = null,
    IReadOnlyDictionary<string, JsonElement>? perItemMetadata = null) => new() {
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
      MessageId = envelope.MessageId.Value,
      StreamId = streamId,
      PreSerializedBytes = preSerializedBytes,
      PerItemMetadata = perItemMetadata
    };

  /// <summary>Envelope whose payload type is deliberately absent from every registered JSON context.</summary>
  private static MessageEnvelope<UnregisteredBatchMessage> _createUnregisteredEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = new UnregisteredBatchMessage("not-registered"),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "bulk-topic"
      }
    ]
  };

  /// <summary>Finds the message with the given MessageId across every batch store of the sender.</summary>
  private static ServiceBusMessage _findBatchedMessage(RecordingBatchSender sender, Guid messageId) {
    var expectedId = messageId.ToString();
    return sender.BatchStores
      .SelectMany(store => store)
      .Single(m => m.MessageId == expectedId);
  }
}
