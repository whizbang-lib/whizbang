// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests.BranchCoverage;

/// <summary>
/// Branch coverage for <see cref="RabbitMQTransport"/>'s diagnostic arms on the single-message
/// path, connection recovery, publish and subscribe failures. The existing suites drive these
/// paths with no logger, so the arm that WRITES the operator-facing diagnostic (and the
/// "unknown" fallback it uses when the broker message carries no MessageId) was never taken.
/// Each test asserts the diagnostic an operator would search for, so a regression that drops
/// the log, or reports a null id instead of the documented fallback, fails here.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQTransport.cs</code-under-test>
public class RabbitMQTransportLoggingBranchTests {

  #region Connection recovery

  [Test]
  public async Task Recovery_WithLoggerAndNoHandler_LogsRecoveryWithoutInvokingAnythingAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new RecordingChannel()));
    var logger = new CapturingLogger<RabbitMQTransport>();
    await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    await connection.SimulateRecoverySucceededAsync();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Information && e.Message.Contains("connection recovered", StringComparison.Ordinal))).IsTrue()
      .Because("an operator correlating an outage needs the recovery moment in the log");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsFalse()
      .Because("with no recovery handler registered there is nothing to fail");
  }

  [Test]
  public async Task Recovery_WithLoggerAndThrowingHandler_LogsTheHandlerErrorAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new RecordingChannel()));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
    transport.SetRecoveryHandler(_ => throw new InvalidOperationException("resubscribe failed"));

    await Assert.That(async () => await connection.SimulateRecoverySucceededAsync()).ThrowsNothing();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Error in recovery handler", StringComparison.Ordinal))).IsTrue()
      .Because("a recovery handler failure is swallowed, so the log is the only trace of it");
  }

  [Test]
  public async Task InitializeAsync_CalledTwiceWithoutLogger_StaysInitializedAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new RecordingChannel()));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: null);

    // The already-initialized fast path must not dereference the absent logger.
    await Assert.That(async () => await transport.InitializeAsync()).ThrowsNothing();
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  #endregion

  #region Publish failures

  [Test]
  public async Task PublishAsync_AlreadyClosedWithLogger_LogsOutboxRetryWarningAsync() {
    var channel = new RecordingChannel {
      PublishExceptionSelector = _ => RabbitTestWire.NewAlreadyClosedException()
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    await Assert.That(async () => await transport.PublishAsync(
      RabbitTestWire.NewEnvelope(), new TransportDestination("test-exchange"))).Throws<InvalidOperationException>();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning && e.Message.Contains("will be retried from outbox", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task PublishAsync_GenericFailureWithLogger_LogsPublishErrorAsync() {
    var channel = new RecordingChannel {
      PublishExceptionSelector = _ => new NotSupportedException("wire failure")
    };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    await Assert.That(async () => await transport.PublishAsync(
      RabbitTestWire.NewEnvelope(), new TransportDestination("test-exchange", "orders.created"))).Throws<InvalidOperationException>();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error
      && e.Message.Contains("Failed to publish message", StringComparison.Ordinal)
      && e.Message.Contains("orders.created", StringComparison.Ordinal))).IsTrue()
      .Because("the error must name the routing key so an operator can find the failed route");
  }

  #endregion

  #region Subscribe failures

  [Test]
  public async Task SubscribeAsync_CanceledWithoutCallerCancellationWithLogger_LogsTimeoutAsync() {
    var channel = new RecordingChannel { ExceptionToThrowOnQueueBind = new OperationCanceledException("broker slow") };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    await Assert.That(async () => await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, RabbitTestWire.Destination())).Throws<InvalidOperationException>();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("timed out", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task SubscribeAsync_GenericFailureWithLogger_LogsSubscriptionErrorAsync() {
    var channel = new RecordingChannel { ExceptionToThrowOnQueueBind = new NotSupportedException("bind exploded") };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);

    await Assert.That(async () => await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, RabbitTestWire.Destination())).Throws<InvalidOperationException>();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Failed to create subscription", StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region Single-message receive: channel closed, deserialization failures

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("msg-42", "msg-42")]
  public async Task Receive_AckThrowsAlreadyClosedWithLogger_LogsRedeliveryWarningAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel { ExceptionToThrowOnAck = RabbitTestWire.NewAlreadyClosedException() };
    var logger = new CapturingLogger<RabbitMQTransport>();
    await _subscribeAsync(channel, logger);
    var (props, body) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;

    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("closed/disposed while processing message " + expectedId, StringComparison.Ordinal))).IsTrue();
    await Assert.That(channel.NackAttempts).IsEmpty();
  }

  [Test]
  public async Task Receive_MissingEnvelopeTypeHeaderWithLogger_LogsAndDeadLettersAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var handled = await _subscribeAsync(channel, logger);

    await RabbitTestWire.DeliverAsync(channel, new BasicProperties { MessageId = "no-header" }, "{}"u8.ToArray(), 1);

    await Assert.That(handled).IsEmpty();
    await Assert.That(channel.NackAttempts.Single().Requeue).IsFalse();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("no-header missing EnvelopeType header", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Receive_UnknownEnvelopeTypeWithLogger_LogsMissingTypeInfoAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var handled = await _subscribeAsync(channel, logger);
    var props = new BasicProperties {
      MessageId = "unknown-type",
      Headers = new Dictionary<string, object?> {
        ["EnvelopeType"] = Encoding.UTF8.GetBytes("Whizbang.Tests.DoesNotExist.Envelope, Whizbang.DoesNotExist")
      }
    };

    await RabbitTestWire.DeliverAsync(channel, props, "{}"u8.ToArray(), 1);

    await Assert.That(handled).IsEmpty();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("No JsonTypeInfo found", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Receive_BodyIsNotAnEnvelopeWithLogger_LogsDeserializeFailureAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var handled = await _subscribeAsync(channel, logger);
    var (props, body) = RabbitTestWire.NonEnvelopeWireMessage();

    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    await Assert.That(handled).IsEmpty();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Failed to deserialize message non-envelope", StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region Single-message receive: handler failure handling

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("retry-1", "retry-1")]
  public async Task Receive_HandlerThrowsBelowMaxWithLogger_LogsErrorAndRetryWarningAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    await _subscribeAsync(channel, logger, () => throw new InvalidOperationException("handler boom"));
    var (props, body) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;

    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    await Assert.That(channel.NackAttempts.Single().Requeue).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Error processing message " + expectedId, StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("requeueing for retry", StringComparison.Ordinal)
      && e.Message.Contains(expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("dead-1", "dead-1")]
  public async Task Receive_HandlerThrowsAtMaxWithLogger_LogsDeadLetterWarningAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    await _subscribeAsync(channel, logger, () => throw new InvalidOperationException("handler boom"));
    var (props, body) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;
    props.Headers!["x-delivery-count"] = 10;

    await RabbitTestWire.DeliverAsync(channel, props, body, 1);

    await Assert.That(channel.NackAttempts.Single().Requeue).IsFalse();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("after max delivery attempts", StringComparison.Ordinal)
      && e.Message.Contains(expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("closed-1", "closed-1")]
  public async Task Receive_FailureNackThrowsWithLogger_LogsRedeliveryOnReconnectAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel { ExceptionToThrowOnNack = new ObjectDisposedException(nameof(IChannel)) };
    var logger = new CapturingLogger<RabbitMQTransport>();
    await _subscribeAsync(channel, logger, () => throw new InvalidOperationException("handler boom"));
    var (props, body) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;

    await Assert.That(async () => await RabbitTestWire.DeliverAsync(channel, props, body, 1)).ThrowsNothing();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("during failure handling for message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Receive_PoisonDetectorThrowsOnHeaderlessMessage_RequeuesFromTheRedeliveredFlagAsync() {
    // The failure handler reads an optional x-delivery-count header. A message that fails BEFORE
    // deserialization (here: the poison gate itself faults) may carry no headers at all; the
    // handler must fall back to the redelivered flag instead of faulting a second time.
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(
      connection, poisonDetector: new ThrowingPoisonDetector());
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, RabbitTestWire.Destination());

    await Assert.That(async () => await RabbitTestWire.DeliverAsync(
      channel, new BasicProperties { MessageId = "headerless" }, "{}"u8.ToArray(), 3)).ThrowsNothing();

    await Assert.That(channel.NackAttempts.Single()).IsEqualTo((3UL, true))
      .Because("not redelivered = attempt 1, below MaxDeliveryAttempts, so the message is requeued");
  }

  #endregion

  #region Direct NACK helpers

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("paused-1", "paused-1")]
  public async Task NackPausedMessage_WithLogger_LogsTheRequeueReasonAsync(string? messageId, string expectedId) {
    var channel = new FakeChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = _newTransport(channel, logger);

    await transport.NackPausedMessageAsync(channel, _args(messageId), "q");

    await Assert.That(channel.LastNackRequeue).IsTrue();
    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("Subscription paused - requeueing message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("paused-2", "paused-2")]
  public async Task NackPausedMessage_ChannelClosedWithLogger_LogsBrokerWillRedeliverAsync(string? messageId, string expectedId) {
    var channel = new FakeChannel { ExceptionToThrowOnNack = RabbitTestWire.NewAlreadyClosedException() };
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = _newTransport(channel, logger);

    await transport.NackPausedMessageAsync(channel, _args(messageId), "q");

    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("NACKing paused message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("bad-1", "bad-1")]
  public async Task NackDeserializationFailure_WithLogger_LogsTheDeadLetterReasonAsync(string? messageId, string expectedId) {
    var channel = new FakeChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = _newTransport(channel, logger);

    await transport.NackDeserializationFailureAsync(channel, _args(messageId), "q");

    await Assert.That(channel.LastNackRequeue).IsFalse();
    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("Deserialization failed for message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("bad-2", "bad-2")]
  public async Task NackDeserializationFailure_ChannelClosedWithLogger_LogsTheSwallowedCloseAsync(string? messageId, string expectedId) {
    var channel = new FakeChannel { ExceptionToThrowOnNack = new ObjectDisposedException(nameof(IChannel)) };
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = _newTransport(channel, logger);

    await transport.NackDeserializationFailureAsync(channel, _args(messageId), "q");

    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("NACKing deserialization failure for message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region Helpers

  private static RabbitMQTransport _newTransport(IChannel channel, ILogger<RabbitMQTransport>? logger) {
    var connection = new FakeConnection(() => Task.FromResult(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    return new RabbitMQTransport(connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger);
  }

  private static BasicDeliverEventArgs _args(string? messageId) =>
    new(
      consumerTag: "test-consumer",
      deliveryTag: 1,
      redelivered: false,
      exchange: "test-exchange",
      routingKey: "#",
      properties: new BasicProperties { MessageId = messageId },
      body: ReadOnlyMemory<byte>.Empty,
      cancellationToken: CancellationToken.None);

  private static async Task<List<IMessageEnvelope>> _subscribeAsync(
      RecordingChannel channel,
      ILogger<RabbitMQTransport>? logger,
      Func<Task>? handlerBehavior = null) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => {
        handled.Add(envelope);
        return handlerBehavior != null ? handlerBehavior() : Task.CompletedTask;
      },
      RabbitTestWire.Destination());
    return handled;
  }

  /// <summary>A poison detector whose evaluation faults — drives the receive failure path
  /// before deserialization has read any header.</summary>
  private sealed class ThrowingPoisonDetector : IPoisonMessageDetector {
    public PoisonVerdict Evaluate(PoisonEvaluationContext context) =>
      throw new InvalidOperationException("detector fault");

    public void RecordQuarantine(
        PoisonQuarantineGate gate,
        PoisonVerdict verdict,
        PoisonEvaluationContext context,
        IReadOnlyDictionary<string, object?>? additionalTags = null) { }

    public void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) { }
  }

  #endregion
}
