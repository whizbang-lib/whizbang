using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
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

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Unit tests for RabbitMQTransport batch paths:
/// <list type="bullet">
///   <item>PublishBatchAsync — success, per-item failure, whole-batch failure (_failRemainingItems),
///     pre-serialized bytes hint, metadata merging, routing-key override</item>
///   <item>SubscribeBatchAsync — subscription-creation error wrapping, batch flush (size + timer triggers),
///     per-message ACK/NACK, deserialization-failure dead-lettering, paused-subscription NACK,
///     channel-closed swallowing on ACK/NACK</item>
/// </list>
/// All broker interaction goes through <see cref="RecordingChannel"/>; batch flushes run on the thread
/// pool, so tests synchronize exclusively via completion signals (semaphores released on ACK/NACK
/// attempts, TaskCompletionSource fed by the capturing logger) — never Task.Delay or polling.
/// </summary>
public class RabbitMQTransportBatchPathTests {

  #region PublishBatchAsync

  [Test]
  public async Task PublishBatchAsync_NotInitialized_ThrowsInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger: null);
    var items = new List<BulkPublishItem> { _newItem(RabbitTestWire.NewEnvelope()) };

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange"))
    );
  }

  [Test]
  public async Task PublishBatchAsync_EmptyItems_ReturnsEmptyResultsWithoutRentingChannelAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    var results = await transport.PublishBatchAsync([], new TransportDestination("batch-exchange"));

    await Assert.That(results).IsEmpty();
    await Assert.That(channel.Published).IsEmpty();
    await Assert.That(channel.DeclaredExchanges).IsEmpty();
  }

  [Test]
  public async Task PublishBatchAsync_AllItemsSucceed_PublishesAllAndReportsSuccessAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var items = new List<BulkPublishItem> {
      _newItem(RabbitTestWire.NewEnvelope("one")),
      _newItem(RabbitTestWire.NewEnvelope("two"))
    };

    var results = await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange", "orders.#"));

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(results[1].Success).IsTrue();
    await Assert.That(results[0].MessageId).IsEqualTo(items[0].MessageId);
    await Assert.That(results[1].MessageId).IsEqualTo(items[1].MessageId);
    await Assert.That(channel.Published).Count().IsEqualTo(2);
    await Assert.That(channel.Published[0].Exchange).IsEqualTo("batch-exchange");
    await Assert.That(channel.Published[0].RoutingKey).IsEqualTo("orders.#");
    await Assert.That(channel.Published[0].Body.Length).IsGreaterThan(0);
    await Assert.That(channel.DeclaredExchanges).Count().IsEqualTo(1)
      .Because("The exchange must be declared exactly once per batch (cached declaration).");
  }

  [Test]
  public async Task PublishBatchAsync_OneItemFailsToPublish_ReportsPerItemFailureAndContinuesAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    var goodEnvelope = RabbitTestWire.NewEnvelope("good");
    var badEnvelope = RabbitTestWire.NewEnvelope("bad");
    var badWireMessageId = badEnvelope.MessageId.Value.ToString();
    channel.PublishExceptionSelector = messageId =>
      messageId == badWireMessageId ? new InvalidOperationException("broker rejected") : null;

    var items = new List<BulkPublishItem> { _newItem(goodEnvelope), _newItem(badEnvelope) };

    var results = await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange"));

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(results[0].Error).IsNull();
    await Assert.That(results[1].Success).IsFalse();
    await Assert.That(results[1].Error!).Contains("InvalidOperationException");
    await Assert.That(results[1].Error!).Contains("broker rejected");
    await Assert.That(channel.Published).Count().IsEqualTo(1)
      .Because("Only the successful item reaches the wire; the failing item is reported per-item.");
  }

  [Test]
  public async Task PublishBatchAsync_ChannelRentThrowsAlreadyClosed_FailsAllItemsAsync() {
    var connection = new FakeConnection(() =>
      Task.FromException<IChannel>(RabbitTestWire.NewAlreadyClosedException()));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var items = new List<BulkPublishItem> {
      _newItem(RabbitTestWire.NewEnvelope("one")),
      _newItem(RabbitTestWire.NewEnvelope("two"))
    };

    var results = await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange"));

    await Assert.That(results).Count().IsEqualTo(2)
      .Because("_failRemainingItems must add a failure result for every item not yet recorded.");
    await Assert.That(results.All(r => !r.Success)).IsTrue();
    await Assert.That(results[0].Error!).Contains("AlreadyClosedException");
    await Assert.That(results[1].Error!).Contains("AlreadyClosedException");
    await Assert.That(results[0].MessageId).IsEqualTo(items[0].MessageId);
    await Assert.That(results[1].MessageId).IsEqualTo(items[1].MessageId);
  }

  [Test]
  public async Task PublishBatchAsync_ExchangeDeclareThrowsGeneric_FailsAllItemsWithErrorTypeAsync() {
    var channel = new RecordingChannel {
      ExceptionToThrowOnExchangeDeclare = new InvalidOperationException("declare failed")
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var items = new List<BulkPublishItem> {
      _newItem(RabbitTestWire.NewEnvelope("one")),
      _newItem(RabbitTestWire.NewEnvelope("two"))
    };

    var results = await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange"));

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results.All(r => !r.Success)).IsTrue();
    await Assert.That(results[0].Error!).Contains("InvalidOperationException");
    await Assert.That(results[0].Error!).Contains("declare failed");
    await Assert.That(channel.Published).IsEmpty();
  }

  [Test]
  public async Task PublishBatchAsync_ItemWithPreSerializedBytes_PublishesHintBytesVerbatimAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var sentinel = "BATCH_SENTINEL_BYTES_NOT_JSON"u8.ToArray();
    var items = new List<BulkPublishItem> {
      _newItem(RabbitTestWire.NewEnvelope(), preSerialized: sentinel)
    };

    var results = await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange"));

    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(channel.Published).Count().IsEqualTo(1);
    await Assert.That(channel.Published[0].Body).IsEquivalentTo(sentinel)
      .Because("Per-item pre-serialized bytes MUST be sent as-is — re-serializing would defeat the upstream post-serialize hook chain.");
  }

  [Test]
  public async Task PublishBatchAsync_PerItemMetadataOverridesDestinationMetadataAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    var destinationMetadata = new Dictionary<string, JsonElement> {
      ["shared"] = JsonDocument.Parse("\"from-destination\"").RootElement.Clone(),
      ["overlap"] = JsonDocument.Parse("\"from-destination\"").RootElement.Clone()
    };
    var perItemMetadata = new Dictionary<string, JsonElement> {
      ["overlap"] = JsonDocument.Parse("\"from-item\"").RootElement.Clone(),
      ["extra"] = JsonDocument.Parse("7").RootElement.Clone()
    };
    var items = new List<BulkPublishItem> {
      _newItem(RabbitTestWire.NewEnvelope(), perItemMetadata: perItemMetadata)
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("batch-exchange", null, destinationMetadata));

    await Assert.That(results[0].Success).IsTrue();
    var headers = channel.Published[0].Properties.Headers!;
    await Assert.That((string)headers["shared"]!).IsEqualTo("from-destination");
    await Assert.That((string)headers["overlap"]!).IsEqualTo("from-item")
      .Because("Per-item metadata overrides shared destination metadata for the same key — that's the contract.");
    await Assert.That((int)headers["extra"]!).IsEqualTo(7);
    await Assert.That(headers.ContainsKey("EnvelopeType")).IsTrue();
  }

  [Test]
  public async Task PublishBatchAsync_ItemRoutingKeyOverridesDestinationRoutingKeyAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var items = new List<BulkPublishItem> {
      _newItem(RabbitTestWire.NewEnvelope("one"), routingKey: "custom.key"),
      _newItem(RabbitTestWire.NewEnvelope("two"))
    };

    var results = await transport.PublishBatchAsync(items, new TransportDestination("batch-exchange"));

    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(channel.Published).Count().IsEqualTo(2);
    await Assert.That(channel.Published[0].RoutingKey).IsEqualTo("custom.key");
    await Assert.That(channel.Published[1].RoutingKey).IsEqualTo("#")
      .Because("With no per-item and no destination routing key, the transport falls back to '#'.");
  }

  #endregion

  #region SubscribeBatchAsync — subscription creation

  [Test]
  public async Task SubscribeBatchAsync_QueueBindThrowsTaskCanceled_WrapsInTimeoutInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel {
      ExceptionToThrowOnQueueBind = new TaskCanceledException("A task was canceled.")
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    InvalidOperationException? caught = null;
    try {
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask, RabbitTestWire.Destination(), new TransportBatchOptions());
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("timed out")
      .Because("An OperationCanceledException without caller cancellation means a broker timeout and must be wrapped with diagnostic context.");
  }

  [Test]
  public async Task SubscribeBatchAsync_QueueBindThrowsGeneric_WrapsInInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel {
      ExceptionToThrowOnQueueBind = new NotSupportedException("bind exploded")
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    InvalidOperationException? caught = null;
    try {
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask, RabbitTestWire.Destination(), new TransportBatchOptions());
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to create RabbitMQ subscription");
    await Assert.That(caught.InnerException).IsTypeOf<NotSupportedException>();
  }

  [Test]
  public async Task SubscribeBatchAsync_WithDebugLogger_LogsBatchSubscriptionCreatedAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask, RabbitTestWire.Destination(), new TransportBatchOptions());

    await Assert.That(subscription).IsNotNull();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Created batch subscription", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("Creating subscription", StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region SubscribeBatchAsync — flush, ACK, NACK

  [Test]
  public async Task SubscribeBatchAsync_BatchSizeReached_InvokesHandlerWithAllMessagesAndAcksEachAsync() {
    // Size-only trigger: timers set far beyond the test lifetime.
    var options = new TransportBatchOptions { BatchSize = 3, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var (props1, body1) = RabbitTestWire.ValidWireMessage("m1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("m2");
    var (props3, body3) = RabbitTestWire.ValidWireMessage("m3");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);
    await RabbitTestWire.DeliverAsync(channel, props3, body3, 3);

    await channel.WaitForAckAttemptsAsync(3);

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0]).Count().IsEqualTo(3);
    var firstEnvelope = (MessageEnvelope<TestMessage>)batches[0][0].Envelope;
    await Assert.That(firstEnvelope.Payload.Content).IsEqualTo("m1");
    await Assert.That(batches[0][0].EnvelopeType)
      .IsEqualTo(typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName);
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(3);
    await Assert.That(channel.AckAttempts[0]).IsEqualTo(1UL);
    await Assert.That(channel.AckAttempts[1]).IsEqualTo(2UL);
    await Assert.That(channel.AckAttempts[2]).IsEqualTo(3UL);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task SubscribeBatchAsync_PartialBatchTimerFlush_InvokesHandlerAndAcksAsync() {
    // Partial batch: only the sliding-window timer can trigger the flush here (BatchSize
    // is never reached). The WAIT is still a completion signal (ACK semaphore), not a delay.
    var options = new TransportBatchOptions { BatchSize = 50, SlideMs = 50, MaxWaitMs = 10_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var (props1, body1) = RabbitTestWire.ValidWireMessage("m1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("m2");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);

    await channel.WaitForAckAttemptsAsync(2);

    // Under heavy CI load the slide timer may fire between the two deliveries and split
    // them into two flushes — the invariant is that every message is handled and ACKed.
    await Assert.That(batches.Sum(b => b.Count)).IsEqualTo(2);
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task SubscribeBatchAsync_HandlerThrows_NacksAllWithRequeueAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(
      options,
      handler: (batch, ct) => throw new InvalidOperationException("handler failed"));

    var (props1, body1) = RabbitTestWire.ValidWireMessage("m1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("m2");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);

    await channel.WaitForNackAttemptsAsync(2);

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsTrue()
      .Because("Handler failures must requeue the whole batch for redelivery, not dead-letter it.");
    await Assert.That(channel.NackAttempts[1].DeliveryTag).IsEqualTo(2UL);
    await Assert.That(channel.NackAttempts[1].Requeue).IsTrue();
    await Assert.That(channel.AckAttempts).IsEmpty();
  }

  [Test]
  public async Task SubscribeBatchAsync_HandlerThrowsAlreadyClosed_SwallowsWithoutAckOrNackAsync() {
    var logger = new CapturingLogger<RabbitMQTransport>();
    var channelClosedLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.OnLog = (level, message) => {
      if (message.Contains("channel closed while processing batch", StringComparison.Ordinal)) {
        channelClosedLogged.TrySetResult();
      }
    };

    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(
      options,
      handler: (batch, ct) => throw RabbitTestWire.NewAlreadyClosedException(),
      logger: logger);

    var (props1, body1) = RabbitTestWire.ValidWireMessage("m1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("m2");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);

    await channelClosedLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(channel.AckAttempts).IsEmpty()
      .Because("A closed channel means the broker redelivers on its own — no ACK possible.");
    await Assert.That(channel.NackAttempts).IsEmpty()
      .Because("The AlreadyClosed branch must NOT try to NACK on the same dead channel.");
  }

  [Test]
  public async Task SubscribeBatchAsync_AckThrowsAlreadyClosed_ContinuesAckingRemainingMessagesAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var channel = new RecordingChannel { ExceptionToThrowOnAck = RabbitTestWire.NewAlreadyClosedException() };
    var (_, batches, _) = await _subscribeBatchAsync(options, channel: channel);

    var (props1, body1) = RabbitTestWire.ValidWireMessage("m1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("m2");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);

    // Both ACKs must be ATTEMPTED even though every attempt throws AlreadyClosedException:
    // the per-message loop swallows channel-closed errors and moves on.
    await channel.WaitForAckAttemptsAsync(2);

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task SubscribeBatchAsync_HandlerThrowsAndNackThrowsAlreadyClosed_SwallowsAndContinuesAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var channel = new RecordingChannel { ExceptionToThrowOnNack = RabbitTestWire.NewAlreadyClosedException() };
    var (_, batches, _) = await _subscribeBatchAsync(
      options,
      handler: (batch, ct) => throw new InvalidOperationException("handler failed"),
      channel: channel);

    var (props1, body1) = RabbitTestWire.ValidWireMessage("m1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("m2");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);

    // Both NACKs attempted; each AlreadyClosedException is swallowed so the loop finishes.
    await channel.WaitForNackAttemptsAsync(2);

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.AckAttempts).IsEmpty();
  }

  #endregion

  #region SubscribeBatchAsync — deserialization failures

  [Test]
  public async Task SubscribeBatchAsync_MessageMissingEnvelopeTypeHeader_NacksToDeadLetterAndProcessesRestAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var badProps = new BasicProperties { MessageId = "missing-header" };
    var badBody = "{}"u8.ToArray();
    var (goodProps, goodBody) = RabbitTestWire.ValidWireMessage("good");
    await RabbitTestWire.DeliverAsync(channel, badProps, badBody, 1);
    await RabbitTestWire.DeliverAsync(channel, goodProps, goodBody, 2);

    await channel.WaitForNackAttemptsAsync(1);
    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("A message without an EnvelopeType header is poison — dead-letter, never requeue.");
    await Assert.That(channel.AckAttempts[0]).IsEqualTo(2UL);
    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0]).Count().IsEqualTo(1)
      .Because("The handler must only see the successfully deserialized messages.");
  }

  [Test]
  public async Task SubscribeBatchAsync_MessageWithEmptyBody_NacksToDeadLetterAndProcessesRestAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var (badProps, _) = RabbitTestWire.ValidWireMessage("ignored");
    var (goodProps, goodBody) = RabbitTestWire.ValidWireMessage("good");
    await RabbitTestWire.DeliverAsync(channel, badProps, [], 1);
    await RabbitTestWire.DeliverAsync(channel, goodProps, goodBody, 2);

    await channel.WaitForNackAttemptsAsync(1);
    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse();
    await Assert.That(channel.AckAttempts[0]).IsEqualTo(2UL);
    await Assert.That(batches[0]).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeBatchAsync_MessageWithNulPrefixedBody_NacksToDeadLetterAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var (badProps, _) = RabbitTestWire.ValidWireMessage("ignored");
    var nulBody = new byte[] { 0, 0, 0, 0 };
    var (goodProps, goodBody) = RabbitTestWire.ValidWireMessage("good");
    await RabbitTestWire.DeliverAsync(channel, badProps, nulBody, 1);
    await RabbitTestWire.DeliverAsync(channel, goodProps, goodBody, 2);

    await channel.WaitForNackAttemptsAsync(1);
    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("A body that is not JSON (NUL prefix) is poison — dead-letter it.");
    await Assert.That(batches[0]).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeBatchAsync_MessageWithUnknownEnvelopeType_NacksToDeadLetterAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var badProps = new BasicProperties {
      MessageId = "unknown-type",
      Headers = new Dictionary<string, object?> {
        ["EnvelopeType"] = Encoding.UTF8.GetBytes("Whizbang.Tests.DoesNotExist.UnknownEnvelope, Whizbang.DoesNotExist")
      }
    };
    var badBody = "{}"u8.ToArray();
    var (goodProps, goodBody) = RabbitTestWire.ValidWireMessage("good");
    await RabbitTestWire.DeliverAsync(channel, badProps, badBody, 1);
    await RabbitTestWire.DeliverAsync(channel, goodProps, goodBody, 2);

    await channel.WaitForNackAttemptsAsync(1);
    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("No registered JsonTypeInfo for the declared envelope type — dead-letter the message.");
    await Assert.That(batches[0]).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeBatchAsync_MessageDeserializesToNonEnvelope_NacksToDeadLetterAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var (badProps, badBody) = RabbitTestWire.NonEnvelopeWireMessage();
    var (goodProps, goodBody) = RabbitTestWire.ValidWireMessage("good");
    await RabbitTestWire.DeliverAsync(channel, badProps, badBody, 1);
    await RabbitTestWire.DeliverAsync(channel, goodProps, goodBody, 2);

    await channel.WaitForNackAttemptsAsync(1);
    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("A payload that deserializes to something other than IMessageEnvelope must be dead-lettered.");
    await Assert.That(batches[0]).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SubscribeBatchAsync_AllMessagesFailDeserialization_HandlerNotInvokedAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    var badProps1 = new BasicProperties { MessageId = "bad-1" };
    var badProps2 = new BasicProperties { MessageId = "bad-2" };
    var badBody = "{}"u8.ToArray();
    await RabbitTestWire.DeliverAsync(channel, badProps1, badBody, 1);
    await RabbitTestWire.DeliverAsync(channel, badProps2, badBody, 2);

    await channel.WaitForNackAttemptsAsync(2);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.NackAttempts.All(n => !n.Requeue)).IsTrue();
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(batches).IsEmpty()
      .Because("When every message in the batch fails deserialization, the handler must not run at all.");
  }

  [Test]
  public async Task SubscribeBatchAsync_PropertiesThrowObjectDisposedDuringDeserialize_DropsMessageWithoutNackAsync() {
    var options = new TransportBatchOptions { BatchSize = 2, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, _) = await _subscribeBatchAsync(options);

    // Headers access throws ObjectDisposedException mid-deserialization — the transport must
    // treat it as a closed channel: log, drop the message (broker redelivers), and keep going.
    var throwingProps = new ThrowingHeadersBasicProperties();
    var anyBody = "{}"u8.ToArray();
    var (goodProps, goodBody) = RabbitTestWire.ValidWireMessage("good");
    await RabbitTestWire.DeliverAsync(channel, throwingProps, anyBody, 1);
    await RabbitTestWire.DeliverAsync(channel, goodProps, goodBody, 2);

    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.AckAttempts[0]).IsEqualTo(2UL);
    await Assert.That(channel.NackAttempts).IsEmpty()
      .Because("Channel-closed during deserialization must NOT NACK — the broker redelivers unacked messages.");
    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0]).Count().IsEqualTo(1);
  }

  #endregion

  #region SubscribeBatchAsync — paused subscription

  [Test]
  public async Task SubscribeBatchAsync_PausedSubscription_NacksIncomingMessageWithRequeueAsync() {
    var options = new TransportBatchOptions { BatchSize = 5, SlideMs = 60_000, MaxWaitMs = 60_000 };
    var (channel, batches, subscription) = await _subscribeBatchAsync(options);

    await subscription.PauseAsync();

    var (props, body) = RabbitTestWire.ValidWireMessage("paused");
    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    // The paused-path NACK happens inline in the consumer's ReceivedAsync, which
    // HandleBasicDeliverAsync awaits — so it is complete when DeliverAsync returns.
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsTrue()
      .Because("Paused subscription must requeue (not dead-letter) so the message is redelivered once active.");
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(batches).IsEmpty();
  }

  [Test]
  public async Task SubscribeBatchAsync_PausedAfterEnqueue_FlushNacksAllPendingWithRequeueAsync() {
    // The message is enqueued while ACTIVE, then the subscription pauses BEFORE the slide
    // timer (250ms — generous vs. the sub-millisecond pause on this thread) triggers the
    // flush. The flush must take the paused path and NACK-requeue everything pending.
    // The wait below is a completion signal (NACK semaphore), not a delay.
    var options = new TransportBatchOptions { BatchSize = 50, SlideMs = 250, MaxWaitMs = 30_000 };
    var (channel, batches, subscription) = await _subscribeBatchAsync(options);

    var (props, body) = RabbitTestWire.ValidWireMessage("pending");
    await RabbitTestWire.DeliverAsync(channel, props, body, 1);
    await subscription.PauseAsync();

    await channel.WaitForNackAttemptsAsync(1);

    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsTrue();
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(batches).IsEmpty()
      .Because("A paused flush must not hand messages to the handler.");
  }

  [Test]
  public async Task SubscribeBatchAsync_PausedFlushNackThrowsAlreadyClosed_SwallowsAsync() {
    var options = new TransportBatchOptions { BatchSize = 50, SlideMs = 250, MaxWaitMs = 30_000 };
    var channel = new RecordingChannel { ExceptionToThrowOnNack = RabbitTestWire.NewAlreadyClosedException() };
    var (_, batches, subscription) = await _subscribeBatchAsync(options, channel: channel);

    var (props1, body1) = RabbitTestWire.ValidWireMessage("p1");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("p2");
    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);
    await subscription.PauseAsync();

    // Both paused NACKs must be attempted even though the first one throws
    // AlreadyClosedException — the paused loop swallows channel-closed errors per message.
    await channel.WaitForNackAttemptsAsync(2);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(batches).IsEmpty();
  }

  #endregion

  #region Helpers

  private static BulkPublishItem _newItem(
    MessageEnvelope<TestMessage> envelope,
    ReadOnlyMemory<byte>? preSerialized = null,
    string? routingKey = null,
    IReadOnlyDictionary<string, JsonElement>? perItemMetadata = null
  ) {
    return new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = null,
      MessageId = Guid.CreateVersion7(),
      RoutingKey = routingKey,
      PreSerializedBytes = preSerialized,
      PerItemMetadata = perItemMetadata
    };
  }

  /// <summary>
  /// Creates an initialized transport with a batch subscription on <paramref name="channel"/>
  /// (or a fresh <see cref="RecordingChannel"/>). Every flushed batch is recorded in the returned
  /// list BEFORE the optional <paramref name="handler"/> runs, so handler-throw tests still
  /// observe what the transport handed over.
  /// </summary>
  private static async Task<(RecordingChannel Channel, List<IReadOnlyList<TransportMessage>> Batches, ISubscription Subscription)> _subscribeBatchAsync(
    TransportBatchOptions options,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? handler = null,
    RecordingChannel? channel = null,
    ILogger<RabbitMQTransport>? logger = null
  ) {
    channel ??= new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    var batches = new List<IReadOnlyList<TransportMessage>>();
    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => {
        batches.Add(batch);
        return handler != null ? handler(batch, ct) : Task.CompletedTask;
      },
      RabbitTestWire.Destination(),
      options
    );

    return (channel, batches, subscription);
  }

  #endregion
}

