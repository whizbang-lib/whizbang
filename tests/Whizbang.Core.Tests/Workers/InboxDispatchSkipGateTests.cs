using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// RED-first locks on Slice 4 of the message-discard-policy plan: the inbox
/// dispatch worker calls <see cref="IMessageDiscardPolicy.EvaluateInbox"/> as an
/// early gate. When the policy says discard (the row references a type with no
/// active consumer), the helper records the skip telemetry and tells the caller
/// to short-circuit the row — avoiding the lease / scope / security-context
/// overhead for messages that wouldn't fire any receptor anyway.
/// </summary>
public class InboxDispatchSkipGateTests {

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
  public async Task ShouldSkipInbox_TypeNoLongerInRegistry_RecordsSkip_AndReturnsTrueAsync() {
    var registry = new TestRegistry();  // empty
    var policyLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.A");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(policyLogger), meter);
    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => { skippedCount += v; });
    listener.Start();

    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: policy,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

    await Assert.That(shouldSkip).IsTrue();
    // RegistryChanged → Information level per policy
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(1);
    await Assert.That(policyLogger.Entries[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(skippedCount).IsEqualTo(1L);
  }

  [Test]
  public async Task ShouldSkipInbox_TypeHasConsumer_ReturnsFalse_NoTelemetryAsync() {
    var registry = new TestRegistry { Consumed = { "Test.Contracts.Foo" } };
    var policyLogger = new RecordingLogger();
    var meter = new Meter("Whizbang.Tests.InboxDispatchSkipGateTests.B");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(policyLogger), meter);
    long skippedCount = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (i, l) => { if (i.Meter == meter && i.Name == MessageDiscardPolicy.COUNTER_NAME) { l.EnableMeasurementEvents(i); } }
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => { skippedCount += v; });
    listener.Start();

    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: policy,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    await Assert.That(shouldSkip).IsFalse();
    await Assert.That(policyLogger.Entries.Count).IsEqualTo(0);
    await Assert.That(skippedCount).IsEqualTo(0L);
  }

  [Test]
  public async Task ShouldSkipInbox_NoPolicyWired_ReturnsFalse_PreservesLegacyBehaviorAsync() {
    var shouldSkip = InboxDispatchWorker.ShouldSkipInbox(
      discardPolicy: null,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("33333333-3333-3333-3333-333333333333"));

    await Assert.That(shouldSkip).IsFalse();
  }
}
