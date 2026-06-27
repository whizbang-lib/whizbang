#pragma warning disable CA1707

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Locks the Phase D no-rebroadcast guard at the outbox-enqueue boundary inside
/// <c>Dispatcher.PublishToOutboxDynamicAsync</c>. Probe: an unregistered event type makes
/// <c>_serializeToJsonEnvelope</c> throw — so reaching serialization is observable. A
/// NoRebroadcast source returns BEFORE serialization (the guard fired → no throw); an ordinary
/// source proceeds TO serialization (guard did not fire → throws). This exercises both guard
/// branches without needing JSON registration or a real outbox.
/// </summary>
[Category("Dispatcher")]
public class DispatcherNoRebroadcastGuardTests {

  // An event type deliberately NOT registered in any JsonSerializerContext, so reaching
  // serialization throws "No JSON type info found".
  private sealed record _unregisteredEvent(Guid Id) : IEvent;

  private sealed class _stubStrategy : IWorkCoordinatorStrategy {
    public int QueueOutboxCallCount { get; private set; }
    public void QueueOutboxMessage(OutboxMessage message) => QueueOutboxCallCount++;
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class _scopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new _scope(provider);
    private sealed class _scope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  // Minimal concrete Dispatcher exposing the protected dynamic outbox publish.
  private sealed class _guardDispatcher : Core.Dispatcher {
    public _guardDispatcher(IServiceProvider sp)
      : base(sp, new ServiceInstanceProvider(configuration: null)) { }

    public Task PublishDynamicAsync(IMessage evt, IMessageEnvelope? source) =>
      PublishToOutboxDynamicAsync(evt, evt.GetType(), MessageId.New(), source);

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  private static (_guardDispatcher dispatcher, _stubStrategy strategy) _dispatcher() {
    var strategy = new _stubStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new _scopeFactory(sp));
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    var sp = services.BuildServiceProvider();
    return (new _guardDispatcher(sp), strategy);
  }

  private static MessageEnvelope<JsonElement> _source(EventFlags flags) => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Flags = flags,
  };

  [Test]
  public async Task NoRebroadcastSource_IsSuppressedBeforeSerializationAsync() {
    var (dispatcher, strategy) = _dispatcher();
    // Unregistered event would throw at serialization — but the guard returns first, so this completes.
    await dispatcher.PublishDynamicAsync(new _unregisteredEvent(Guid.NewGuid()), _source(EventFlags.NoRebroadcast));

    await Assert.That(strategy.QueueOutboxCallCount).IsEqualTo(0)
      .Because("The guard short-circuited before the outbox write — the child is never re-broadcast.");
  }

  [Test]
  public async Task OrdinarySource_ProceedsToOutboxAsync() {
    var (dispatcher, _) = _dispatcher();
    // No NoRebroadcast flag → the guard does not fire → the publish proceeds to serialization, which
    // throws for the unregistered type. Proves the guard's false branch lets the publish through.
    await Assert.That(async () => await dispatcher.PublishDynamicAsync(new _unregisteredEvent(Guid.NewGuid()), _source(EventFlags.None)))
      .Throws<InvalidOperationException>()
      .Because("Without NoRebroadcast the publish reaches _serializeToJsonEnvelope, which throws for an unregistered type.");
  }
}