#region Test doubles and wire helpers (shared with RabbitMQTransportFailurePathTests)

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

/// <summary>
/// Shared helpers for constructing transports, destinations, and wire-format messages
/// that round-trip through the transport's JsonContextRegistry-based deserialization.
/// </summary>
internal static class RabbitTestWire {
  /// <summary>Combined options: framework context (ServiceInstanceInfo, hops) + TestJsonContext (test envelopes).</summary>
  public static readonly JsonSerializerOptions JsonOptions = JsonContextRegistry.CreateCombinedOptions();

  public static MessageEnvelope<TestMessage> NewEnvelope(string content = "hello") {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  public static async Task<RabbitMQTransport> NewInitializedTransportAsync(
    FakeConnection connection,
    RabbitMQOptions? options = null,
    ILogger<RabbitMQTransport>? logger = null,
    IMessageDiscardPolicy? discardPolicy = null,
    Whizbang.Core.Routing.IPoisonMessageDetector? poisonDetector = null,
    TimeProvider? timeProvider = null
  ) {
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(
      connection, JsonOptions, pool, options ?? new RabbitMQOptions(), logger, discardPolicy,
      poisonDetector, timeProvider);
    await transport.InitializeAsync();
    return transport;
  }

  /// <summary>Destination with the SubscriberName metadata required for deterministic queue naming.</summary>
  public static TransportDestination Destination(
    string exchange = "test-exchange",
    string? routingKey = "#",
    IReadOnlyDictionary<string, JsonElement>? extraMetadata = null
  ) {
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-subscriber\"").RootElement.Clone()
    };
    if (extraMetadata != null) {
      foreach (var (key, value) in extraMetadata) {
        metadata[key] = value;
      }
    }
    return new TransportDestination(exchange, routingKey, metadata);
  }

  /// <summary>
  /// Builds properties + body exactly as the transport publishes them: JSON body serialized via
  /// the JsonTypeInfo registered in JsonContextRegistry and an EnvelopeType header (byte[], as
  /// RabbitMQ delivers string headers over AMQP).
  /// </summary>
  public static (BasicProperties Properties, byte[] Body) ValidWireMessage(string content = "hello") {
    var envelope = NewEnvelope(content);
    var aqn = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(aqn, JsonOptions)
      ?? throw new InvalidOperationException("MessageEnvelope<TestMessage> is not registered with JsonContextRegistry");
    var json = JsonSerializer.Serialize(envelope, typeInfo);
    var properties = new BasicProperties {
      MessageId = envelope.MessageId.Value.ToString(),
      Headers = new Dictionary<string, object?> {
        ["EnvelopeType"] = Encoding.UTF8.GetBytes(aqn)
      }
    };
    return (properties, Encoding.UTF8.GetBytes(json));
  }

  /// <summary>
  /// A message whose EnvelopeType header names a REGISTERED type that is not an
  /// IMessageEnvelope — deserialization succeeds but the type check must dead-letter it.
  /// </summary>
  public static (BasicProperties Properties, byte[] Body) NonEnvelopeWireMessage() {
    var aqn = typeof(TestMessage).AssemblyQualifiedName!;
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(aqn, JsonOptions)
      ?? throw new InvalidOperationException("TestMessage is not registered with JsonContextRegistry");
    var json = JsonSerializer.Serialize(new TestMessage("not-an-envelope"), typeInfo);
    var properties = new BasicProperties {
      MessageId = "non-envelope",
      Headers = new Dictionary<string, object?> {
        ["EnvelopeType"] = Encoding.UTF8.GetBytes(aqn)
      }
    };
    return (properties, Encoding.UTF8.GetBytes(json));
  }

  /// <summary>Delivers a message to the consumer registered on <paramref name="channel"/>.</summary>
  public static async Task DeliverAsync(
    RecordingChannel channel,
    IReadOnlyBasicProperties properties,
    byte[] body,
    ulong deliveryTag,
    bool redelivered = false
  ) {
    var consumer = (AsyncEventingBasicConsumer)channel.LastRegisteredConsumer!;
    await consumer.HandleBasicDeliverAsync(
      "test-consumer", deliveryTag, redelivered, "test-exchange", "#", properties, body);
  }

  public static AlreadyClosedException NewAlreadyClosedException() {
    return new AlreadyClosedException(new ShutdownEventArgs(
      ShutdownInitiator.Peer,
      replyCode: 406,
      replyText: "PRECONDITION_FAILED - unknown delivery tag"));
  }
}

