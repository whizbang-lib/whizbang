using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Unit tests for RabbitMQTransport failure handling and infrastructure paths:
/// <list type="bullet">
///   <item>Initialize/publish guard clauses and publish error wrapping (AlreadyClosed vs generic)</item>
///   <item>Single-message consume: deserialization-failure NACKs, discard-policy ack-and-skip,
///     handler failure retry/dead-letter decisions (_handleMessageFailureAsync), channel-closed swallowing</item>
///   <item>Dead-letter exchange declaration, single-active-consumer queue arguments, routing-pattern binding</item>
///   <item>Connection-recovery success path (exchange cache reset) and JsonElement→header conversion</item>
/// </list>
/// Single-message processing is fully awaited by the fake consumer's HandleBasicDeliverAsync, so
/// no timers or delays are involved — every assertion runs after the transport finished the message.
/// </summary>
public class RabbitMQTransportFailurePathTests {

  #region Initialize / publish guards

  [Test]
  public async Task InitializeAsync_CalledTwice_IsIdempotentAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = new RabbitMQTransport(connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger);

    await transport.InitializeAsync();
    await Assert.That(async () => await transport.InitializeAsync()).ThrowsNothing();

    await Assert.That(transport.IsInitialized).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("already initialized", StringComparison.Ordinal))).IsTrue()
      .Because("The second call must take the fast-path skip, not re-initialize.");
  }

  [Test]
  public async Task InitializeAsync_ConnectionNotOpen_ThrowsInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel), isOpen: false);
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger: null);

    InvalidOperationException? caught = null;
    try {
      await transport.InitializeAsync();
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("not open");
    await Assert.That(transport.IsInitialized).IsFalse();
  }

  [Test]
  public async Task PublishAsync_NotInitialized_ThrowsInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger: null);

    InvalidOperationException? caught = null;
    try {
      await transport.PublishAsync(RabbitTestWire.NewEnvelope(), new TransportDestination("test-exchange"));
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("not initialized");
    await Assert.That(channel.Published).IsEmpty();
  }

  #endregion

  #region Publish error wrapping and logging

  [Test]
  public async Task PublishAsync_WithDebugLogger_LogsPublishingAndSuccessAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    await transport.PublishAsync(RabbitTestWire.NewEnvelope(), new TransportDestination("test-exchange", "orders.created"));

    await Assert.That(channel.Published).Count().IsEqualTo(1);
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Publishing message", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Successfully published", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishAsync_ChannelThrowsAlreadyClosed_WrapsWithOutboxRetryMessageAsync() {
    var channel = new RecordingChannel {
      PublishExceptionSelector = _ => RabbitTestWire.NewAlreadyClosedException()
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    InvalidOperationException? caught = null;
    try {
      await transport.PublishAsync(RabbitTestWire.NewEnvelope(), new TransportDestination("test-exchange"));
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("will be retried")
      .Because("A closed connection during publish is recoverable — the outbox row persists and retries.");
    await Assert.That(caught.InnerException).IsTypeOf<AlreadyClosedException>();
  }

  [Test]
  public async Task PublishAsync_ChannelThrowsGeneric_WrapsInInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel {
      PublishExceptionSelector = _ => new NotSupportedException("wire failure")
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    InvalidOperationException? caught = null;
    try {
      await transport.PublishAsync(RabbitTestWire.NewEnvelope(), new TransportDestination("test-exchange"));
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to publish message");
    await Assert.That(caught.InnerException).IsTypeOf<NotSupportedException>();
  }

  [Test]
  public async Task PublishAsync_MetadataValues_ConvertedToRabbitMqHeaderTypesAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    var metadata = new Dictionary<string, JsonElement> {
      ["str"] = JsonDocument.Parse("\"text\"").RootElement.Clone(),
      ["int"] = JsonDocument.Parse("42").RootElement.Clone(),
      ["long"] = JsonDocument.Parse("9999999999").RootElement.Clone(),
      ["dbl"] = JsonDocument.Parse("1.5").RootElement.Clone(),
      ["yes"] = JsonDocument.Parse("true").RootElement.Clone(),
      ["no"] = JsonDocument.Parse("false").RootElement.Clone(),
      ["nil"] = JsonDocument.Parse("null").RootElement.Clone(),
      ["arr"] = JsonDocument.Parse("[1,\"two\",true]").RootElement.Clone(),
      ["obj"] = JsonDocument.Parse("{\"k\":\"v\",\"n\":3}").RootElement.Clone()
    };

    await transport.PublishAsync(
      RabbitTestWire.NewEnvelope(), new TransportDestination("meta-exchange", null, metadata));

    var headers = channel.Published[0].Properties.Headers!;
    await Assert.That((string)headers["str"]!).IsEqualTo("text");
    await Assert.That((int)headers["int"]!).IsEqualTo(42);
    await Assert.That((long)headers["long"]!).IsEqualTo(9999999999L);
    await Assert.That((double)headers["dbl"]!).IsEqualTo(1.5);
    await Assert.That((bool)headers["yes"]!).IsTrue();
    await Assert.That((bool)headers["no"]!).IsFalse();
    await Assert.That(headers["nil"]).IsNull();

    var arr = (List<object?>)headers["arr"]!;
    await Assert.That(arr).Count().IsEqualTo(3);
    await Assert.That((int)arr[0]!).IsEqualTo(1);
    await Assert.That((string)arr[1]!).IsEqualTo("two");
    await Assert.That((bool)arr[2]!).IsTrue();

    var obj = (Dictionary<string, object?>)headers["obj"]!;
    await Assert.That((string)obj["k"]!).IsEqualTo("v");
    await Assert.That((int)obj["n"]!).IsEqualTo(3);
  }

  #endregion

  #region SubscribeAsync guards, error wrapping, and logging

  [Test]
  public async Task SubscribeAsync_NotInitialized_ThrowsInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger: null);

    InvalidOperationException? caught = null;
    try {
      await transport.SubscribeAsync(
        (envelope, envelopeType, ct) => Task.CompletedTask, RabbitTestWire.Destination());
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("not initialized");
  }

  [Test]
  public async Task SubscribeAsync_QueueBindThrowsGeneric_WrapsInInvalidOperationExceptionAsync() {
    var channel = new RecordingChannel {
      ExceptionToThrowOnQueueBind = new NotSupportedException("bind exploded")
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    InvalidOperationException? caught = null;
    try {
      await transport.SubscribeAsync(
        (envelope, envelopeType, ct) => Task.CompletedTask, RabbitTestWire.Destination());
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Failed to create RabbitMQ subscription");
    await Assert.That(caught.InnerException).IsTypeOf<NotSupportedException>();
  }

  [Test]
  public async Task SubscribeAsync_WithDebugLogger_LogsCreationBindingAndConsumerAsync() {
    var logger = new CapturingLogger<RabbitMQTransport>();
    var (_, _, subscription, _) = await _subscribeAsync(logger: logger);

    await Assert.That(subscription).IsNotNull();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Creating subscription", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Binding queue", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Created subscription for exchange", StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region Single-message consume — success, deserialization failures, discard policy

  [Test]
  public async Task ProcessMessage_ValidMessage_InvokesHandlerAcksAndLogsDiagnosticsAsync() {
    var logger = new CapturingLogger<RabbitMQTransport>();
    var (channel, handled, _, _) = await _subscribeAsync(logger: logger);

    var (props, body) = RabbitTestWire.ValidWireMessage("payload-content");
    await RabbitTestWire.DeliverAsync(channel, props, body, 7);

    await Assert.That(handled).Count().IsEqualTo(1);
    var envelope = (MessageEnvelope<TestMessage>)handled[0].Envelope;
    await Assert.That(envelope.Payload.Content).IsEqualTo("payload-content");
    await Assert.That(handled[0].EnvelopeType)
      .IsEqualTo(typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName);
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.AckAttempts[0]).IsEqualTo(7UL);
    await Assert.That(channel.NackAttempts).IsEmpty();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Deserializing envelope", StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("Processed message", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ProcessMessage_MissingEnvelopeTypeHeader_NacksToDeadLetterAsync() {
    var (channel, handled, _, _) = await _subscribeAsync();

    var props = new BasicProperties { MessageId = "missing-header" };
    await RabbitTestWire.DeliverAsync(channel, props, "{}"u8.ToArray(), 1);

    await Assert.That(handled).IsEmpty();
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("Deserialization failures must dead-letter, never requeue a poison message.");
  }

  [Test]
  public async Task ProcessMessage_UnknownEnvelopeType_NacksToDeadLetterAsync() {
    var (channel, handled, _, _) = await _subscribeAsync();

    var props = new BasicProperties {
      MessageId = "unknown-type",
      Headers = new Dictionary<string, object?> {
        ["EnvelopeType"] = Encoding.UTF8.GetBytes("Whizbang.Tests.DoesNotExist.UnknownEnvelope, Whizbang.DoesNotExist")
      }
    };
    await RabbitTestWire.DeliverAsync(channel, props, "{}"u8.ToArray(), 2);

    await Assert.That(handled).IsEmpty();
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(2UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse();
  }

  [Test]
  public async Task ProcessMessage_BodyDeserializesToNonEnvelope_NacksToDeadLetterAsync() {
    var (channel, handled, _, _) = await _subscribeAsync();

    var (props, body) = RabbitTestWire.NonEnvelopeWireMessage();
    await RabbitTestWire.DeliverAsync(channel, props, body, 3);

    await Assert.That(handled).IsEmpty();
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse();
  }

  [Test]
  public async Task ProcessMessage_DiscardPolicySaysSkip_AcksWithoutInvokingHandlerAsync() {
    using var meter = new Meter("Whizbang.Tests.RabbitMQTransportFailurePathTests.Discard");
    var policy = new MessageDiscardPolicy(
      new EmptyReceptorRegistry(), new CapturingLogger<MessageDiscardPolicy>(), meter);
    var (channel, handled, _, _) = await _subscribeAsync(discardPolicy: policy);

    var (props, body) = RabbitTestWire.ValidWireMessage("skipped");
    await RabbitTestWire.DeliverAsync(channel, props, body, 5);

    await Assert.That(handled).IsEmpty()
      .Because("With no local consumer for the payload type, the message must be acked-and-skipped without touching the handler.");
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.AckAttempts[0]).IsEqualTo(5UL);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task ProcessMessage_IsClaimHeaderVariants_AllDecodedAndProcessedAsync() {
    // _tryReadStringHeader must handle byte[] (AMQP wire form), string (pre-decoded),
    // arbitrary objects (ToString fallback), and explicit null — none of which mark a claim.
    var (channel, handled, _, _) = await _subscribeAsync();

    var (props1, body1) = RabbitTestWire.ValidWireMessage("c1");
    props1.Headers![BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY] = Encoding.UTF8.GetBytes("false");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("c2");
    props2.Headers![BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY] = "false";
    var (props3, body3) = RabbitTestWire.ValidWireMessage("c3");
    props3.Headers![BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY] = false;
    var (props4, body4) = RabbitTestWire.ValidWireMessage("c4");
    props4.Headers![BodyOffloadPostSerializeHook.IS_CLAIM_METADATA_KEY] = null;

    await RabbitTestWire.DeliverAsync(channel, props1, body1, 1);
    await RabbitTestWire.DeliverAsync(channel, props2, body2, 2);
    await RabbitTestWire.DeliverAsync(channel, props3, body3, 3);
    await RabbitTestWire.DeliverAsync(channel, props4, body4, 4);

    await Assert.That(handled).Count().IsEqualTo(4);
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(4);
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task ProcessMessage_PausedSubscription_NacksWithRequeueAsync() {
    var (channel, handled, subscription, _) = await _subscribeAsync();

    await subscription.PauseAsync();

    var (props, body) = RabbitTestWire.ValidWireMessage("paused");
    await RabbitTestWire.DeliverAsync(channel, props, body, 9);

    await Assert.That(handled).IsEmpty();
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(9UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsTrue()
      .Because("Paused subscriptions requeue so the broker redelivers once the subscription resumes.");
  }

  [Test]
  public async Task ProcessMessage_AckThrowsAlreadyClosed_SwallowedByReceiveHandlerAsync() {
    var channel = new RecordingChannel { ExceptionToThrowOnAck = RabbitTestWire.NewAlreadyClosedException() };
    var (_, handled, _, _) = await _subscribeAsync(channel: channel);

    var (props, body) = RabbitTestWire.ValidWireMessage("ack-fails");
    Exception? caught = null;
    try {
      await RabbitTestWire.DeliverAsync(channel, props, body, 1);
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull()
      .Because("AlreadyClosedException from the ACK must not escape the consumer's ReceivedAsync handler.");
    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(channel.AckAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts).IsEmpty()
      .Because("Channel-closed after a successful handler run must NOT NACK — the broker redelivers unacked messages.");
  }

  #endregion

  #region Handler failure → _handleMessageFailureAsync

  [Test]
  public async Task HandleMessageFailure_FirstAttempt_NacksWithRequeueAsync() {
    var (channel, handled, _, _) = await _subscribeAsync(
      handlerBehavior: () => throw new InvalidOperationException("handler boom"));

    var (props, body) = RabbitTestWire.ValidWireMessage("retry-me");
    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].Requeue).IsTrue()
      .Because("Below MaxDeliveryAttempts the message must be requeued for retry, not dead-lettered.");
  }

  [Test]
  public async Task HandleMessageFailure_DeliveryCountHeaderAtMax_NacksToDeadLetterAsync() {
    var (channel, _, _, _) = await _subscribeAsync(
      handlerBehavior: () => throw new InvalidOperationException("handler boom"));

    var (props, body) = RabbitTestWire.ValidWireMessage("exhausted");
    props.Headers!["x-delivery-count"] = 10; // == default MaxDeliveryAttempts
    await RabbitTestWire.DeliverAsync(channel, props, body, 2);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].DeliveryTag).IsEqualTo(2UL);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("At MaxDeliveryAttempts the message must go to the dead-letter queue.");
  }

  [Test]
  public async Task HandleMessageFailure_RedeliveredWithMaxTwoAttempts_NacksToDeadLetterAsync() {
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 2 };
    var (channel, _, _, _) = await _subscribeAsync(
      options: options,
      handlerBehavior: () => throw new InvalidOperationException("handler boom"));

    var (props, body) = RabbitTestWire.ValidWireMessage("redelivered");
    await RabbitTestWire.DeliverAsync(channel, props, body, 3, redelivered: true);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("Without an x-delivery-count header, redelivered=true counts as attempt 2, hitting MaxDeliveryAttempts=2.");
  }

  [Test]
  public async Task HandleMessageFailure_NackThrowsAlreadyClosed_SwallowsAsync() {
    var channel = new RecordingChannel { ExceptionToThrowOnNack = RabbitTestWire.NewAlreadyClosedException() };
    var (_, handled, _, _) = await _subscribeAsync(
      channel: channel,
      handlerBehavior: () => throw new InvalidOperationException("handler boom"));

    var (props, body) = RabbitTestWire.ValidWireMessage("nack-fails");
    Exception? caught = null;
    try {
      await RabbitTestWire.DeliverAsync(channel, props, body, 4);
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull()
      .Because("A channel closed during failure handling is a shutdown race — swallow and let the broker redeliver.");
    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
  }

  #endregion

  #region Infrastructure — dead-letter exchange, SAC, routing patterns

  [Test]
  public async Task SubscribeAsync_DeadLetterEnabled_DeclaresDlxDlqAndBindingAsync() {
    var (channel, _, _, _) = await _subscribeAsync(); // AutoDeclareDeadLetterExchange defaults to true

    await Assert.That(channel.DeclaredExchanges.Contains(("test-exchange.dlx", "fanout", true, false))).IsTrue()
      .Because("The dead-letter exchange must be a durable fanout named '<exchange>.dlx'.");
    await Assert.That(channel.DeclaredQueues.Any(q => q.Queue == "test-subscriber-test-exchange.dlq")).IsTrue();
    await Assert.That(channel.QueueBindings.Contains(
      ("test-subscriber-test-exchange.dlq", "test-exchange.dlx", ""))).IsTrue();

    var mainQueue = channel.DeclaredQueues.Single(q => q.Queue == "test-subscriber-test-exchange");
    await Assert.That((string)mainQueue.Arguments!["x-dead-letter-exchange"]!).IsEqualTo("test-exchange.dlx");
  }

  [Test]
  public async Task SubscribeAsync_DeadLetterDisabled_DoesNotDeclareDlxAsync() {
    var options = new RabbitMQOptions { AutoDeclareDeadLetterExchange = false };
    var (channel, _, _, _) = await _subscribeAsync(options: options);

    await Assert.That(channel.DeclaredExchanges.Any(e =>
      e.Exchange.EndsWith(".dlx", StringComparison.Ordinal))).IsFalse();
    var mainQueue = channel.DeclaredQueues.Single(q => q.Queue == "test-subscriber-test-exchange");
    await Assert.That(mainQueue.Arguments!.ContainsKey("x-dead-letter-exchange")).IsFalse();
  }

  [Test]
  public async Task SubscribeAsync_SingleActiveConsumer_SetsQueueArgumentAndOrderedCapabilityAsync() {
    var options = new RabbitMQOptions { EnableSingleActiveConsumer = true };
    var (channel, _, _, transport) = await _subscribeAsync(options: options);

    var mainQueue = channel.DeclaredQueues.Single(q => q.Queue == "test-subscriber-test-exchange");
    await Assert.That((bool)mainQueue.Arguments!["x-single-active-consumer"]!).IsTrue();
    await Assert.That((transport.Capabilities & TransportCapabilities.Ordered) != 0).IsTrue()
      .Because("With SAC enabled RabbitMQ guarantees ordering, so the transport must advertise it.");
  }

  [Test]
  public async Task SubscribeAsync_RoutingPatternsArrayMetadata_BindsEachPatternAsync() {
    var extraMetadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = JsonDocument.Parse("[\"orders.*\",\"payments.#\"]").RootElement.Clone()
    };
    var (channel, _, _, _) = await _subscribeAsync(
      destination: RabbitTestWire.Destination(extraMetadata: extraMetadata));

    var routingKeys = channel.QueueBindings
      .Where(b => b.Queue == "test-subscriber-test-exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    await Assert.That(routingKeys).Count().IsEqualTo(2);
    await Assert.That(routingKeys).Contains("orders.*");
    await Assert.That(routingKeys).Contains("payments.#");
  }

  [Test]
  public async Task SubscribeAsync_SingularRoutingPatternMetadata_BindsThatPatternAsync() {
    var extraMetadata = new Dictionary<string, JsonElement> {
      ["RoutingPattern"] = JsonDocument.Parse("\"orders.created\"").RootElement.Clone()
    };
    var (channel, _, _, _) = await _subscribeAsync(
      destination: RabbitTestWire.Destination(extraMetadata: extraMetadata));

    var routingKeys = channel.QueueBindings
      .Where(b => b.Queue == "test-subscriber-test-exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    await Assert.That(routingKeys).Count().IsEqualTo(1);
    await Assert.That(routingKeys[0]).IsEqualTo("orders.created");
  }

  [Test]
  public async Task SubscribeAsync_CommaSeparatedRoutingKey_BindsEachSegmentAsync() {
    var (channel, _, _, _) = await _subscribeAsync(
      destination: RabbitTestWire.Destination(routingKey: "orders.*,payments.#"));

    var routingKeys = channel.QueueBindings
      .Where(b => b.Queue == "test-subscriber-test-exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    await Assert.That(routingKeys).Count().IsEqualTo(2);
    await Assert.That(routingKeys).Contains("orders.*");
    await Assert.That(routingKeys).Contains("payments.#");
  }

  [Test]
  public async Task SubscribeAsync_EmptyRoutingPatternsArray_FallsBackToMatchAllAsync() {
    var extraMetadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = JsonDocument.Parse("[]").RootElement.Clone()
    };
    var (channel, _, _, _) = await _subscribeAsync(
      destination: RabbitTestWire.Destination(routingKey: "#", extraMetadata: extraMetadata));

    var routingKeys = channel.QueueBindings
      .Where(b => b.Queue == "test-subscriber-test-exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    await Assert.That(routingKeys).Count().IsEqualTo(1);
    await Assert.That(routingKeys[0]).IsEqualTo("#");
  }

  #endregion

  #region Connection recovery and SendAsync

  [Test]
  public async Task Publish_AfterConnectionRecovery_ResetsExchangeCacheAndRedeclaresAsync() {
    var channels = new List<RecordingChannel>();
    var connection = new FakeConnection(() => {
      var created = new RecordingChannel();
      channels.Add(created);
      return Task.FromResult<IChannel>(created);
    });
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var destination = new TransportDestination("cached-exchange");

    await transport.PublishAsync(RabbitTestWire.NewEnvelope("one"), destination);
    await transport.PublishAsync(RabbitTestWire.NewEnvelope("two"), destination);

    var declaresBeforeRecovery = channels.Sum(c => c.DeclaredExchanges.Count);
    await Assert.That(declaresBeforeRecovery).IsEqualTo(1)
      .Because("The exchange declaration is cached — only the first publish hits the broker.");

    // Recovery success path: channel pool reset + declared-exchange cache cleared.
    await connection.SimulateRecoverySucceededAsync();

    await transport.PublishAsync(RabbitTestWire.NewEnvelope("three"), destination);

    var declaresAfterRecovery = channels.Sum(c => c.DeclaredExchanges.Count);
    await Assert.That(declaresAfterRecovery).IsEqualTo(2)
      .Because("After recovery the cache must be cleared so the exchange is re-declared on the new connection.");
    await Assert.That(channels).Count().IsEqualTo(2)
      .Because("The pool must discard the stale channel and create a fresh one after recovery.");
    await Assert.That(channels[1].Published).Count().IsEqualTo(1);
  }

  [Test]
  public async Task SendAsync_ThrowsNotSupportedExceptionAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);

    NotSupportedException? caught = null;
    try {
      await transport.SendAsync<TestMessage, TestMessage>(
        RabbitTestWire.NewEnvelope(), new TransportDestination("rpc-exchange"));
    } catch (NotSupportedException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("Request/response pattern is not supported");
  }

  #endregion

  #region Helpers

  /// <summary>
  /// Creates an initialized transport with a single-message subscription on the given (or a
  /// fresh) <see cref="RecordingChannel"/>. Every handled envelope is recorded BEFORE the
  /// optional <paramref name="handlerBehavior"/> runs, so handler-throw tests still observe
  /// what the transport delivered. Single-path processing is fully awaited by the consumer's
  /// HandleBasicDeliverAsync, so callers can assert immediately after delivery.
  /// </summary>
  private static async Task<(RecordingChannel Channel, List<(IMessageEnvelope Envelope, string? EnvelopeType)> Handled, ISubscription Subscription, RabbitMQTransport Transport)> _subscribeAsync(
    RabbitMQOptions? options = null,
    ILogger<RabbitMQTransport>? logger = null,
    IMessageDiscardPolicy? discardPolicy = null,
    Func<Task>? handlerBehavior = null,
    RecordingChannel? channel = null,
    TransportDestination? destination = null
  ) {
    channel ??= new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, options, logger, discardPolicy);

    var handled = new List<(IMessageEnvelope Envelope, string? EnvelopeType)>();
    var subscription = await transport.SubscribeAsync(
      (envelope, envelopeType, ct) => {
        handled.Add((envelope, envelopeType));
        return handlerBehavior != null ? handlerBehavior() : Task.CompletedTask;
      },
      destination ?? RabbitTestWire.Destination()
    );

    return (channel, handled, subscription, transport);
  }

  #endregion
}

/// <summary>
/// IReceptorRegistryQuery with no consumers at all — every EvaluateReceive decision through
/// <see cref="MessageDiscardPolicy"/> becomes a discard.
/// </summary>
internal sealed class EmptyReceptorRegistry : IReceptorRegistryQuery {
  public bool HasReceptors(LifecycleStage stage, string messageType) => false;
  public bool HasInboxHandler(string messageType) => false;
  public bool HasAnyConsumer(string messageType) => false;
}
