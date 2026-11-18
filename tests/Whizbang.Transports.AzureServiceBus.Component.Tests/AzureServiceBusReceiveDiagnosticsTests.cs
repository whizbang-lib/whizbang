// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The receive path explains every delivery it is given (#2406): a delivery given back to the broker unsettled is a
/// Warning naming the message, the reason and the delivery count; every delivery handed to the handler is a Debug line
/// with its id and delivery count; and a delivery past the first is an Information line, so a message whose earlier
/// deliveries never reached this code is visible before it reaches the dead-letter limit.
/// </summary>
/// <remarks>
/// Every path is pumped through raisable processors without a broker. The session paths run the handler inline, so the
/// assertions follow the raise; the non-session batch path flushes on the collector, and its tests wait on the receiver's
/// settlement signal or the handler's own completion source.
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#receive-diagnostics</docs>
[Timeout(10_000)]
public class AzureServiceBusReceiveDiagnosticsTests {
  private const string UNSETTLED = "was returned to the broker unsettled";
  private const string PAUSED_REASON = "the subscription is paused or disposed";
  private const string HANDLER_FAILED_REASON = "the handler failed";
  private const string HANDED = "to the handler";
  private const string REDELIVERED = "an earlier delivery was not settled";
  private const string ENTITY = "diag-topic/diag-sub";

  private static readonly string _testMessageType = typeof(TestMessage).FullName!;

  // ========================================
  // UNSETTLED: a paused or disposed subscription gives the delivery back
  // ========================================