/// <summary>
/// ILogger&lt;T&gt; test double that records every entry and optionally raises a callback per
/// entry — used as a deterministic completion signal for log-only code paths (no Task.Delay).
/// IsEnabled always returns true so Debug-guarded branches execute.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T> {
  private readonly Lock _sync = new();
  private readonly List<(LogLevel Level, string Message)> _entries = [];

  /// <summary>Invoked synchronously after each entry is recorded.</summary>
  public Action<LogLevel, string>? OnLog { get; set; }

  public IReadOnlyList<(LogLevel Level, string Message)> Entries {
    get {
      lock (_sync) {
        return [.. _entries];
      }
    }
  }

  public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    lock (_sync) {
      _entries.Add((logLevel, message));
    }
    OnLog?.Invoke(logLevel, message);
  }

  private sealed class NullScope : IDisposable {
    public static readonly NullScope Instance = new();
    public void Dispose() { }
  }
}

/// <summary>
/// IChannel test double that records every ACK/NACK ATTEMPT (recorded before any configured
/// exception is thrown) and releases a semaphore per attempt so tests can await completion
/// signals instead of sleeping. Also records publishes, exchange/queue declarations, and
/// bindings for behavioral assertions.
/// </summary>
internal sealed class RecordingChannel : IChannel {
  private readonly Lock _sync = new();
  private readonly SemaphoreSlim _ackSignal = new(0);
  private readonly SemaphoreSlim _nackSignal = new(0);

