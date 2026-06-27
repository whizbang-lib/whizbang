#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Linq;
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
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks producer-side local fan-out: publishing a composite in the service's OWN domain expands it into
/// child inbox rows and stores them on the local inbox (so the publishing service persists the inner
/// events — the composite itself is never event-stored and is echo-suppressed on self-receive). A
/// composite in a non-owned domain is left to the outbox/receive-side path only.
/// </summary>
[Category("Dispatcher")]
public class DispatcherOwnedCompositeFanoutTests {

  private sealed record _innerEvt(string Id) : IEvent;

  private sealed class _ownedComposite : CompositeEventBase;

  private sealed class _fakeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  private sealed class _scopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new _scope(provider);
    private sealed class _scope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  private sealed class _fanoutDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  private static (_fanoutDispatcher dispatcher, NoOpWorkCoordinator coordinator) _build(bool ownTestNamespace) {
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new _scopeFactory(sp));
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new _fakeSerializer());
    services.Configure<RoutingOptions>(o => {
      if (ownTestNamespace) {
        o.OwnDomains(typeof(_ownedComposite).Namespace!);
      }
    });
    var sp = services.BuildServiceProvider();
    return (new _fanoutDispatcher(sp), coordinator);
  }

  [Test]
  public async Task OwnedComposite_FansOutLocally_StoresChildInboxRowsAsync() {
    var (dispatcher, coordinator) = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b"), new _innerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1)
      .Because("an owned composite fans out locally into one StoreInboxMessages call.");
    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(3)
      .Because("each inner event becomes a child inbox row on the local inbox.");
  }

  [Test]
  public async Task OwnedComposite_ChildrenCarryCompositeLineageAsync() {
    var (dispatcher, coordinator) = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    foreach (var child in coordinator.StoredMessages) {
      await Assert.That(child.Metadata!.Hops[0].CausationType).IsEqualTo(nameof(_ownedComposite))
        .Because("each locally-fanned-out child traces back to the composite via its creation hop.");
    }
  }

  [Test]
  public async Task NonOwnedComposite_DoesNotFanOutLocallyAsync() {
    var (dispatcher, coordinator) = _build(ownTestNamespace: false);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(0)
      .Because("a composite outside the service's owned domains is left to the outbox / receive-side path.");
  }
}
