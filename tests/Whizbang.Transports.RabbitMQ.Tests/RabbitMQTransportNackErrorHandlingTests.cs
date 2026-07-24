using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Change-level tests for the a consumer 2026-05-04 hardening of <c>_nackPausedMessageAsync</c>
/// and <c>_nackDeserializationFailureAsync</c> against <see cref="AlreadyClosedException"/>
/// (e.g., 406 PRECONDITION_FAILED — unknown delivery tag, after channel auto-recovery).
///
/// <para>
/// Without the try/catch added in this branch, an exception thrown by
/// <see cref="IChannel.BasicNackAsync"/> escapes the consumer's <c>ReceivedAsync</c> event
/// handler, surfacing as an unhandled exception that crashes RabbitMQ's
/// <c>AsyncEventingBasicConsumer</c> dispatch thread. With the fix, the exception is
/// logged and swallowed — the broker will redeliver the message after auto-recovery.
/// </para>
/// </summary>
public class RabbitMQTransportNackErrorHandlingTests {

  private static RabbitMQTransport _newTransport(IChannel channel) {
    var fakeConnection = new FakeConnection(() => Task.FromResult(channel));
    var pool = new RabbitMQChannelPool(fakeConnection, maxChannels: 5);
    var jsonOptions = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    return new RabbitMQTransport(
      fakeConnection, jsonOptions, pool, new RabbitMQOptions(), logger: null);
  }

  private static BasicDeliverEventArgs _envelope(IChannel channel, ulong tag = 1) {
    var props = new BasicProperties { MessageId = "test-msg-1" };
    return new BasicDeliverEventArgs(
      consumerTag: "test-consumer",
      deliveryTag: tag,
      redelivered: false,
      exchange: "test-exchange",
      routingKey: "#",
      properties: props,
      body: ReadOnlyMemory<byte>.Empty,
      cancellationToken: CancellationToken.None);
  }

  private static AlreadyClosedException _newAlreadyClosed() =>
    new(new ShutdownEventArgs(
      ShutdownInitiator.Peer,
      replyCode: 406,
      replyText: "PRECONDITION_FAILED - unknown delivery tag 1"));