  public bool IsDisposed { get; private set; }

  public List<(string Exchange, string Type, bool Durable, bool AutoDelete)> DeclaredExchanges { get; } = [];
  public List<(string Queue, IDictionary<string, object?>? Arguments)> DeclaredQueues { get; } = [];
  public List<(string Queue, string Exchange, string RoutingKey)> QueueBindings { get; } = [];
  public List<(string Exchange, string RoutingKey, byte[] Body, IReadOnlyBasicProperties Properties)> Published { get; } = [];
  public List<ulong> AckAttempts { get; } = [];
  public List<(ulong DeliveryTag, bool Requeue)> NackAttempts { get; } = [];
  public IAsyncBasicConsumer? LastRegisteredConsumer { get; private set; }

  public Exception? ExceptionToThrowOnAck { get; set; }
  public Exception? ExceptionToThrowOnNack { get; set; }
  public Exception? ExceptionToThrowOnQueueBind { get; set; }
  public Exception? ExceptionToThrowOnExchangeDeclare { get; set; }

  /// <summary>Returns the exception to throw for a publish given the wire MessageId, or null to succeed.</summary>
  public Func<string?, Exception?>? PublishExceptionSelector { get; set; }

  /// <summary>Awaits <paramref name="count"/> ACK attempts (completion signal — not polling).</summary>
  public async Task WaitForAckAttemptsAsync(int count, TimeSpan? timeout = null) {
    var perSignalTimeout = timeout ?? TimeSpan.FromSeconds(30);
    for (var i = 0; i < count; i++) {
      if (!await _ackSignal.WaitAsync(perSignalTimeout)) {
        throw new TimeoutException($"Timed out waiting for ACK attempt {i + 1} of {count}");
      }
    }
  }

