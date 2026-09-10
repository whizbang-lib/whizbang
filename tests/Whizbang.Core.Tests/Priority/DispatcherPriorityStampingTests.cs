using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// The dispatcher declares a priority on every message it sends to the outbox (priority step 1): the
/// registered producer hooks run at dispatch with the dispatch context, the schedule and the ambient parent,
/// and the declared number lands on the envelope (so it travels) and on the outbox row (so the store keeps
/// it). A host without the chain registered sends undeclared envelopes exactly as before.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#declaration</docs>
[Category("Unit")]
public class DispatcherPriorityStampingTests {

  public record StampCommand(string Data);

  private sealed class RecordingStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutbox { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutbox.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = envelope.DispatchContext,
        Priority = envelope.Priority,
      };
      var messageType = typeof(TMessage).AssemblyQualifiedName ?? typeof(TMessage).FullName ?? typeof(TMessage).Name;
      return new SerializedEnvelope(jsonEnvelope, $"Whizbang.Core.Observability.MessageEnvelope`1[[{messageType}]], Whizbang.Core", messageType);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => new();
  }

  private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestScope(provider);
    private sealed class TestScope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  /// <summary>No local receptors at all, so every send routes to the outbox strategy.</summary>
  private sealed class OutboxOnlyDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: null), envelopeSerializer: new StubEnvelopeSerializer()) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  private sealed class _sevenForEverything : IPriorityProducerHook {
    public int Order => 100;   // before the framework default, which keeps an explicit declaration
    public int DeclarePriority(PriorityDeclarationContext context) => 7;
  }

  private static (OutboxOnlyDispatcher Dispatcher, RecordingStrategy Strategy) _dispatcher(bool withChain = true, IPriorityProducerHook? hostHook = null) {
    var strategy = new RecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    if (withChain) {
      services.AddWhizbangPriority();
    }
    if (hostHook is not null) {
      services.AddSingleton(hostHook);
    }
    var sp = services.BuildServiceProvider();
    return (new OutboxOnlyDispatcher(sp), strategy);
  }

  [Test]
  public async Task Send_OutsideAnyHandling_DeclaresInteractive_OnTheEnvelopeAndTheRowAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new StampCommand("a"), MessageContext.New());

    var row = strategy.QueuedOutbox.Single();
    await Assert.That(row.Envelope.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("an application-initiated send has a caller waiting; the number travels on the envelope");
    await Assert.That(row.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the outbox row carries the same number so the store keeps it without reading the envelope");
  }

  [Test]
  public async Task Send_WhileHandlingBackgroundWork_InheritsBackgroundAsync() {
    var (dispatcher, strategy) = _dispatcher();

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await dispatcher.SendAsync(new StampCommand("child"), MessageContext.New());
    }

    await Assert.That(strategy.QueuedOutbox.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a message produced while handling background work is background, whatever its type normally is");
  }

  [Test]
  public async Task Send_WithAHostProducerHook_UsesItsDeclarationAsync() {
    var (dispatcher, strategy) = _dispatcher(hostHook: new _sevenForEverything());

    await dispatcher.SendAsync(new StampCommand("x"), MessageContext.New());

    await Assert.That(strategy.QueuedOutbox.Single().Envelope.Priority).IsEqualTo(7)
      .Because("a host's hook runs in the chain the dispatcher consults; the framework default keeps its explicit declaration");
  }

  [Test]
  public async Task Send_WithNoChainRegistered_LeavesTheEnvelopeUndeclaredAsync() {
    var (dispatcher, strategy) = _dispatcher(withChain: false);

    await dispatcher.SendAsync(new StampCommand("legacy"), MessageContext.New());

    await Assert.That(strategy.QueuedOutbox.Single().Envelope.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("a host that never registered the chain sends exactly what it sent before; the receive side reads undeclared as standard");
  }

  [Test]
  public async Task Send_WithAPriorityOnTheOptions_KeepsItOverTheContextRulesAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new StampCommand("bulk"), new DispatchOptions().WithPriority(WorkPriority.BACKGROUND));

    var row = strategy.QueuedOutbox.Single();
    await Assert.That(row.Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("an explicit number on the options is the caller's declaration; the context rules only fill a blank");
    await Assert.That(row.Priority).IsEqualTo(WorkPriority.BACKGROUND);
  }
}
