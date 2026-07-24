using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// RED-first locks on Slice 3 of the message-discard-policy plan — RabbitMQ parity
/// with the ASB transport. When a received envelope's payload type has no local
/// consumer, the transport asks the discard policy and acks-without-processing
/// (mirroring the ASB <c>AckAndDrop</c> path), emitting telemetry through the
/// shared <see cref="IMessageDiscardPolicy"/>. When no policy is wired, behaviour
/// is unchanged (every envelope is handed to the handler).
/// </summary>
public class RabbitMQReceiveSkipTelemetryTests {

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

  private sealed class TestLogger<T>(RecordingLogger inner) : ILogger<T> {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      => inner.Log(logLevel, eventId, state, exception, formatter);
  }

  [Test]
  public async Task ShouldSkipReceive_NoLocalConsumer_RecordsPolicySkip_AndReturnsTrueAsync() {
    var registry = new TestRegistry();  // empty — nothing consumed
    var policyLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.RabbitMQReceiveSkipTelemetryTests.A");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(policyLogger), meter);
    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => { skippedCount += v; });
    listener.Start();

    var shouldSkip = RabbitMQTransport.ShouldSkipReceive(
      discardPolicy: policy,
      envelopeTypeName: "Test.Contracts.Foo",
      queueName: "q.a",
      messageId: "mid-1");

    await Assert.That(shouldSkip).IsTrue();
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(1);
    await Assert.That(policyLogger.Entries[0].Level).IsEqualTo(LogLevel.Debug);
    await Assert.That(skippedCount).IsEqualTo(1L);
  }

  [Test]
  public async Task ShouldSkipReceive_LocalConsumerExists_ReturnsFalse_NoTelemetryAsync() {
    var registry = new TestRegistry { Consumed = { "Test.Contracts.Foo" } };
    var policyLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.RabbitMQReceiveSkipTelemetryTests.B");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(policyLogger), meter);
    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => { skippedCount += v; });
    listener.Start();

    var shouldSkip = RabbitMQTransport.ShouldSkipReceive(
      discardPolicy: policy,
      envelopeTypeName: "Test.Contracts.Foo",
      queueName: "q.a",
      messageId: "mid-2");

    await Assert.That(shouldSkip).IsFalse();
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(0);
    await Assert.That(skippedCount).IsEqualTo(0L);
  }

  [Test]
  public async Task ShouldSkipReceive_NoPolicyWired_ReturnsFalse_PreservesLegacyBehaviorAsync() {
    var shouldSkip = RabbitMQTransport.ShouldSkipReceive(
      discardPolicy: null,
      envelopeTypeName: "Test.Contracts.Foo",
      queueName: "q.a",
      messageId: "mid-3");

    // No policy injected → never skip; existing handler path runs unchanged.
    await Assert.That(shouldSkip).IsFalse();
  }

  [Test]
  public async Task ShouldSkipReceive_NoEnvelopeTypeName_ReturnsFalseAsync() {
    var registry = new TestRegistry();
    var meter = new Meter("Whizbang.Tests.RabbitMQReceiveSkipTelemetryTests.D");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(new RecordingLogger()), meter);

    // Unknown envelope type → don't pre-filter; existing deserialization-failure
    // path is responsible for that branch.
    var shouldSkip = RabbitMQTransport.ShouldSkipReceive(
      discardPolicy: policy,
      envelopeTypeName: null,
      queueName: "q.a",
      messageId: "mid-4");

    await Assert.That(shouldSkip).IsFalse();
  }
}
