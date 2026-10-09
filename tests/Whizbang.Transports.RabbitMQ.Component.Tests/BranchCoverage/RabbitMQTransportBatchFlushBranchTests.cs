// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.RabbitMQ.Tests.BranchCoverage;

/// <summary>
/// Branch coverage for <see cref="RabbitMQTransport.FlushBatchAsync"/> and the copied-body
/// deserializer behind it, driven through the internal flush seam so every outcome is awaited
/// directly (no collector timers). The existing batch suites run these paths without a logger;
/// these tests take the logging arms (and the "unknown" MessageId fallback) and the
/// logger-absent arm of the channel-closed handler path.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQTransport.cs</code-under-test>
public class RabbitMQTransportBatchFlushBranchTests {
  private const string QUEUE = "batch-queue";

  [Test]
  public async Task Flush_HandlerThrowsAlreadyClosedWithoutLogger_NeitherAcksNorNacksAsync() {
    var channel = new RecordingChannel();
    var transport = await _transportAsync(channel, logger: null);
    var (props, body) = RabbitTestWire.ValidWireMessage();

    await Assert.That(async () => await transport.FlushBatchAsync(
      [_pending(channel, props, body, 1)],
      (_, _) => throw RabbitTestWire.NewAlreadyClosedException(),
      null,
      QUEUE)).ThrowsNothing();

    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(channel.NackAttempts).IsEmpty()
      .Because("a closed channel cannot settle; the broker redelivers the unacked batch");
  }

  [Test]
  public async Task Flush_HandlerThrowsGenericWithLogger_LogsAndNacksEachForRedeliveryAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var (props1, body1) = RabbitTestWire.ValidWireMessage("a");
    var (props2, body2) = RabbitTestWire.ValidWireMessage("b");

    await transport.FlushBatchAsync(
      [_pending(channel, props1, body1, 1), _pending(channel, props2, body2, 2)],
      (_, _) => throw new InvalidOperationException("batch handler boom"),
      null,
      QUEUE);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(2);
    await Assert.That(channel.NackAttempts.All(n => n.Requeue)).IsTrue()
      .Because("a failed batch is requeued so every message is redelivered");
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Error processing batch of 2 messages", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("disposed-1", "disposed-1")]
  public async Task Flush_HeadersThrowObjectDisposedWithLogger_LogsAndDropsTheMessageAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var handlerCalled = false;

    await transport.FlushBatchAsync(
      [_pending(channel, new DisposedHeadersProperties(messageId), "{}"u8.ToArray(), 1)],
      (_, _) => {
        handlerCalled = true;
        return Task.CompletedTask;
      },
      null,
      QUEUE);

