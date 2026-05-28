using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// RED-first locks on Slice 2 of the message-discard-policy plan: when an ASB
/// receive results in <c>AckAndDrop</c> with reason <c>NoLocalConsumer</c>, the
/// transport must route through <see cref="IMessageDiscardPolicy"/> (Debug log
/// + OTel counter) instead of emitting a top-level <c>LogWarning</c>. Other
/// AckAndDrop reasons (genuine envelope/type failures) keep their existing
/// Warning level.
/// </summary>
public class AsbAckDropTelemetryTests {

  private sealed class TestRegistry : IReceptorRegistryQuery {
    public HashSet<string> Consumed { get; } = [];
    public bool HasReceptors(LifecycleStage stage, string messageType) => Consumed.Contains(messageType);
    public bool HasInboxHandler(string messageType) => Consumed.Contains(messageType);
    public bool HasAnyConsumer(string messageType) => Consumed.Contains(messageType);
  }

  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }
    private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
  }

  [Test]
  public async Task EmitAckDropTelemetry_NoLocalConsumer_RoutesThroughPolicy_NotWarningAsync() {
    var registry = new TestRegistry();
    var policyLogger = new RecordingLogger();
    var transportLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.AsbAckDropTelemetryTests.A");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(policyLogger), meter);

    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => { skippedCount += v; });
    listener.Start();

    var decision = new AsbReceiveDecision {
      Action = AsbReceiveAction.AckAndDrop,
      Reason = AsbReceiveReason.NO_LOCAL_CONSUMER,
      Description = "no consumer",
      EnvelopeTypeName = "Test.Contracts.Foo",
    };

    AzureServiceBusTransport.EmitAckDropTelemetry(
      transportLogger: new TestLogger<AzureServiceBusTransport>(transportLogger),
      discardPolicy: policy,
      decision: decision,
      messageId: "mid-1",
      sessionId: "sid-1",
      topic: "topic.a",
      subscription: "sub-1");

    // Policy logs at Debug; transport logger emits nothing for NoLocalConsumer.
    await Assert.That(transportLogger.Entries.Count).IsEqualTo(0);
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(1);
    await Assert.That(policyLogger.Entries[0].Level).IsEqualTo(LogLevel.Debug);
    await Assert.That(skippedCount).IsEqualTo(1L);
  }

  [Test]
  public async Task EmitAckDropTelemetry_OtherReasons_StillLogWarning_NoCounterIncrementAsync() {
    var registry = new TestRegistry();
    var policyLogger = new RecordingLogger();
    var transportLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.AsbAckDropTelemetryTests.B");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(policyLogger), meter);

    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => { skippedCount += v; });
    listener.Start();

    var decision = new AsbReceiveDecision {
      Action = AsbReceiveAction.AckAndDrop,
      Reason = AsbReceiveReason.MISSING_JSON_TYPE_INFO,
      Description = "type info missing",
      EnvelopeTypeName = "Test.Contracts.Foo",
    };

    AzureServiceBusTransport.EmitAckDropTelemetry(
      transportLogger: new TestLogger<AzureServiceBusTransport>(transportLogger),
      discardPolicy: policy,
      decision: decision,
      messageId: "mid-2",
      sessionId: "sid-2",
      topic: "topic.a",
      subscription: "sub-1");

    await Assert.That(transportLogger.Entries.Count).IsEqualTo(1);
    await Assert.That(transportLogger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(0);
    await Assert.That(skippedCount).IsEqualTo(0L);
  }

  [Test]
  public async Task EmitAckDropTelemetry_NoLocalConsumer_WithoutPolicy_FallsBackToWarningAsync() {
    var transportLogger = new RecordingLogger();

    var decision = new AsbReceiveDecision {
      Action = AsbReceiveAction.AckAndDrop,
      Reason = AsbReceiveReason.NO_LOCAL_CONSUMER,
      Description = "no consumer",
      EnvelopeTypeName = "Test.Contracts.Foo",
    };

    AzureServiceBusTransport.EmitAckDropTelemetry(
      transportLogger: new TestLogger<AzureServiceBusTransport>(transportLogger),
      discardPolicy: null,
      decision: decision,
      messageId: "mid-3",
      sessionId: "sid-3",
      topic: "topic.a",
      subscription: "sub-1");

    // No policy injected — keep the legacy Warning so the behaviour is not silently lost.
    await Assert.That(transportLogger.Entries.Count).IsEqualTo(1);
    await Assert.That(transportLogger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
  }

  private sealed class TestLogger<T>(RecordingLogger inner) : ILogger<T> {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => inner.Log(logLevel, eventId, state, exception, formatter);
  }
}
