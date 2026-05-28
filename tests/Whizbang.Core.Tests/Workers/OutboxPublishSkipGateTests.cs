using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// RED-first locks on Slice 5 of the message-discard-policy plan: the outbox
/// publish gate. The default policy's <see cref="IMessageDiscardPolicy.EvaluateOutbox"/>
/// returns <c>ShouldDiscard = false</c>, so without explicit catalog evidence a
/// publish never silently disappears. The gate seam exists so a future
/// <c>IEventCatalog</c> implementation can drop publish rows whose event type no
/// service consumes — but that requires opt-in.
/// </summary>
public class OutboxPublishSkipGateTests {

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
  public async Task ShouldSkipOutboxPublish_DefaultPolicy_AlwaysReturnsFalseAsync() {
    var registry = new TestRegistry();  // empty — but EvaluateOutbox is the SAFE-default no-op
    var meter = new Meter("Whizbang.Tests.OutboxPublishSkipGateTests.A");
    var policy = new MessageDiscardPolicy(registry,
      new TestLogger<MessageDiscardPolicy>(new RecordingLogger()), meter);

    var shouldSkip = OutboxPublishWorker.ShouldSkipOutboxPublish(
      discardPolicy: policy,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

    // Slice 5 safe-default: without a populated catalog, never silently drop a publish.
    await Assert.That(shouldSkip).IsFalse();
  }

  [Test]
  public async Task ShouldSkipOutboxPublish_NoPolicyWired_ReturnsFalseAsync() {
    var shouldSkip = OutboxPublishWorker.ShouldSkipOutboxPublish(
      discardPolicy: null,
      messageType: "Test.Contracts.Foo",
      messageId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    await Assert.That(shouldSkip).IsFalse();
  }
}
