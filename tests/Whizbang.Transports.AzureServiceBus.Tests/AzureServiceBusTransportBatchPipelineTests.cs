using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for AzureServiceBusTransport's SubscribeBatchAsync message pipelines: the
/// session-mode single-item fallback (_handleSessionBatchMessageAsync) and the non-session
/// TransportBatchCollector flush (deserialize → batch handler → per-message complete).
/// Raisable processors pump the pipelines without a broker; the collector's Task.Run flush
/// is awaited via completion signals on the recording receiver / logger — no polling.
/// </summary>
[Timeout(10_000)]
public class AzureServiceBusTransportBatchPipelineTests {

  // ========================================
  // SESSION-MODE BATCH PIPELINE (single-item fallback)
  // ========================================

  /// <summary>
  /// Session batch mode dispatches each message as a single-item batch and completes it
  /// after the handler returns.
  /// </summary>
  [Test]
  public async Task SessionBatch_ValidEnvelope_InvokesBatchHandlerAndCompletesAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var batches = new List<IReadOnlyList<TransportMessage>>();
    await transport.SubscribeBatchAsync(
      (batch, _) => { batches.Add(batch); return Task.CompletedTask; },
      _destination(),
      new TransportBatchOptions());
    var receiver = new RecordingTransportSessionReceiver();
    var envelope = AsbTransportTestData.CreateEnvelope();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(AsbTransportTestData.EnvelopeMessage(envelope), receiver));

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0]).Count().IsEqualTo(1);
    await Assert.That(batches[0][0].Envelope.MessageId.Value).IsEqualTo(envelope.MessageId.Value);
    await Assert.That(batches[0][0].EnvelopeType).IsEqualTo(typeof(Whizbang.Core.Observability.MessageEnvelope<TestMessage>).AssemblyQualifiedName);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.Abandoned).IsEmpty();
  }

  /// <summary>A paused session batch subscription abandons messages without dispatching.</summary>
  [Test]
  public async Task SessionBatch_PausedSubscription_AbandonsWithoutInvokingHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var handlerInvoked = false;
    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination(),
      new TransportBatchOptions());
    await subscription.PauseAsync();
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>
  /// A session batch message whose envelope type cannot be resolved is acked and dropped by
  /// the deserializer — the batch handler never sees it.
  /// </summary>
  [Test]
  public async Task SessionBatch_UnknownEnvelopeType_AcksWithoutInvokingHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var handlerInvoked = false;
    await transport.SubscribeBatchAsync(
      (_, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination(),
      new TransportBatchOptions());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.RawMessage("{}", "Unknown.Namespace.NoSuchEnvelope, NoSuchAssembly"), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("unresolvable types are acked so they exit the topic without DLQ accumulation");
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>
  /// A session batch message without EnvelopeType metadata has nothing to route on and is
  /// dead-lettered at the broker (session dead-letter branch of the decision switch).
  /// </summary>
  [Test]
  public async Task SessionBatch_MissingEnvelopeType_DeadLettersWithoutInvokingHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    var handlerInvoked = false;
    await transport.SubscribeBatchAsync(
      (_, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination(),
      new TransportBatchOptions());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.RawMessage("{}", envelopeTypeName: null), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo(AsbReceiveReason.MISSING_ENVELOPE_TYPE);
    await Assert.That(handlerInvoked).IsFalse();
  }

  /// <summary>
  /// A batch-handler failure below MaxDeliveryAttempts routes through the session error
  /// handler and abandons the message for redelivery.
  /// </summary>
  [Test]
  public async Task SessionBatch_HandlerThrows_AbandonsForRedeliveryAsync() {
    var (transport, client) = _createTransport(enableSessions: true);
    await transport.SubscribeBatchAsync(
      (_, _) => throw new InvalidOperationException("session batch boom"),
      _destination(),
      new TransportBatchOptions());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered).IsEmpty();
  }

  // ========================================
  // NON-SESSION BATCH PIPELINE (TransportBatchCollector flush)
  // ========================================

  /// <summary>
  /// BatchSize=1 triggers an immediate collector flush: the message is deserialized, handed
  /// to the batch handler, and completed per-message afterwards.
  /// </summary>
  [Test]
  public async Task NonSessionBatch_BatchSizeReached_FlushesToHandlerAndCompletesAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var batches = new List<IReadOnlyList<TransportMessage>>();
    await transport.SubscribeBatchAsync(
      (batch, _) => { batches.Add(batch); return Task.CompletedTask; },
      _destination(),
      _sizeOnlyBatchOptions());
    var receiver = new RecordingTransportReceiver();
    var envelope = AsbTransportTestData.CreateEnvelope();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(envelope), receiver));
    await receiver.CompletedSignal.Task;

    await Assert.That(batches).Count().IsEqualTo(1);
    await Assert.That(batches[0]).Count().IsEqualTo(1);
    await Assert.That(batches[0][0].Envelope.MessageId.Value).IsEqualTo(envelope.MessageId.Value);
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await Assert.That(receiver.Abandoned).IsEmpty();
  }

  /// <summary>
  /// When every message in the flushed batch fails deserialization (ack+drop), the flush
  /// returns without invoking the batch handler.
  /// </summary>
  [Test]
  public async Task NonSessionBatch_AllMessagesFailDeserialization_SkipsHandlerAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handlerInvocations = 0;
    await transport.SubscribeBatchAsync(
      (_, _) => { handlerInvocations++; return Task.CompletedTask; },
      _destination(),
      _sizeOnlyBatchOptions());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.RawMessage("{}", "Unknown.Namespace.NoSuchEnvelope, NoSuchAssembly"), receiver));
    await receiver.CompletedSignal.Task;

    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("the undeserializable message is acked during the flush's deserialize step");
    await Assert.That(handlerInvocations).IsEqualTo(0)
      .Because("an all-dropped batch must not invoke the handler with an empty list");
  }

  /// <summary>
  /// A ServiceBusException from per-message CompleteMessageAsync after a successful flush is
  /// logged as a warning (redelivery after lock expiry) — the flush must not propagate it.
  /// </summary>
  [Test]
  public async Task NonSessionBatch_CompleteThrowsServiceBusException_LogsWarningAndContinuesAsync() {
    var logger = new RecordingTransportLogger();
    var warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (level, message) => {
      if (level == LogLevel.Warning && message.Contains("Failed to complete message", StringComparison.Ordinal)) {
        warned.TrySetResult();
      }
    };
    var (transport, client) = _createTransport(enableSessions: false, logger: logger);
    var batches = new List<IReadOnlyList<TransportMessage>>();
    await transport.SubscribeBatchAsync(
      (batch, _) => { batches.Add(batch); return Task.CompletedTask; },
      _destination(),
      _sizeOnlyBatchOptions());
    var receiver = new RecordingTransportReceiver {
      CompleteException = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost)
    };

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));
    await warned.Task;

    await Assert.That(batches).Count().IsEqualTo(1)
      .Because("the handler ran before the completion failure");
    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("the complete attempt is made, then its failure is logged as a warning");
  }

  /// <summary>
  /// A paused non-session batch subscription abandons incoming messages instead of
  /// enqueueing them to the collector.
  /// </summary>
  [Test]
  public async Task NonSessionBatch_PausedSubscription_AbandonsMessageAsync() {
    var (transport, client) = _createTransport(enableSessions: false);
    var handlerInvoked = false;
    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _destination(),
      _sizeOnlyBatchOptions());
    await subscription.PauseAsync();
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await Assert.That(handlerInvoked).IsFalse();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport(
    bool enableSessions,
    RecordingTransportLogger? logger = null) {
    var client = new RaisableServiceBusClient();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      (ILogger<AzureServiceBusTransport>?)logger ?? NullLogger<AzureServiceBusTransport>.Instance);
    return (transport, client);
  }

  /// <summary>
  /// BatchSize=1 flushes immediately on the first enqueue; the slide / hard-max timers are
  /// pushed far out so only the size trigger can fire (no timing dependence).
  /// </summary>
  private static TransportBatchOptions _sizeOnlyBatchOptions() => new() {
    BatchSize = 1,
    SlideMs = 600_000,
    MaxWaitMs = 600_000
  };

  private static TransportDestination _destination() => new("batch-topic", "batch-sub");
}