  /// <summary>Awaits <paramref name="count"/> NACK attempts (completion signal — not polling).</summary>
  public async Task WaitForNackAttemptsAsync(int count, TimeSpan? timeout = null) {
    var perSignalTimeout = timeout ?? TimeSpan.FromSeconds(30);
    for (var i = 0; i < count; i++) {
      if (!await _nackSignal.WaitAsync(perSignalTimeout)) {
        throw new TimeoutException($"Timed out waiting for NACK attempt {i + 1} of {count}");
      }
    }
  }

  // --- Members used by RabbitMQTransport / RabbitMQSubscription / RabbitMQChannelPool ---

  public bool IsOpen => !IsDisposed;
  public bool IsClosed => IsDisposed;
  public int ChannelNumber => 1;
  public ShutdownEventArgs? CloseReason => null;
  public IAsyncBasicConsumer? DefaultConsumer { get; set; }
  public ulong NextPublishSeqNo => 0;
  public string? CurrentQueue => null;
  public TimeSpan ContinuationTimeout { get; set; } = TimeSpan.FromSeconds(10);

  public event AsyncEventHandler<BasicAckEventArgs>? BasicAcksAsync;
  public event AsyncEventHandler<BasicNackEventArgs>? BasicNacksAsync;
  public event AsyncEventHandler<BasicReturnEventArgs>? BasicReturnAsync;
  public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
  public event AsyncEventHandler<FlowControlEventArgs>? FlowControlAsync;
  public event AsyncEventHandler<ShutdownEventArgs>? ChannelShutdownAsync;