    await Assert.That(handlerCalled).IsFalse();
    await Assert.That(channel.NackAttempts).IsEmpty();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("while deserializing message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("ack-1", "ack-1")]
  public async Task Flush_AckThrowsAlreadyClosedWithLogger_LogsWillBeRedeliveredAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel { ExceptionToThrowOnAck = RabbitTestWire.NewAlreadyClosedException() };
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var (props, body) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;

    await transport.FlushBatchAsync([_pending(channel, props, body, 1)], (_, _) => Task.CompletedTask, null, QUEUE);

    await Assert.That(channel.AckAttempts).Count().IsEqualTo(1);
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("while ACKing message " + expectedId, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("empty-1", "empty-1")]
  public async Task Flush_EmptyBodyWithLogger_LogsAndDeadLettersAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var (props, _) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;

    await transport.FlushBatchAsync([_pending(channel, props, [], 4)], (_, _) => Task.CompletedTask, null, QUEUE);

    await Assert.That(channel.NackAttempts.Single()).IsEqualTo((4UL, false));
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Message " + expectedId + " has an empty body", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  [Arguments(null, "unknown")]
  [Arguments("nul-1", "nul-1")]
  public async Task Flush_NulPrefixedBodyWithLogger_LogsNotValidJsonAsync(string? messageId, string expectedId) {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var (props, _) = RabbitTestWire.ValidWireMessage();
    props.MessageId = messageId;

    await transport.FlushBatchAsync([_pending(channel, props, [0, 0, 0], 5)], (_, _) => Task.CompletedTask, null, QUEUE);

    await Assert.That(channel.NackAttempts.Single()).IsEqualTo((5UL, false));
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error
      && e.Message.Contains("Message " + expectedId + " body is not valid JSON (starts with 0x000000)", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Flush_MissingEnvelopeTypeHeaderWithLogger_LogsAndDeadLettersAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);

    await transport.FlushBatchAsync(
      [_pending(channel, new BasicProperties { MessageId = "no-type" }, "{}"u8.ToArray(), 6)],
      (_, _) => Task.CompletedTask, null, QUEUE);

    await Assert.That(channel.NackAttempts.Single()).IsEqualTo((6UL, false));
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("no-type missing EnvelopeType header", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Flush_UnknownEnvelopeTypeWithLogger_LogsMissingTypeInfoAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var props = new BasicProperties {
      MessageId = "unknown-type",
      Headers = new Dictionary<string, object?> {
        ["EnvelopeType"] = Encoding.UTF8.GetBytes("Whizbang.Tests.DoesNotExist.Envelope, Whizbang.DoesNotExist")
      }
    };

    await transport.FlushBatchAsync([_pending(channel, props, "{}"u8.ToArray(), 7)], (_, _) => Task.CompletedTask, null, QUEUE);

    await Assert.That(channel.NackAttempts.Single()).IsEqualTo((7UL, false));
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("No JsonTypeInfo found", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Flush_BodyIsNotAnEnvelopeWithLogger_LogsDeserializeFailureAsync() {
    var channel = new RecordingChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await _transportAsync(channel, logger);
    var (props, body) = RabbitTestWire.NonEnvelopeWireMessage();

    await transport.FlushBatchAsync([_pending(channel, props, body, 8)], (_, _) => Task.CompletedTask, null, QUEUE);

    await Assert.That(channel.NackAttempts.Single()).IsEqualTo((8UL, false));
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Failed to deserialize message non-envelope", StringComparison.Ordinal))).IsTrue();
  }

  private static async Task<RabbitMQTransport> _transportAsync(RecordingChannel channel, ILogger<RabbitMQTransport>? logger) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    return await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
  }

  private static RabbitMQTransport.PendingRabbitMessage _pending(
      IChannel channel, IReadOnlyBasicProperties properties, byte[] body, ulong deliveryTag) {
    var args = new BasicDeliverEventArgs(
      consumerTag: "test-consumer",
      deliveryTag: deliveryTag,
      redelivered: false,
      exchange: "test-exchange",
      routingKey: "#",
      properties: properties,
      body: body,
      cancellationToken: CancellationToken.None);
    return new RabbitMQTransport.PendingRabbitMessage(channel, args, body, QUEUE);
  }

  /// <summary>Properties whose Headers getter throws as if the channel were torn down mid-read,
  /// with a configurable MessageId (the shared double fixes it to a non-null value).</summary>
  private sealed class DisposedHeadersProperties(string? messageId) : IReadOnlyBasicProperties {
    public string? AppId => null;
    public string? ClusterId => null;
    public string? ContentEncoding => null;
    public string? ContentType => null;
    public string? CorrelationId => null;
    public DeliveryModes DeliveryMode => DeliveryModes.Persistent;
    public string? Expiration => null;
    public IDictionary<string, object?>? Headers => throw new ObjectDisposedException(nameof(IChannel));
    public string? MessageId => messageId;
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
    public bool IsMessageIdPresent() => messageId is not null;
    public bool IsPriorityPresent() => false;
    public bool IsReplyToPresent() => false;
    public bool IsTimestampPresent() => false;
    public bool IsTypePresent() => false;
    public bool IsUserIdPresent() => false;
  }
}