  /// <summary>The session batch path abandons a delivery on a paused subscription with a Warning naming it.</summary>
  [Test]
  public async Task SessionBatch_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    var subscription = await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    await subscription.PauseAsync();
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 3);
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(message, receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await _assertUnsettledAsync(logger, message.MessageId, _testMessageType, 3, PAUSED_REASON);
    await _assertRedeliveredOnceAsync(logger, message.MessageId, _testMessageType, 3);
  }

  /// <summary>
  /// The non-session batch path abandons a delivery on a paused subscription with a Warning naming it. A message with no
  /// EnvelopeType property is named by its id with the type "unknown".
  /// </summary>
  [Test]
  public async Task NonSessionBatch_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: false, logger);
    var subscription = await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), _sizeOnlyBatchOptions());
    await subscription.PauseAsync();
    var message = AsbTransportTestData.RawMessage("{}", envelopeTypeName: null, deliveryCount: 4);
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(message, receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await _assertUnsettledAsync(logger, message.MessageId, "unknown", 4, PAUSED_REASON);
    await _assertRedeliveredOnceAsync(logger, message.MessageId, "unknown", 4);
  }

  /// <summary>
  /// The single-message session path names the delivery it abandons on a paused subscription. A type name that is not an
  /// envelope name is reported as the type itself, without its assembly.
  /// </summary>
  [Test]
  public async Task Session_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    var subscription = await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    await subscription.PauseAsync();
    var message = AsbTransportTestData.RawMessage("{}", "Plain.Namespace.PlainType, Plain.Assembly", deliveryCount: 2);
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(message, receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await _assertUnsettledAsync(logger, message.MessageId, "Plain.Namespace.PlainType", 2, PAUSED_REASON);
    await _assertRedeliveredOnceAsync(logger, message.MessageId, "Plain.Namespace.PlainType", 2);
  }

  /// <summary>The single-message non-session path names the delivery it abandons on a paused subscription.</summary>
  [Test]
  public async Task NonSession_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: false, logger);
    var subscription = await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    await subscription.PauseAsync();
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 5);
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(message, receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await _assertUnsettledAsync(logger, message.MessageId, _testMessageType, 5, PAUSED_REASON);
    await _assertRedeliveredOnceAsync(logger, message.MessageId, _testMessageType, 5);
  }

  // ========================================
  // UNSETTLED: a failed handler gives the delivery back for redelivery
  // ========================================

  /// <summary>A session delivery whose handler fails is abandoned with a Warning naming it and the reason.</summary>
  [Test]
  public async Task SessionBatch_HandlerFails_LogsUnsettledWarningNamingTheMessageAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    await transport.SubscribeBatchAsync(
      (_, _) => throw new InvalidOperationException("store failed"), _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(message, receiver));

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await _assertUnsettledAsync(logger, message.MessageId, _testMessageType, 1, HANDLER_FAILED_REASON);
  }

  /// <summary>A non-session delivery whose batch fails is abandoned with a Warning naming it and the reason.</summary>
  [Test]
  public async Task NonSessionBatch_HandlerFails_LogsUnsettledWarningNamingTheMessageAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: false, logger);
    await transport.SubscribeBatchAsync(
      (_, _) => throw new InvalidOperationException("store failed"), _destination(), _sizeOnlyBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(message, receiver));
    await receiver.NotCompletedSignal.Task;

    await Assert.That(receiver.Abandoned).Count().IsEqualTo(1);
    await _assertUnsettledAsync(logger, message.MessageId, _testMessageType, 1, HANDLER_FAILED_REASON);
  }

  /// <summary>
  /// At the delivery limit a failed delivery is dead-lettered, which settles it: no unsettled Warning is written for it.
  /// </summary>
  [Test]
  public async Task SessionBatch_HandlerFailsAtTheDeliveryLimit_DeadLettersWithoutAnUnsettledWarningAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger, maxDeliveryAttempts: 2);
    await transport.SubscribeBatchAsync(
      (_, _) => throw new InvalidOperationException("store failed"), _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 2);
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(message, receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(logger.Count(LogLevel.Warning, UNSETTLED)).IsEqualTo(0);
  }

  // ========================================
  // HANDED TO THE HANDLER: one Debug line per delivery
  // ========================================

  /// <summary>The session batch path logs each delivery it hands to the handler, with its id and delivery count.</summary>
  [Test]
  public async Task SessionBatch_HandsADeliveryToTheHandler_LogsItsIdAndDeliveryCountAtDebugAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(message, new RecordingTransportSessionReceiver()));

    await _assertHandedAsync(logger, message.MessageId, 1);
  }

  /// <summary>The non-session batch path logs each delivery it hands to the handler, with its id and delivery count.</summary>
  [Test]
  public async Task NonSessionBatch_HandsADeliveryToTheHandler_LogsItsIdAndDeliveryCountAtDebugAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: false, logger);
    var receiver = new RecordingTransportReceiver();
    await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), _sizeOnlyBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(message, receiver));
    await receiver.CompletedSignal.Task;

    await _assertHandedAsync(logger, message.MessageId, 1);
  }

  /// <summary>The single-message session path logs each delivery it hands to the handler.</summary>
  [Test]
  public async Task Session_HandsADeliveryToTheHandler_LogsItsIdAndDeliveryCountAtDebugAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(message, new RecordingTransportSessionReceiver()));

    await _assertHandedAsync(logger, message.MessageId, 1);
  }

  /// <summary>The single-message non-session path logs each delivery it hands to the handler.</summary>
  [Test]
  public async Task NonSession_HandsADeliveryToTheHandler_LogsItsIdAndDeliveryCountAtDebugAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: false, logger);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(message, new RecordingTransportReceiver()));

    await _assertHandedAsync(logger, message.MessageId, 1);
  }

  /// <summary>
  /// With Debug off the handed-to-handler line is never formatted: the hot path pays only the level check.
  /// </summary>
  [Test]
  public async Task HandsADeliveryToTheHandler_WithDebugOff_FormatsNothingAsync() {
    var logger = new LevelRecordingLogger(LogLevel.Information);
    var (transport, client) = _createTransport(enableSessions: true, logger);
    var handled = false;
    await transport.SubscribeBatchAsync((_, _) => { handled = true; return Task.CompletedTask; }, _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(message, new RecordingTransportSessionReceiver()));

    await Assert.That(handled).IsTrue();
    await Assert.That(logger.CalledBelowMinimum(HANDED)).IsEqualTo(0)
      .Because("a disabled level must not reach the logger at all, so the receive hot path pays only the level check");
    await Assert.That(logger.Count(LogLevel.Debug, HANDED)).IsEqualTo(0);
  }

  // ========================================
  // REDELIVERED: one Information line for a delivery past the first
  // ========================================

  /// <summary>
  /// A first delivery writes no redelivery line: only a delivery count above 1 means an earlier delivery was not settled.
  /// </summary>
  [Test]
  public async Task FirstDelivery_WritesNoRedeliveryLineAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 1);

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(message, new RecordingTransportSessionReceiver()));

    await Assert.That(logger.Count(LogLevel.Information, REDELIVERED)).IsEqualTo(0);
  }

  /// <summary>
  /// A redelivery that is then handled still writes its one Information line, so a message whose earlier deliveries never
  /// reached this code shows up on the delivery that finally does.
  /// </summary>
  [Test]
  public async Task Redelivery_HandledNormally_WritesOneInformationLineNamingItAsync() {
    var logger = new LevelRecordingLogger();
    var (transport, client) = _createTransport(enableSessions: true, logger);
    await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 7);
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(message, receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
    await _assertRedeliveredOnceAsync(logger, message.MessageId, _testMessageType, 7);
  }

  /// <summary>With Information off the redelivery line is neither written nor formatted.</summary>
  [Test]
  public async Task Redelivery_WithInformationOff_WritesAndFormatsNothingAsync() {
    var logger = new LevelRecordingLogger(LogLevel.Warning);
    var (transport, client) = _createTransport(enableSessions: true, logger);
    await transport.SubscribeBatchAsync((_, _) => Task.CompletedTask, _destination(), new TransportBatchOptions());
    var message = AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope(), deliveryCount: 3);

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(message, new RecordingTransportSessionReceiver()));

    await Assert.That(logger.Count(LogLevel.Information, REDELIVERED)).IsEqualTo(0);
    await Assert.That(logger.CalledBelowMinimum(REDELIVERED)).IsEqualTo(0);
  }

  // ========================================
  // HELPERS
  // ========================================

  private static async Task _assertUnsettledAsync(
      LevelRecordingLogger logger, string messageId, string messageType, int deliveryCount, string reason) {
    var lines = logger.Lines(LogLevel.Warning, UNSETTLED);
    await Assert.That(lines).Count().IsEqualTo(1)
      .Because("every delivery given back to the broker unsettled is named exactly once");
    var line = lines[0];
    await Assert.That(line).Contains(messageId);
    await Assert.That(line).Contains($"of type {messageType} ");
    await Assert.That(line).Contains(ENTITY);
    await Assert.That(line).Contains($"at delivery {deliveryCount}");
    await Assert.That(line).Contains(reason);
  }

  private static async Task _assertRedeliveredOnceAsync(
      LevelRecordingLogger logger, string messageId, string messageType, int deliveryCount) {
    var lines = logger.Lines(LogLevel.Information, REDELIVERED);
    await Assert.That(lines).Count().IsEqualTo(1)
      .Because("a delivery past the first is reported once, whatever happens to it next");
    var line = lines[0];
    await Assert.That(line).Contains(messageId);
    await Assert.That(line).Contains($"of type {messageType} ");
    await Assert.That(line).Contains(ENTITY);
    await Assert.That(line).Contains($"at delivery {deliveryCount}");
  }

  private static async Task _assertHandedAsync(LevelRecordingLogger logger, string messageId, int deliveryCount) {
    var lines = logger.Lines(LogLevel.Debug, HANDED);
    await Assert.That(lines).Count().IsEqualTo(1)
      .Because("each delivery handed to the handler is logged once");
    await Assert.That(lines[0]).Contains(messageId);
    await Assert.That(lines[0]).Contains($"delivery {deliveryCount}");
    await Assert.That(lines[0]).Contains(ENTITY);
  }

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport(
      bool enableSessions, LevelRecordingLogger logger, int maxDeliveryAttempts = 10) {
    var client = new RaisableServiceBusClient();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions,
      MaxDeliveryAttempts = maxDeliveryAttempts,
    };
    var transport = new AzureServiceBusTransport(
      client, AsbTransportTestData.CombinedOptions, options, logger, typeBinder: new MissTypeBinder());
    return (transport, client);
  }

  /// <summary>BatchSize=1 flushes on the first enqueue; the timers are pushed out so only the size trigger fires.</summary>
  private static TransportBatchOptions _sizeOnlyBatchOptions() => new() {
    BatchSize = 1,
    SlideMs = 600_000,
    MaxWaitMs = 600_000,
  };

  private static TransportDestination _destination() => new("diag-topic", "diag-sub");

  /// <summary>
  /// Records formatted lines at or above a minimum level, as a real logger does, and records the message template of any
  /// call made below it without formatting it: a call below the minimum means the caller skipped its own level check.
  /// </summary>
  private sealed class LevelRecordingLogger(LogLevel minimum = LogLevel.Trace) : ILogger<AzureServiceBusTransport> {
    private readonly Lock _sync = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly List<string> _templatesBelowMinimum = [];

    public List<string> Lines(LogLevel level, string fragment) {
      lock (_sync) {
        return [.. _entries.Where(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal))
          .Select(e => e.Message)];
      }
    }

    public int Count(LogLevel level, string fragment) => Lines(level, fragment).Count;

    public int CalledBelowMinimum(string fragment) {
      lock (_sync) {
        return _templatesBelowMinimum.Count(t => t.Contains(fragment, StringComparison.Ordinal));
      }
    }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel < minimum) {
        var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
          ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value as string ?? string.Empty
          : string.Empty;
        lock (_sync) {
          _templatesBelowMinimum.Add(template);
        }
        return;
      }
      var message = formatter(state, exception);
      lock (_sync) {
        _entries.Add((logLevel, message));
      }
    }
  }
}