  public void Dispose() => IsDisposed = true;

  public ValueTask DisposeAsync() {
    IsDisposed = true;
    return ValueTask.CompletedTask;
  }

  public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object?>? arguments, bool passive, bool noWait, CancellationToken cancellationToken = default) {
    if (ExceptionToThrowOnExchangeDeclare != null) {
      throw ExceptionToThrowOnExchangeDeclare;
    }
    lock (_sync) {
      DeclaredExchanges.Add((exchange, type, durable, autoDelete));
    }
    return Task.CompletedTask;
  }

  public ValueTask BasicPublishAsync<TProperties>(string exchange, string routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body = default, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader {
    var toThrow = PublishExceptionSelector?.Invoke(basicProperties.MessageId);
    if (toThrow != null) {
      throw toThrow;
    }
    lock (_sync) {
      Published.Add((exchange, routingKey, body.ToArray(), basicProperties));
    }
    return ValueTask.CompletedTask;
  }

  public ValueTask BasicPublishAsync<TProperties>(CachedString exchange, CachedString routingKey, bool mandatory, TProperties basicProperties, ReadOnlyMemory<byte> body = default, CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader =>
    throw new NotImplementedException();

  public Task<QueueDeclareOk> QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? arguments, bool passive, bool noWait, CancellationToken cancellationToken = default) {
    lock (_sync) {
      DeclaredQueues.Add((queue, arguments));
    }
    return Task.FromResult(new QueueDeclareOk(queue, 0, 0));
  }

  public Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) {
    if (ExceptionToThrowOnQueueBind != null) {
      throw ExceptionToThrowOnQueueBind;
    }
    lock (_sync) {
      QueueBindings.Add((queue, exchange, routingKey));
    }
    return Task.CompletedTask;
  }

  public Task<string> BasicConsumeAsync(string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object?>? arguments, IAsyncBasicConsumer consumer, CancellationToken cancellationToken = default) {
    LastRegisteredConsumer = consumer;
    return Task.FromResult(consumerTag);
  }

  public Task BasicCancelAsync(string consumerTag, bool noWait, CancellationToken cancellationToken = default) => Task.CompletedTask;

  public ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) {
    lock (_sync) {
      AckAttempts.Add(deliveryTag);
    }
    _ackSignal.Release();
    if (ExceptionToThrowOnAck != null) {
      throw ExceptionToThrowOnAck;
    }
    return ValueTask.CompletedTask;
  }

  public ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) {
    lock (_sync) {
      NackAttempts.Add((deliveryTag, requeue));
    }
    _nackSignal.Release();
    if (ExceptionToThrowOnNack != null) {
      throw ExceptionToThrowOnNack;
    }
    return ValueTask.CompletedTask;
  }

  public Task BasicQosAsync(uint prefetchSize, ushort prefetchCount, bool global, CancellationToken cancellationToken = default) => Task.CompletedTask;

  // --- Members not used by these tests ---

  public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task AbortAsync(ushort replyCode, string replyText, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public ValueTask BasicRejectAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task CloseAsync(ushort replyCode, string replyText, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task CloseAsync(ShutdownEventArgs reason, bool abort, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task CloseAsync(ShutdownEventArgs reason, bool abort) => Task.CompletedTask;
  public ValueTask ConfirmSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> ConsumerCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeBindAsync(string destination, string source, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeDeclarePassiveAsync(string exchange, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeDeleteAsync(string exchange, bool ifUnused, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task ExchangeUnbindAsync(string destination, string source, string routingKey, IDictionary<string, object?>? arguments, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> MessageCountAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<QueueDeclareOk> QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<uint> QueuePurgeAsync(string queue, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task QueueUnbindAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? arguments, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task TxCommitAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task TxRollbackAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task TxSelectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task<bool> WaitForConfirmsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
  public Task WaitForConfirmsOrDieAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

/// <summary>
/// IReadOnlyBasicProperties whose Headers getter throws ObjectDisposedException — simulates the
/// channel being torn down while the batch flush reads message properties during deserialization.
/// </summary>
internal sealed class ThrowingHeadersBasicProperties : IReadOnlyBasicProperties {
  public string? AppId => null;
  public string? ClusterId => null;
  public string? ContentEncoding => null;
  public string? ContentType => null;
  public string? CorrelationId => null;
  public DeliveryModes DeliveryMode => DeliveryModes.Persistent;
  public string? Expiration => null;
  public IDictionary<string, object?>? Headers => throw new ObjectDisposedException(nameof(IChannel));
  public string? MessageId => "throwing-headers-message";
  public bool Persistent => true;
  public byte Priority => 0;
  public string? ReplyTo => null;
  public PublicationAddress? ReplyToAddress => null;
  public AmqpTimestamp Timestamp => default;
  public string? Type => null;
  public string? UserId => null;

  public bool IsAppIdPresent() => false;
  public bool IsClusterIdPresent() => false;
  public bool IsContentEncodingPresent() => false;
  public bool IsContentTypePresent() => false;
  public bool IsCorrelationIdPresent() => false;
  public bool IsDeliveryModePresent() => false;
  public bool IsExpirationPresent() => false;
  public bool IsHeadersPresent() => true;
  public bool IsMessageIdPresent() => true;
  public bool IsPriorityPresent() => false;
  public bool IsReplyToPresent() => false;
  public bool IsTimestampPresent() => false;
  public bool IsTypePresent() => false;
  public bool IsUserIdPresent() => false;
}

#pragma warning restore CS0067, CA1822

#endregion
