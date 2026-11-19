// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// A paused subscription stops receiving (#2406). The run-control path pauses every subscription when an instance stands
/// down for a newer version during a rolling upgrade (and on a pause or a stop). The subscription used to stay on the
/// broker with its processor running, and abandon every delivery it took: each abandon counted a delivery, and a session
/// whose messages keep arriving (an origin's checkpoints share one) stayed with the standing-down instance while every
/// message in it burned toward the dead-letter limit. Paused, the processor is stopped: it takes no session and no
/// message, so nothing is counted and another instance takes the session at once.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#paused-subscriptions</docs>
[Timeout(10_000)]
public class AzureServiceBusPausedSubscriptionTests {
  private const string CHECKPOINT_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core]], Whizbang.Core";

  /// <summary>
  /// A session subscription paused for a stand-down stops its processor, so no further checkpoint delivery is taken and
  /// counted on this instance. Before the fix the processor kept running and every checkpoint it took was abandoned.
  /// </summary>
  [Test]
  public async Task SessionBatch_PausedForAStandDown_StopsTakingCheckpointDeliveriesAsync() {
    var (transport, client, _) = _createTransport(enableSessions: true);
    var subscription = await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    var processor = client.LastSessionProcessor!;

    await subscription.PauseAsync();

    await Assert.That(processor.StopProcessingAsyncCallCount).IsEqualTo(1)
      .Because("a paused subscription must not keep taking sessions: every delivery it takes is abandoned and counted");
    await Assert.That(subscription.IsActive).IsFalse();
  }

  /// <summary>
  /// A checkpoint that reaches a paused subscription before its processor has stopped is handed back once, with the
  /// unsettled Warning; it is the only delivery the pause can cost.
  /// </summary>
  [Test]
  public async Task SessionBatch_ACheckpointInFlightAsThePauseLands_IsHandedBackOnceWithAWarningAsync() {
    var (transport, client, logger) = _createTransport(enableSessions: true);
    var handled = false;
    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => { handled = true; return Task.CompletedTask; }, _destination(), new TransportBatchOptions());
    await subscription.PauseAsync();
    var receiver = new RecordingTransportSessionReceiver();
    var checkpoint = AsbTransportTestData.RawMessage("{}", CHECKPOINT_ENVELOPE_TYPE, deliveryCount: 1);

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(checkpoint, receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(receiver.Completed).IsEmpty();
    await Assert.That(handled).IsFalse();
    await Assert.That(logger.Contains(LogLevel.Warning, "Whizbang.Core.Messaging.IntegrityCheckpoint")).IsTrue();
  }

  /// <summary>A non-session subscription paused for a stand-down stops its processor as well.</summary>
  [Test]
  public async Task NonSessionBatch_Paused_StopsItsProcessorAsync() {
    var (transport, client, _) = _createTransport(enableSessions: false);
    var subscription = await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), _sizeOnlyBatchOptions());
    var processor = client.LastProcessor!;

    await subscription.PauseAsync();

    await Assert.That(processor.StopProcessingAsyncCallCount).IsEqualTo(1);
  }

  /// <summary>Resuming a paused session subscription starts its processor again and handles deliveries.</summary>
  [Test]
  public async Task SessionBatch_ResumedAfterAPause_StartsItsProcessorAndHandlesDeliveriesAsync() {
    var (transport, client, _) = _createTransport(enableSessions: true);
    var handled = 0;
    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => { handled++; return Task.CompletedTask; }, _destination(), new TransportBatchOptions());
    var processor = client.LastSessionProcessor!;
    await subscription.PauseAsync();

    await subscription.ResumeAsync();
    var receiver = new RecordingTransportSessionReceiver();
    await processor.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(processor.StartProcessingAsyncCallCount).IsEqualTo(2)
      .Because("the subscribe started it once and the resume starts it again");
    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(handled).IsEqualTo(1);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }

  /// <summary>Resuming a paused non-session subscription starts its processor again.</summary>
  [Test]
  public async Task NonSessionBatch_ResumedAfterAPause_StartsItsProcessorAsync() {
    var (transport, client, _) = _createTransport(enableSessions: false);
    var subscription = await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), _sizeOnlyBatchOptions());
    var processor = client.LastProcessor!;
    await subscription.PauseAsync();

    await subscription.ResumeAsync();

    await Assert.That(processor.StartProcessingAsyncCallCount).IsEqualTo(2);
  }

  /// <summary>
  /// When the processor cannot be stopped the subscription stays paused and says so: it keeps receiving, and each
  /// delivery is handed back with the unsettled Warning.
  /// </summary>
  [Test]
  public async Task SessionBatch_PauseCannotStopTheProcessor_LogsAWarningAndStaysPausedAsync() {
    var (transport, client, logger) = _createTransport(enableSessions: true);
    var subscription = await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    client.LastSessionProcessor!.StopProcessingException = new InvalidOperationException("stop failed");

    await subscription.PauseAsync();
    await subscription.ResumeAsync();

    await Assert.That(logger.ContainsWithException(LogLevel.Warning, "could not stop its processor")).IsTrue();
    await Assert.That(client.LastSessionProcessor.StartProcessingAsyncCallCount).IsEqualTo(2)
      .Because("a failed stop does not block the resume");
  }

  /// <summary>
  /// A namespace-throttle pause that ends while the subscription is paused does not start the processor again: the
  /// subscription stays stopped until it is resumed.
  /// </summary>
  [Test]
  public async Task NamespaceThrottle_EndingWhileTheSubscriptionIsPaused_LeavesTheProcessorStoppedAsync() {
    var clock = new ArmingClock();
    var (transport, client, logger) = _createTransport(enableSessions: true, clock);
    var subscription = await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var processor = client.LastSessionProcessor!;

    await processor.RaiseErrorAsync(new ProcessErrorEventArgs(
      new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy),
      ServiceBusErrorSource.Receive, "unit-test.servicebus.windows.net", "paused-topic", CancellationToken.None));
    await clock.WaitForArmedAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    await subscription.PauseAsync();
    clock.Advance(TimeSpan.FromSeconds(2));
    await transport.ThrottlePause;

    await Assert.That(processor.StartProcessingAsyncCallCount).IsEqualTo(1)
      .Because("only the subscribe started it; the throttle pause must not restart a paused subscription");
    await Assert.That(logger.Contains(LogLevel.Information, "the subscription is paused, so its processor stays stopped")).IsTrue();
  }

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, RecordingTransportLogger Logger)
      _createTransport(bool enableSessions, TimeProvider? timeProvider = null) {
    var client = new RaisableServiceBusClient();
    var logger = new RecordingTransportLogger();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions,
      EnableAdaptiveAcceptors = false,
      EnableReceiveLivenessWatchdog = false,
    };
    var transport = new AzureServiceBusTransport(
      client, AsbTransportTestData.CombinedOptions, options, logger, typeBinder: new MissTypeBinder(),
      timeProvider: timeProvider);
    return (transport, client, logger);
  }

  /// <summary>BatchSize=1 flushes on the first enqueue; the timers are pushed out so only the size trigger fires.</summary>
  private static TransportBatchOptions _sizeOnlyBatchOptions() => new() {
    BatchSize = 1,
    SlideMs = 600_000,
    MaxWaitMs = 600_000,
  };

  private static TransportDestination _destination() => new("paused-topic", "paused-sub");
}
