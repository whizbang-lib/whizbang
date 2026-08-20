using System.Text.Json;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Transport;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Payload type deliberately NOT registered with any JsonSerializerContext.
/// Used to exercise the per-item serialization-failure branch of PublishBatchAsync.
/// </summary>
public sealed record UnserializableBatchMessage(string Content);

/// <summary>
/// <para>Integration tests for AzureServiceBusTransport dead-letter and bulk-publish paths
/// against the Service Bus emulator:</para>
/// <list type="bullet">
///   <item>PublishBatchAsync multi-message paths — empty input, multi-message success,
///   stream grouping (SessionId stamping), batch overflow split, oversized-item per-item
///   failure, unserializable-item per-item failure.</item>
///   <item>Receive-side settlement — missing EnvelopeType metadata dead-letters via
///   _safeDeadLetterAsync; handler exceptions abandon then dead-letter once
///   MaxDeliveryAttempts is reached; malformed JSON bodies are acked+dropped
///   (NOT dead-lettered) per the slice 1 hotfix.</item>
///   <item>SubscribeBatchAsync — non-session collector flush path and session-enabled
///   single-item batch path (session subscription pre-provisioned in Config.json).</item>
/// </list>
/// <para>Waits use completion signals (Channel, MessageAwaiter) or broker-side
/// ReceiveMessageAsync(maxWaitTime) — no Task.Delay polling.</para>
/// </summary>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)] // 240s — emulator initialization + DLQ redelivery cycles need headroom
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusDlqAndBatchIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;
  private readonly List<IAsyncDisposable> _disposables = [];

  [After(Test)]
  public async Task DisposeTrackedTransportsAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  // ========================================
  // PUBLISHBATCHASYNC — BATCH SEND / RECORD PATHS
  // ========================================

  [Test]
  public async Task PublishBatchAsync_EmptyItemList_ReturnsEmptyResultsAsync() {
    // Arrange
    var transport = _createTransport(_publishOnlyJsonOptions());
    await transport.InitializeAsync();

    // Act
    var results = await transport.PublishBatchAsync([], new TransportDestination("topic-00"));

    // Assert — early-return path: no sender created, no broker interaction
    await Assert.That(results).IsEmpty();
  }

  [Test]
  public async Task PublishBatchAsync_TenMessages_AllSucceedAndArriveWithContentAsync() {
    // Arrange
    var transport = _createTransport(_publishOnlyJsonOptions());
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var marker = $"bulk-{Guid.CreateVersion7():N}";
    var envelopes = Enumerable.Range(0, 10)
      .Select(i => _createTestEnvelope($"{marker}-{i}"))
      .ToList();
    var items = envelopes.Select(e => _createBulkItem(e)).ToList();
    var contentById = envelopes.ToDictionary(
      e => e.MessageId.Value.ToString(),
      e => e.Payload.Content);

    // Act
    var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-00"));

    // Assert — every item reports success
    await Assert.That(results).Count().IsEqualTo(10);
    foreach (var result in results) {
      await Assert.That(result.Success).IsTrue()
        .Because($"Item {result.MessageId} should publish successfully (Error: {result.Error})");
      await Assert.That(result.Error).IsNull();
    }

    // Assert — all 10 messages arrive with their own content and envelope metadata
    var received = await _receiveMessagesByIdAsync("topic-00", "sub-00-a", [.. contentById.Keys]);
    await Assert.That(received).Count().IsEqualTo(10);

    var expectedEnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    foreach (var (messageId, message) in received) {
      await Assert.That(message.Body.ToString()).Contains(contentById[messageId])
        .Because("each wire message must carry its own serialized payload");
      await Assert.That(message.Subject).IsEqualTo("message")
        .Because("no routing key was supplied, so the default subject applies");
      await Assert.That(message.ApplicationProperties["EnvelopeType"]?.ToString())
        .IsEqualTo(expectedEnvelopeType);
    }
  }

  [Test]
  public async Task PublishBatchAsync_ItemsWithStreamIds_SetsSessionIdOnDeliveredMessagesAsync() {
    // Arrange — two stream groups + one null-stream group exercises the
    // group-by-StreamId + Parallel.ForEachAsync fan-out
    var transport = _createTransport(_publishOnlyJsonOptions());
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var streamA = Guid.CreateVersion7();
    var streamB = Guid.CreateVersion7();

    var envelopeA1 = _createTestEnvelope("stream-a-1");
    var envelopeA2 = _createTestEnvelope("stream-a-2");
    var envelopeB1 = _createTestEnvelope("stream-b-1");
    var envelopeNull = _createTestEnvelope("stream-none");

    var items = new List<BulkPublishItem> {
      _createBulkItem(envelopeA1, streamA),
      _createBulkItem(envelopeA2, streamA),
      _createBulkItem(envelopeB1, streamB),
      _createBulkItem(envelopeNull)
    };

    // Act
    var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-00"));

    // Assert — all groups sent successfully
    await Assert.That(results).Count().IsEqualTo(4);
    foreach (var result in results) {
      await Assert.That(result.Success).IsTrue()
        .Because($"Item {result.MessageId} should publish successfully (Error: {result.Error})");
    }

    // Assert — SessionId is stamped from StreamId per item
    var expectedIds = items.Select(i => i.MessageId.ToString()).ToHashSet();
    var received = await _receiveMessagesByIdAsync("topic-00", "sub-00-a", expectedIds);
    await Assert.That(received).Count().IsEqualTo(4);

    await Assert.That(received[envelopeA1.MessageId.Value.ToString()].SessionId).IsEqualTo(streamA.ToString());
    await Assert.That(received[envelopeA2.MessageId.Value.ToString()].SessionId).IsEqualTo(streamA.ToString());
    await Assert.That(received[envelopeB1.MessageId.Value.ToString()].SessionId).IsEqualTo(streamB.ToString());
    await Assert.That(string.IsNullOrEmpty(received[envelopeNull.MessageId.Value.ToString()].SessionId)).IsTrue()
      .Because("items without a StreamId must not get a SessionId");
  }

  [Test]
  public async Task PublishBatchAsync_PayloadsExceedingSingleBatchCapacity_SplitsBatchesAndDeliversAllAsync() {
    // Arrange — 6 × ~200KB payloads exceed a single ServiceBusMessageBatch
    // (emulator advertises the 256KB Standard-tier ceiling), forcing the
    // TryAddMessage-false → send-and-start-new-batch split path
    var transport = _createTransport(_publishOnlyJsonOptions());
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var marker = $"split-{Guid.CreateVersion7():N}";
    var envelopes = Enumerable.Range(0, 6)
      .Select(i => _createTestEnvelope($"{marker}-{i}-{new string('x', 200_000)}"))
      .ToList();
    var items = envelopes.Select(e => _createBulkItem(e)).ToList();

    // Act
    var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-00"));

    // Assert — every item succeeded even though multiple broker batches were required
    await Assert.That(results).Count().IsEqualTo(6);
    foreach (var result in results) {
      await Assert.That(result.Success).IsTrue()
        .Because($"Item {result.MessageId} should publish successfully across batch splits (Error: {result.Error})");
    }

    var expectedIds = envelopes.Select(e => e.MessageId.Value.ToString()).ToHashSet();
    var received = await _receiveMessagesByIdAsync("topic-00", "sub-00-a", expectedIds);
    await Assert.That(received).Count().IsEqualTo(6)
      .Because("all messages must be delivered regardless of how they were split across batches");
  }

  [Test]
  public async Task PublishBatchAsync_OversizedItem_ReportsPerItemFailureAndDeliversRemainingItemsAsync() {
    // Arrange — a ~2MB payload can never fit in any batch, even a fresh one;
    // the transport must record a per-item failure and keep going
    var transport = _createTransport(_publishOnlyJsonOptions());
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var goodBefore = _createTestEnvelope("oversized-test-good-before");
    var oversized = _createTestEnvelope($"oversized-{new string('x', 2_000_000)}");
    var goodAfter = _createTestEnvelope("oversized-test-good-after");

    var items = new List<BulkPublishItem> {
      _createBulkItem(goodBefore),
      _createBulkItem(oversized),
      _createBulkItem(goodAfter)
    };

    // Act
    var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-00"));

    // Assert — oversized item fails with the batch-size error, good items succeed
    await Assert.That(results).Count().IsEqualTo(3);

    var oversizedResult = results.Single(r => r.MessageId == oversized.MessageId.Value);
    await Assert.That(oversizedResult.Success).IsFalse();
    await Assert.That(oversizedResult.Error).IsNotNull();
    await Assert.That(oversizedResult.Error!).Contains("exceeds maximum batch message size");

    await Assert.That(results.Single(r => r.MessageId == goodBefore.MessageId.Value).Success).IsTrue();
    await Assert.That(results.Single(r => r.MessageId == goodAfter.MessageId.Value).Success).IsTrue();

    // Assert — only the two good messages arrive
    var expectedIds = new HashSet<string> {
      goodBefore.MessageId.Value.ToString(),
      goodAfter.MessageId.Value.ToString()
    };
    var received = await _receiveMessagesByIdAsync("topic-00", "sub-00-a", expectedIds);
    await Assert.That(received).Count().IsEqualTo(2);
  }

  [Test]
  public async Task PublishBatchAsync_UnserializableItem_ReportsPerItemFailureAndDeliversValidItemAsync() {
    // Arrange — UnserializableBatchMessage has no JsonTypeInfo in TestJsonContext,
    // so _createServiceBusMessage throws for that item and the batching loop's
    // per-item catch records the failure without sinking the whole batch
    var transport = _createTransport(_publishOnlyJsonOptions());
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var good = _createTestEnvelope("unserializable-test-good");
    var bad = _createUnserializableEnvelope("cannot-serialize-me");

    var items = new List<BulkPublishItem> {
      _createBulkItem(good),
      _createBulkItem(bad)
    };

    // Act
    var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-00"));

    // Assert — per-item failure for the unserializable envelope
    await Assert.That(results).Count().IsEqualTo(2);

    var badResult = results.Single(r => r.MessageId == bad.MessageId.Value);
    await Assert.That(badResult.Success).IsFalse();
    await Assert.That(badResult.Error).IsNotNull()
      .Because("serialization failures must surface as per-item errors, not exceptions");

    var goodResult = results.Single(r => r.MessageId == good.MessageId.Value);
    await Assert.That(goodResult.Success).IsTrue();

    // Assert — the good message still arrives
    var received = await _receiveMessagesByIdAsync(
      "topic-00", "sub-00-a", [good.MessageId.Value.ToString()]);
    await Assert.That(received).Count().IsEqualTo(1);
  }

  // ========================================
  // SUBSCRIBEBATCHASYNC — NON-SESSION AND SESSION PATHS
  // ========================================

  [Test]
  public async Task SubscribeBatchAsync_NonSessionSubscription_DeliversAllPublishedMessagesAsync() {
    // Arrange — non-session batch subscription drives the TransportBatchCollector
    // flush path (deserialize → batch handler → per-message complete)
    var options = new AzureServiceBusOptions { EnableSessions = false };
    var transport = _createTransport(JsonContextRegistry.CreateCombinedOptions(), options);
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-00", "sub-00-a");

    var envelopes = Enumerable.Range(0, 5)
      .Select(i => _createTestEnvelope($"batch-subscribe-{i}"))
      .ToList();
    var expectedIds = envelopes.Select(e => e.MessageId.Value).ToHashSet();

    var receivedChannel = Channel.CreateUnbounded<Guid>();

    var subscription = await transport.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var transportMessage in batch) {
          if (expectedIds.Contains(transportMessage.Envelope.MessageId.Value)) {
            await receivedChannel.Writer.WriteAsync(transportMessage.Envelope.MessageId.Value, ct);
          }
        }
      },
      new TransportDestination("topic-00", "sub-00-a"),
      new TransportBatchOptions { BatchSize = 5, SlideMs = 100, MaxWaitMs = 2000 }
    );

    try {
      // Act — publish all 5 through the bulk path
      var items = envelopes.Select(e => _createBulkItem(e)).ToList();
      var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-00"));
      foreach (var result in results) {
        await Assert.That(result.Success).IsTrue()
          .Because($"Item {result.MessageId} should publish successfully (Error: {result.Error})");
      }

      // Assert — the batch handler receives every published message
      var receivedIds = new HashSet<Guid>();
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
      while (receivedIds.Count < 5) {
        receivedIds.Add(await receivedChannel.Reader.ReadAsync(cts.Token));
      }

      await Assert.That(receivedIds.SetEquals(expectedIds)).IsTrue()
        .Because("the batch subscription must deliver exactly the published messages");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task SubscribeBatchAsync_SessionSubscription_DeliversSessionMessagesInOrderAsync() {
    // Arrange — session-enabled batch subscription (topic-fifo-02/sub-fifo-session has
    // RequiresSession=true in Config.json) drives _startSessionBatchSubscriptionAsync
    // and the single-item _handleSessionBatchMessageAsync path
    var options = new AzureServiceBusOptions { EnableSessions = true };
    var transport = _createTransport(JsonContextRegistry.CreateCombinedOptions(), options);
    await transport.InitializeAsync();

    var streamId = Guid.CreateVersion7();
    var marker = $"session-batch-{Guid.CreateVersion7():N}";
    var envelopes = Enumerable.Range(0, 5)
      .Select(i => _createTestEnvelope($"{marker}-{i}"))
      .ToList();

    var receivedChannel = Channel.CreateUnbounded<string>();

    var subscription = await transport.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var transportMessage in batch) {
          if (transportMessage.Envelope is MessageEnvelope<TestMessage> testEnvelope &&
              testEnvelope.Payload.Content.StartsWith(marker, StringComparison.Ordinal)) {
            await receivedChannel.Writer.WriteAsync(testEnvelope.Payload.Content, ct);
          }
        }
      },
      new TransportDestination("topic-fifo-02", "sub-fifo-session"),
      new TransportBatchOptions()
    );

    try {
      // Act — publish 5 messages in one session so ordering is guaranteed
      var items = envelopes.Select(e => _createBulkItem(e, streamId)).ToList();
      var results = await transport.PublishBatchAsync(items, new TransportDestination("topic-fifo-02"));
      foreach (var result in results) {
        await Assert.That(result.Success).IsTrue()
          .Because($"Item {result.MessageId} should publish successfully (Error: {result.Error})");
      }

      // Assert — session batch handler receives all 5 in publish order (FIFO per session)
      var receivedContents = new List<string>();
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
      for (var i = 0; i < 5; i++) {
        receivedContents.Add(await receivedChannel.Reader.ReadAsync(cts.Token));
      }

      for (var i = 0; i < 5; i++) {
        await Assert.That(receivedContents[i]).IsEqualTo($"{marker}-{i}")
          .Because($"session FIFO must preserve publish order at position {i}");
      }
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // DEAD-LETTER / SETTLEMENT PATHS
  // ========================================

  [Test]
  public async Task SubscribeAsync_MessageWithoutEnvelopeTypeHeader_DeadLettersWithMissingEnvelopeTypeReasonAsync() {
    // Arrange — a message with no EnvelopeType ApplicationProperty has nothing to
    // route on; the decision maker returns DeadLetter and the transport settles it
    // via _safeDeadLetterAsync with the decision's reason + description
    var options = new AzureServiceBusOptions { EnableSessions = false };
    var transport = _createTransport(JsonContextRegistry.CreateCombinedOptions(), options);
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-01", "sub-01-a");
    await _drainDeadLetterQueueAsync("topic-01", "sub-01-a");

    var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask,
      new TransportDestination("topic-01", "sub-01-a")
    );

    try {
      // Act — send a raw message WITHOUT the EnvelopeType property
      var rawMessageId = Guid.CreateVersion7().ToString();
      var sender = _fixture.Client.CreateSender("topic-01");
      try {
        await sender.SendMessageAsync(new ServiceBusMessage("{}") {
          MessageId = rawMessageId,
          ContentType = "application/json"
        });
      } finally {
        await sender.DisposeAsync();
      }

      // Assert — the message lands in the subscription's dead-letter sub-queue
      var deadLettered = await _receiveDeadLetteredMessageAsync("topic-01", "sub-01-a", rawMessageId);
      await Assert.That(deadLettered).IsNotNull()
        .Because("a message without EnvelopeType metadata must be dead-lettered by the transport");
      await Assert.That(deadLettered!.DeadLetterReason).IsEqualTo("MissingEnvelopeType");
      await Assert.That(deadLettered.DeadLetterErrorDescription)
        .IsEqualTo("Message does not contain EnvelopeType metadata");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task SubscribeAsync_HandlerThrowsOnEveryDelivery_DeadLettersAfterMaxDeliveryAttemptsAsync() {
    // Arrange — MaxDeliveryAttempts=2: first delivery abandons (redelivery), second
    // delivery dead-letters via _handleMessageProcessingErrorAsync. Keeps below the
    // broker's MaxDeliveryCount=3 so the TRANSPORT's dead-letter branch fires,
    // not the broker's own count-based dead-lettering.
    var failureMarker = $"handler-failure-{Guid.CreateVersion7():N}";
    var options = new AzureServiceBusOptions { EnableSessions = false, MaxDeliveryAttempts = 2 };
    var transport = _createTransport(JsonContextRegistry.CreateCombinedOptions(), options);
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-01", "sub-01-a");
    await _drainDeadLetterQueueAsync("topic-01", "sub-01-a");

    var handlerInvocations = 0;
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => {
        Interlocked.Increment(ref handlerInvocations);
        throw new InvalidOperationException(failureMarker);
      },
      new TransportDestination("topic-01", "sub-01-a")
    );

    try {
      // Act — publish a well-formed envelope so it reaches the (always-throwing) handler
      var envelope = _createTestEnvelope("poison-message-content");
      await transport.PublishAsync(envelope, new TransportDestination("topic-01"));

      // Assert — DLQ arrival is the completion signal for the abandon → redeliver → DLQ cycle
      var deadLettered = await _receiveDeadLetteredMessageAsync(
        "topic-01", "sub-01-a", envelope.MessageId.Value.ToString());

      await Assert.That(deadLettered).IsNotNull()
        .Because("a message whose handler always throws must be dead-lettered after MaxDeliveryAttempts");
      await Assert.That(deadLettered!.DeadLetterReason).IsEqualTo("MaxDeliveryAttemptsExceeded");
      await Assert.That(deadLettered.DeadLetterErrorDescription).IsEqualTo(failureMarker)
        .Because("the dead-letter description carries the handler's exception message");
      await Assert.That(deadLettered.DeliveryCount).IsEqualTo(2)
        .Because("the transport dead-letters on the delivery that reaches MaxDeliveryAttempts");
      await Assert.That(Volatile.Read(ref handlerInvocations)).IsEqualTo(2)
        .Because("first delivery abandons (retry), second delivery dead-letters");
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // POISON QUARANTINE — TOPOLOGY ARC PHASE 8.5
  // ========================================

  [Test]
  public async Task SubscribeAsync_AgedSessionMessage_QuarantinesToTheEntityDlqWithDeliveryCountOneAsync() {
    // The arc's motivating failure, at broker tier on a SESSION-enabled subscription.
    //
    // The emulator spike and a live Standard-namespace probe both established that a lock lost to
    // connection death on a session entity does NOT increment DeliveryCount. So the broker's
    // MaxDeliveryCount valve and the transport's MaxDeliveryAttempts branch — both reading that
    // counter — can never fire under a consumer-death storm, and the message is hostage forever.
    // This test proves the replacement works where the counter cannot: the message quarantines on
    // AGE with DeliveryCount still at 1, and lands in the real per-entity DLQ that the existing
    // dead-letter drainer already replays.
    //
    // AgeThreshold = zero makes any already-enqueued message aged, which is the only way to write
    // this as a bounded test — the derivation (renewal x attempts, floored at 30 minutes) is
    // property-locked in Whizbang.Core.Tests, not re-derived here.
    var options = new AzureServiceBusOptions { EnableSessions = true };
    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      JsonContextRegistry.CreateCombinedOptions(),
      options,
      logger: null,
      adminClient: null,
      poisonDetector: _poisonDetector(TimeSpan.Zero));
    _disposables.Add(transport);
    await transport.InitializeAsync();
    await _drainDeadLetterQueueAsync("topic-fifo-02", "sub-fifo-session");

    var handlerInvoked = 0;
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => { Interlocked.Increment(ref handlerInvoked); return Task.CompletedTask; },
      new TransportDestination("topic-fifo-02", "sub-fifo-session")
    );

    try {
      // Session entities REQUIRE a SessionId; the bulk path stamps it from StreamId.
      var envelope = _createTestEnvelope($"aged-session-{Guid.CreateVersion7():N}");
      await transport.PublishBatchAsync(
        [_createBulkItem(envelope, Guid.CreateVersion7())],
        new TransportDestination("topic-fifo-02"));

      var deadLettered = await _receiveDeadLetteredMessageAsync(
        "topic-fifo-02", "sub-fifo-session", envelope.MessageId.Value.ToString());

      await Assert.That(deadLettered).IsNotNull()
        .Because("an aged session message must reach the DLQ on age alone");
      await Assert.That(deadLettered!.DeadLetterReason).IsEqualTo("PoisonQuarantine");
      await Assert.That(deadLettered.DeliveryCount).IsEqualTo(1)
        .Because("the counter every legacy valve reads never moved — that is the whole point");
      await Assert.That(Volatile.Read(ref handlerInvoked)).IsEqualTo(0)
        .Because("quarantine happens at the receive boundary, before the handler");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task SubscribeAsync_FreshSessionMessage_IsNotQuarantinedAsync() {
    // The negative half of the same lock: with a real (derived-scale) threshold, a message
    // published moments ago flows to the handler untouched. Without this, a passing quarantine
    // test would be indistinguishable from "quarantines everything".
    var options = new AzureServiceBusOptions { EnableSessions = true };
    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      JsonContextRegistry.CreateCombinedOptions(),
      options,
      logger: null,
      adminClient: null,
      poisonDetector: _poisonDetector(TimeSpan.FromHours(6)));
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var received = Channel.CreateUnbounded<string>();
    var marker = $"fresh-session-{Guid.CreateVersion7():N}";
    var subscription = await transport.SubscribeAsync(
      async (env, _, _) => {
        if (env.Payload is TestMessage m) { await received.Writer.WriteAsync(m.Content, CancellationToken.None); }
      },
      new TransportDestination("topic-fifo-02", "sub-fifo-session")
    );

    try {
      await transport.PublishBatchAsync(
        [_createBulkItem(_createTestEnvelope(marker), Guid.CreateVersion7())],
        new TransportDestination("topic-fifo-02"));

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
      string? delivered = null;
      while (delivered != marker) {
        delivered = await received.Reader.ReadAsync(cts.Token);
      }

      await Assert.That(delivered).IsEqualTo(marker)
        .Because("a fresh message must never be quarantined");
    } finally {
      subscription.Dispose();
    }
  }

  /// <summary>Real Core detector with an explicit age threshold; layer 2 is unreachable here
  /// (the transport boundary reports no durable observation count), so any quarantine is layer 1.</summary>
  private static Whizbang.Core.Routing.PoisonMessageDetector _poisonDetector(TimeSpan ageThreshold) =>
    new Whizbang.Core.Routing.PoisonMessageDetector(
      Microsoft.Extensions.Options.Options.Create(new Whizbang.Core.Routing.PoisonMessageOptions {
        AgeThreshold = ageThreshold,
      }),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<Whizbang.Core.Routing.PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Transports.AzureServiceBus.Integration.Tests.Poison"));

  [Test]
  public async Task SubscribeAsync_MalformedJsonBody_AcksAndDropsWithoutDeadLetteringAsync() {
    // Arrange — slice 1 hotfix behavior: a body that fails deserialization for a
    // KNOWN envelope type is acked + dropped (completed at the broker), NOT
    // dead-lettered. MaxConcurrentCalls=1 guarantees the malformed message is fully
    // settled before the subsequent valid message reaches the handler.
    var options = new AzureServiceBusOptions { EnableSessions = false, MaxConcurrentCalls = 1 };
    var transport = _createTransport(JsonContextRegistry.CreateCombinedOptions(), options);
    await transport.InitializeAsync();
    await _drainMessagesAsync("topic-01", "sub-01-a");
    await _drainDeadLetterQueueAsync("topic-01", "sub-01-a");

    var validEnvelope = _createTestEnvelope("valid-after-malformed");
    var validAwaiter = new MessageAwaiter<IMessageEnvelope>(
      envelope => envelope.MessageId.Value == validEnvelope.MessageId.Value ? envelope : null
    );

    var subscription = await transport.SubscribeAsync(
      validAwaiter.Handler,
      new TransportDestination("topic-01", "sub-01-a")
    );

    var malformedMessageId = Guid.CreateVersion7().ToString();

    try {
      // Act — send malformed body FIRST (known EnvelopeType, garbage JSON), then a
      // valid envelope; with MaxConcurrentCalls=1 processing order matches enqueue order
      var sender = _fixture.Client.CreateSender("topic-01");
      try {
        var malformed = new ServiceBusMessage("this is {{{ not json") {
          MessageId = malformedMessageId,
          ContentType = "application/json"
        };
        malformed.ApplicationProperties["EnvelopeType"] =
          typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName;
        await sender.SendMessageAsync(malformed);
      } finally {
        await sender.DisposeAsync();
      }

      await transport.PublishAsync(validEnvelope, new TransportDestination("topic-01"));

      // The valid message reaching the handler proves the malformed one was already settled
      var received = await validAwaiter.WaitAsync(TimeSpan.FromSeconds(60));
      await Assert.That(received.MessageId.Value).IsEqualTo(validEnvelope.MessageId.Value);
    } finally {
      subscription.Dispose();
    }

    // Assert — NOT dead-lettered: the DLQ contains no trace of the malformed message
    var dlqReceiver = _fixture.Client.CreateReceiver("topic-01", "sub-01-a",
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      var dlqMessages = await dlqReceiver.PeekMessagesAsync(maxMessages: 100);
      await Assert.That(dlqMessages.Any(m => m.MessageId == malformedMessageId)).IsFalse()
        .Because("deserialization failures are acked + dropped, never dead-lettered (slice 1 hotfix)");
    } finally {
      await dlqReceiver.DisposeAsync();
    }

    // Assert — completed at the broker: the malformed message is gone from the subscription
    var receiver = _fixture.Client.CreateReceiver("topic-01", "sub-01-a");
    try {
      for (var i = 0; i < 2; i++) {
        var residual = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(3));
        if (residual == null) {
          break;
        }
        await Assert.That(residual.MessageId).IsNotEqualTo(malformedMessageId)
          .Because("the malformed message must have been completed (acked) by the transport");
        await receiver.CompleteMessageAsync(residual);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private AzureServiceBusTransport _createTransport(
    JsonSerializerOptions jsonOptions,
    AzureServiceBusOptions? options = null
  ) {
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    _disposables.Add(transport);
    return transport;
  }

  private static JsonSerializerOptions _publishOnlyJsonOptions() {
    // Deliberately limited to TestJsonContext so UnserializableBatchMessage
    // (registered nowhere) reliably fails serialization in the per-item error test
    return new JsonSerializerOptions { TypeInfoResolver = TestJsonContext.Default };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope(string content) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(Guid.CreateVersion7().ToString())
          }
        }
      ]
    };
  }

  private static MessageEnvelope<UnserializableBatchMessage> _createUnserializableEnvelope(string content) {
    return new MessageEnvelope<UnserializableBatchMessage> {
      MessageId = MessageId.New(),
      Payload = new UnserializableBatchMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(Guid.CreateVersion7().ToString())
          }
        }
      ]
    };
  }

  private static BulkPublishItem _createBulkItem(IMessageEnvelope envelope, Guid? streamId = null) {
    return new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = envelope.GetType().AssemblyQualifiedName,
      MessageId = envelope.MessageId.Value,
      StreamId = streamId
    };
  }

  /// <summary>
  /// Receives from the subscription until every expected MessageId has been seen
  /// (or the broker reports empty three times in a row). Every received message is
  /// completed; stray messages from earlier runs are completed and ignored.
  /// Uses broker-side ReceiveMessageAsync(maxWaitTime) — not client-side polling.
  /// </summary>
  private async Task<Dictionary<string, ServiceBusReceivedMessage>> _receiveMessagesByIdAsync(
    string topicName,
    string subscriptionName,
    HashSet<string> expectedMessageIds
  ) {
    var found = new Dictionary<string, ServiceBusReceivedMessage>();
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      var consecutiveEmptyReceives = 0;
      while (found.Count < expectedMessageIds.Count && consecutiveEmptyReceives < 3) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        if (message == null) {
          consecutiveEmptyReceives++;
          continue;
        }

        consecutiveEmptyReceives = 0;
        await receiver.CompleteMessageAsync(message);
        if (expectedMessageIds.Contains(message.MessageId)) {
          found[message.MessageId] = message;
        }
      }
    } finally {
      await receiver.DisposeAsync();
    }
    return found;
  }

  /// <summary>
  /// Waits (broker-side) for a specific message to arrive in the subscription's
  /// dead-letter sub-queue. Completes and returns the match; completes and skips
  /// stray dead-lettered messages from earlier runs. Returns null if the message
  /// never arrives within ~60 seconds of broker-side waits.
  /// </summary>
  private async Task<ServiceBusReceivedMessage?> _receiveDeadLetteredMessageAsync(
    string topicName,
    string subscriptionName,
    string expectedMessageId
  ) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      for (var attempt = 0; attempt < 12; attempt++) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        if (message == null) {
          continue;
        }

        await receiver.CompleteMessageAsync(message);
        if (message.MessageId == expectedMessageId) {
          return message;
        }
      }
      return null;
    } finally {
      await receiver.DisposeAsync();
    }
  }

  private async Task _drainMessagesAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      for (var i = 0; i < 100; i++) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (message == null) {
          break;
        }
        await receiver.CompleteMessageAsync(message);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

  private async Task _drainDeadLetterQueueAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      for (var i = 0; i < 100; i++) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (message == null) {
          break;
        }
        await receiver.CompleteMessageAsync(message);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }
}
