// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// A paused RabbitMQ subscription stops consuming (#2406). It used to keep its consumer on the queue and nack every
/// delivery with requeue: each requeue raised the message's delivery count (the redelivered flag, and x-delivery-count on
/// a quorum queue), and with the instance still a consumer the broker kept handing the messages back to it, so a paused
/// instance burned redeliveries and the delivery limit instead of leaving the queue to the instances that are running.
/// Paused, the consumer is canceled; resumed, a consumer is started again.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#paused-subscriptions</docs>
[Timeout(10_000)]
public class RabbitMQPausedSubscriptionTests {
  /// <summary>A paused batch subscription cancels its consumer, so the broker hands it no more deliveries.</summary>
  [Test]
  public async Task Batch_Paused_CancelsItsConsumerAsync(CancellationToken cancellationToken) {
    var (channel, _, subscription) = await _subscribeBatchAsync();
    var consumerTag = channel.ConsumedTags[0];

    await subscription.PauseAsync();

    await Assert.That(channel.CanceledTags).IsEquivalentTo([consumerTag])
      .Because("a paused consumer that stays on the queue is handed every delivery and can only requeue it");
  }

  /// <summary>A paused single-message subscription cancels its consumer.</summary>
  [Test]
  public async Task Single_Paused_CancelsItsConsumerAsync(CancellationToken cancellationToken) {
    var (channel, subscription) = await _subscribeAsync();
    var consumerTag = channel.ConsumedTags[0];

    await subscription.PauseAsync();

    await Assert.That(channel.CanceledTags).IsEquivalentTo([consumerTag]);
  }

  /// <summary>Resuming a paused subscription starts a consumer again, and its deliveries are handled.</summary>
  [Test]
  public async Task Batch_ResumedAfterAPause_ConsumesAgainAndHandlesDeliveriesAsync(CancellationToken cancellationToken) {
    var (channel, batches, subscription) = await _subscribeBatchAsync();
    await subscription.PauseAsync();

    await subscription.ResumeAsync();
    var (props, body) = RabbitTestWire.ValidWireMessage("after-resume");
    await RabbitTestWire.DeliverAsync(channel, props, body, 1);
    await channel.WaitForAckAttemptsAsync(1);

    await Assert.That(channel.ConsumedTags).Count().IsEqualTo(2)
      .Because("the subscribe started one consumer and the resume starts another");
    await Assert.That(batches).Count().IsEqualTo(1);
  }

  /// <summary>
  /// When the consumer cannot be canceled the subscription stays paused and says so; it keeps requeueing what it is
  /// given, and resuming does not start a second consumer next to the one still on the queue.
  /// </summary>
  [Test]
  public async Task Batch_PauseCannotCancelTheConsumer_LogsAWarningAndResumeStartsNoSecondConsumerAsync(CancellationToken cancellationToken) {
    var channel = new RecordingChannel { ExceptionToThrowOnCancel = new InvalidOperationException("cancel failed") };
    var logger = new CapturingLogger<RabbitMQTransport>();
    var (_, _, subscription) = await _subscribeBatchAsync(channel, logger);

    await subscription.PauseAsync();
    await subscription.ResumeAsync();

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning
      && e.Message.Contains("could not cancel its consumer", StringComparison.Ordinal))).IsTrue();
    await Assert.That(channel.ConsumedTags).Count().IsEqualTo(1);
    await Assert.That(subscription.IsActive).IsTrue();
  }

  /// <summary>Disposing a paused subscription does not cancel the consumer the pause already canceled.</summary>
  [Test]
  public async Task Batch_DisposedWhilePaused_DoesNotCancelTheConsumerAgainAsync(CancellationToken cancellationToken) {
    var (channel, _, subscription) = await _subscribeBatchAsync();
    await subscription.PauseAsync();

    subscription.Dispose();
    await ((RabbitMQSubscription)subscription).DisposalCompletion;

    await Assert.That(channel.CanceledTags).Count().IsEqualTo(1);
    await Assert.That(channel.IsDisposed).IsTrue();
  }

  private static async Task<(RecordingChannel Channel, List<IReadOnlyList<TransportMessage>> Batches, ISubscription Subscription)>
      _subscribeBatchAsync(RecordingChannel? channel = null, ILogger<RabbitMQTransport>? logger = null) {
    channel ??= new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
    var batches = new List<IReadOnlyList<TransportMessage>>();
    var subscription = await transport.SubscribeBatchAsync(
      (batch, _) => { batches.Add(batch); return Task.CompletedTask; },
      RabbitTestWire.Destination(),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 600_000, MaxWaitMs = 600_000 });
    return (channel, batches, subscription);
  }

  private static async Task<(RecordingChannel Channel, ISubscription Subscription)> _subscribeAsync() {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var subscription = await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, RabbitTestWire.Destination());
    return (channel, subscription);
  }
}
