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
/// Locks publish-time composite fan-out (step 1.1 of the composite lifecycle): publishing a composite in
/// the service's OWN domain expands it and <b>local-publishes each inner event</b> through the normal
/// local-only flow (<see cref="DispatchModes.Local"/> = local receptor dispatch + event store, no
/// transport), so the publishing service materializes its own composite's children immediately. The
/// composite itself still goes over transport (step 1.2) for other subscribers, and inner events never
/// rebroadcast. A composite outside the service's owned domains is left to the receive-side fan-out only.
/// </summary>
[Category("Dispatcher")]
public class DispatcherCompositePublishFanoutTests {

  private sealed record _innerEvt(string Id) : IEvent;

  private sealed class _ownedComposite : CompositeEventBase;

  private sealed class _atomicComposite : CompositeEventBase {
    public _atomicComposite() => Atomicity = FanoutAtomicity.Atomic;
  }

  private sealed record _localCascade(IMessage Message, IMessageEnvelope? Source);

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

  // Captures the local-only event-store seam: every inner event of an owned composite is local-published via
  // CascadeMessageAsync(Local), whose EventStore half lands here. Overriding it avoids needing a real event
  // store and lets us assert WHICH messages were fanned out locally and that they were sourced from the
  // composite (lineage). Inner events must NEVER reach the cascade-outbox seam (no rebroadcast).
  private sealed class _fanoutDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: null)) {
    public List<_localCascade> LocalEventStores { get; } = [];
    public int InnerOutboxCascadeCount { get; private set; }
    public string? ThrowOnInnerId { get; set; }

    protected override Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      if (ThrowOnInnerId is not null && message is _innerEvt e && e.Id == ThrowOnInnerId) {
        throw new InvalidOperationException($"injected failure for inner '{e.Id}'");
      }
      LocalEventStores.Add(new _localCascade(message, sourceEnvelope));
      return Task.CompletedTask;
    }
    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      InnerOutboxCascadeCount++;
      return Task.CompletedTask;
    }

    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  private static _fanoutDispatcher _build(bool ownTestNamespace) {
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
    return new _fanoutDispatcher(sp);
  }

  [Test]
  public async Task OwnedComposite_LocalPublishesEachInnerEventAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b"), new _innerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<_innerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "b", "c"])
      .Because("an owned composite local-publishes each inner event at publish (local dispatch + event store).");
    await Assert.That(dispatcher.InnerOutboxCascadeCount).IsEqualTo(0)
      .Because("inner events never rebroadcast — only the composite itself goes over transport.");
  }

  [Test]
  public async Task OwnedComposite_FansOutAtPublish_ViaDispatchOptionsOverloadAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b")],
    };

    await dispatcher.PublishAsync(composite, new DispatchOptions());

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<_innerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "b"])
      .Because("the DispatchOptions overload fans out an owned composite at publish, same as the simple overload.");
  }

  [Test]
  public async Task OwnedComposite_InnerEventsSourcedFromCompositeForLineageAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    var innerCascades = dispatcher.LocalEventStores.Where(c => c.Message is _innerEvt).ToList();
    await Assert.That(innerCascades.Count).IsEqualTo(2);
    foreach (var cascade in innerCascades) {
      await Assert.That(cascade.Source?.Payload is _ownedComposite).IsTrue()
        .Because("each inner event is local-published sourced from the composite, so its stored hop traces back to it.");
    }
  }

  [Test]
  public async Task OwnedComposite_OverCap_ThrowsAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      MaxInnerEventsAllowed = 1,
      Inner = [new _innerEvt("a"), new _innerEvt("b")],
    };

    await Assert.That(async () => await dispatcher.PublishAsync(composite))
      .ThrowsExactly<InvalidOperationException>()
      .Because("a composite over MaxInnerEventsAllowed fails fast at publish so a runaway producer surfaces synchronously.");
    await Assert.That(dispatcher.LocalEventStores.Any(c => c.Message is _innerEvt)).IsFalse()
      .Because("the cap is checked before any child is fanned out.");
  }

  [Test]
  public async Task OwnedComposite_IndependentAtomicity_SkipsFailedChild_ContinuesAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    dispatcher.ThrowOnInnerId = "b";  // default Atomicity is Independent
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b"), new _innerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<_innerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "c"])
      .Because("Independent atomicity drops the failed child ('b') and fans out the rest.");
  }

  [Test]
  public async Task OwnedComposite_AtomicAtomicity_PropagatesChildFailureAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    dispatcher.ThrowOnInnerId = "b";
    var composite = new _atomicComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b"), new _innerEvt("c")],
    };

    await Assert.That(async () => await dispatcher.PublishAsync(composite))
      .ThrowsExactly<InvalidOperationException>()
      .Because("Atomic atomicity propagates a child failure so the whole publish fails.");
  }

  [Test]
  public async Task OwnedComposite_SkipsNullInnerAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), null!, new _innerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<_innerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "c"])
      .Because("a null inner event is skipped without failing the fan-out.");
  }

  [Test]
  public async Task NonOwnedComposite_DoesNotFanOutAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: false);
    var composite = new _ownedComposite {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Inner = [new _innerEvt("a"), new _innerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    await Assert.That(dispatcher.LocalEventStores.Any(c => c.Message is _innerEvt)).IsFalse()
      .Because("a composite outside the service's owned domains fans out only at the receive-side (other services).");
  }
}