  /// <summary>
  /// _nackPausedMessageAsync (paused-subscription requeue) must SWALLOW
  /// AlreadyClosedException instead of letting it escape. RED before the fix:
  /// exception propagates and unhandled. GREEN after: try/catch logs + returns.
  /// </summary>
  [Test]
  public async Task NackPausedMessage_WhenChannelAlreadyClosed_SwallowsExceptionAsync() {
    var channel = new FakeChannel { ExceptionToThrowOnNack = _newAlreadyClosed() };
    var transport = _newTransport(channel);
    var args = _envelope(channel);

    var method = typeof(RabbitMQTransport).GetMethod(
      "_nackPausedMessageAsync",
      BindingFlags.Instance | BindingFlags.NonPublic)!;

    Exception? caught = null;
    try {
      var task = (Task)method.Invoke(transport, [(IChannel)channel, args, "test-queue"])!;
      await task;
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull()
      .Because("AlreadyClosedException must be swallowed inside _nackPausedMessageAsync. Otherwise it escapes the AsyncEventingBasicConsumer.ReceivedAsync handler and crashes the dispatch thread.");
  }

  /// <summary>
  /// _nackPausedMessageAsync also swallows ObjectDisposedException — same
  /// rationale (channel disposed during shutdown).
  /// </summary>
  [Test]
  public async Task NackPausedMessage_WhenChannelDisposed_SwallowsExceptionAsync() {
    var channel = new FakeChannel {
      ExceptionToThrowOnNack = new ObjectDisposedException(nameof(IChannel))
    };
    var transport = _newTransport(channel);
    var args = _envelope(channel);

    var method = typeof(RabbitMQTransport).GetMethod(
      "_nackPausedMessageAsync",
      BindingFlags.Instance | BindingFlags.NonPublic)!;

    Exception? caught = null;
    try {
      var task = (Task)method.Invoke(transport, [(IChannel)channel, args, "test-queue"])!;
      await task;
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull();
  }

  /// <summary>
  /// _nackDeserializationFailureAsync (DLQ-bound NACK) must also swallow
  /// AlreadyClosedException. Same call site shape: invoked from the consumer's
  /// ReceivedAsync without an outer try/catch around it.
  /// </summary>
  [Test]
  public async Task NackDeserializationFailure_WhenChannelAlreadyClosed_SwallowsExceptionAsync() {
    var channel = new FakeChannel { ExceptionToThrowOnNack = _newAlreadyClosed() };
    var transport = _newTransport(channel);
    var args = _envelope(channel);

    var method = typeof(RabbitMQTransport).GetMethod(
      "_nackDeserializationFailureAsync",
      BindingFlags.Instance | BindingFlags.NonPublic)!;

    Exception? caught = null;
    try {
      var task = (Task)method.Invoke(transport, [(IChannel)channel, args, "test-queue"])!;
      await task;
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNull()
      .Because("AlreadyClosedException during dead-letter NACK must be swallowed. The broker will redeliver after auto-recovery; an unhandled exception crashes the consumer dispatch thread.");
  }

  /// <summary>
  /// Unrelated exceptions (e.g., a transient transport error other than
  /// AlreadyClosed/ObjectDisposed) must STILL propagate so they can be diagnosed.
  /// The catch is intentionally narrow — anything else is a real bug to surface.
  /// </summary>
  [Test]
  public async Task NackPausedMessage_WhenUnrelatedExceptionThrown_PropagatesAsync() {
    var channel = new FakeChannel { ExceptionToThrowOnNack = new InvalidOperationException("unrelated") };
    var transport = _newTransport(channel);
    var args = _envelope(channel);

    var method = typeof(RabbitMQTransport).GetMethod(
      "_nackPausedMessageAsync",
      BindingFlags.Instance | BindingFlags.NonPublic)!;

    InvalidOperationException? caught = null;
    try {
      var task = (Task)method.Invoke(transport, [(IChannel)channel, args, "test-queue"])!;
      await task;
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull()
      .Because("The catch is narrow on purpose — only channel-closed/disposed exceptions are swallowed. Unrelated exceptions must propagate.");
  }

  /// <summary>
  /// Happy path: when no exception is thrown, the NACK reaches the channel and
  /// the requeue flag is set as expected (paused → requeue=true).
  /// </summary>
  [Test]
  public async Task NackPausedMessage_HappyPath_CallsBasicNackWithRequeueAsync() {
    var channel = new FakeChannel();
    var transport = _newTransport(channel);
    var args = _envelope(channel, tag: 42);

    var method = typeof(RabbitMQTransport).GetMethod(
      "_nackPausedMessageAsync",
      BindingFlags.Instance | BindingFlags.NonPublic)!;

    var task = (Task)method.Invoke(transport, [(IChannel)channel, args, "test-queue"])!;
    await task;

    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackedDeliveryTag).IsEqualTo(42UL);
    await Assert.That(channel.LastNackRequeue).IsTrue()
      .Because("Paused subscription must requeue (not dead-letter) so the message is redelivered when active.");
  }

  /// <summary>
  /// Happy path for deserialization-failure NACK: requeue=false (dead-letter route).
  /// </summary>
  [Test]
  public async Task NackDeserializationFailure_HappyPath_CallsBasicNackWithoutRequeueAsync() {
    var channel = new FakeChannel();
    var transport = _newTransport(channel);
    var args = _envelope(channel, tag: 99);

    var method = typeof(RabbitMQTransport).GetMethod(
      "_nackDeserializationFailureAsync",
      BindingFlags.Instance | BindingFlags.NonPublic)!;

    var task = (Task)method.Invoke(transport, [(IChannel)channel, args, "test-queue"])!;
    await task;

    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackedDeliveryTag).IsEqualTo(99UL);
    await Assert.That(channel.LastNackRequeue).IsFalse()
      .Because("Deserialization failure must NOT requeue — dead-letter the poison message instead.");
  }
}
