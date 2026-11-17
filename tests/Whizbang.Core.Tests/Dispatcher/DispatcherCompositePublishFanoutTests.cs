// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
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

  private sealed record InnerEvt(string Id) : IEvent;

  private sealed class OwnedComposite : CompositeEventBase;

  private sealed class AtomicComposite : CompositeEventBase {
    public AtomicComposite() => Atomicity = FanoutAtomicity.Atomic;
  }

  private sealed class IdentityComposite : CompositeEventBase, IIdentityPreservingComposite {
    public List<Guid> InnerEventIds { get; init; } = [];
    IReadOnlyList<Guid> IIdentityPreservingComposite.InnerEventIds => InnerEventIds;
  }

  private sealed record LocalCascade(IMessage Message, IMessageEnvelope? Source, Guid? EventId);

  // Captures the outbox rows the publish writes, so a test can read the composite exactly as it goes on the wire.
  private sealed class CapturingStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> Rows { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => Rows.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class FakeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonElement.Parse("{}"),
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  private sealed class ScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new Scope(provider);
    private sealed class Scope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  // Captures the local-only event-store seam: every inner event of an owned composite is local-published via
  // CascadeMessageAsync(Local), whose EventStore half lands here. Overriding it avoids needing a real event
  // store and lets us assert WHICH messages were fanned out locally and that they were sourced from the
  // composite (lineage). Inner events must NEVER reach the cascade-outbox seam (no rebroadcast).
  private sealed class FanoutDispatcher(IServiceProvider sp) : Core.Dispatcher(sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build())) {
    public List<LocalCascade> LocalEventStores { get; } = [];
    public IServiceProvider Services { get; } = sp;
    public int InnerOutboxCascadeCount { get; private set; }
    public string? ThrowOnInnerId { get; set; }

    protected override Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
      if (ThrowOnInnerId is not null && message is InnerEvt e && e.Id == ThrowOnInnerId) {
        throw new InvalidOperationException($"injected failure for inner '{e.Id}'");
      }
      LocalEventStores.Add(new LocalCascade(message, sourceEnvelope, eventId));
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

  private static FanoutDispatcher _build(bool ownTestNamespace, CapturingStrategy? strategy = null) {
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceScopeFactory>(sp => new ScopeFactory(sp));
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new FakeSerializer());
    if (strategy is not null) {
      services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    }
    services.Configure<RoutingOptions>(o => {
      if (ownTestNamespace) {
        o.OwnDomains(typeof(OwnedComposite).Namespace!);
      }
    });
    var sp = services.BuildServiceProvider();
    return new FanoutDispatcher(sp);
  }

  [Test]
  public async Task OwnedComposite_LocalPublishesEachInnerEventAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b"), new InnerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<InnerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "b", "c"])
      .Because("an owned composite local-publishes each inner event at publish (local dispatch + event store).");
    await Assert.That(dispatcher.InnerOutboxCascadeCount).IsEqualTo(0)
      .Because("inner events never rebroadcast — only the composite itself goes over transport.");
  }

  [Test]
  public async Task OwnedComposite_FansOutAtPublish_ViaDispatchOptionsOverloadAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
    };

    await dispatcher.PublishAsync(composite, new DispatchOptions());

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<InnerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "b"])
      .Because("the DispatchOptions overload fans out an owned composite at publish, same as the simple overload.");
  }

  [Test]
  public async Task OwnedComposite_InnerEventsSourcedFromCompositeForLineageAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    var innerCascades = dispatcher.LocalEventStores.Where(c => c.Message is InnerEvt).ToList();
    await Assert.That(innerCascades.Count).IsEqualTo(2);
    foreach (var cascade in innerCascades) {
      await Assert.That(cascade.Source?.Payload is OwnedComposite).IsTrue()
        .Because("each inner event is local-published sourced from the composite, so its stored hop traces back to it.");
    }
  }

  [Test]
  public async Task OwnedComposite_OverCap_ThrowsAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      MaxInnerEventsAllowed = 1,
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
    };

    await Assert.That(async () => await dispatcher.PublishAsync(composite))
      .ThrowsExactly<InvalidOperationException>()
      .Because("a composite over MaxInnerEventsAllowed fails fast at publish so a runaway producer surfaces synchronously.");
    await Assert.That(dispatcher.LocalEventStores.Any(c => c.Message is InnerEvt)).IsFalse()
      .Because("the cap is checked before any child is fanned out.");
  }

  [Test]
  public async Task OwnedComposite_IndependentAtomicity_SkipsFailedChild_ContinuesAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    dispatcher.ThrowOnInnerId = "b";  // default Atomicity is Independent
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b"), new InnerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<InnerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "c"])
      .Because("Independent atomicity drops the failed child ('b') and fans out the rest.");
  }

  [Test]
  public async Task OwnedComposite_AtomicAtomicity_PropagatesChildFailureAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    dispatcher.ThrowOnInnerId = "b";
    var composite = new AtomicComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b"), new InnerEvt("c")],
    };

    await Assert.That(async () => await dispatcher.PublishAsync(composite))
      .ThrowsExactly<InvalidOperationException>()
      .Because("Atomic atomicity propagates a child failure so the whole publish fails.");
  }

  [Test]
  public async Task OwnedComposite_SkipsNullInnerAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), null!, new InnerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var fannedOut = dispatcher.LocalEventStores.Select(c => c.Message).OfType<InnerEvt>().Select(e => e.Id).ToList();
    await Assert.That(fannedOut).IsEquivalentTo(["a", "c"])
      .Because("a null inner event is skipped without failing the fan-out.");
  }

  [Test]
  public async Task NonOwnedComposite_DoesNotFanOutAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: false);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    await Assert.That(dispatcher.LocalEventStores.Any(c => c.Message is InnerEvt)).IsFalse()
      .Because("a composite outside the service's owned domains fans out only at the receive-side (other services).");
  }

  // #2235: the producer stores each constituent of its own composite locally, and every receiver stores it from
  // the copy on the wire. Both must store it under ONE id, or digests diverge and cross-service correlation by id
  // sees two events. The receiver's ids come from the real receive-side expansion of the wire row.
  [Test]
  public async Task OwnedComposite_StoredConstituentIds_AreTheIdsEveryReceiverStoresAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _build(ownTestNamespace: true, strategy);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b"), new InnerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var wireRow = strategy.Rows.Single(r => r.MessageType.Contains(nameof(OwnedComposite), StringComparison.Ordinal));
    var received = CompositeInboxFanout.TryExpand(composite, wireRow.Envelope, dispatcher.Services);
    await Assert.That(received.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var producerIds = dispatcher.LocalEventStores.Where(c => c.Message is InnerEvt).Select(c => c.EventId).ToList();
    var receiverIds = received.Children.Select(c => (Guid?)c.MessageId).ToList();
    await Assert.That(producerIds).IsEquivalentTo(receiverIds, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a constituent carries one id from mint to every store: the producer's stored id is the id each receiver stores.");
  }

  // #2266: a receiver that expands the bundle form of the composite reads each child's type off the wire. A publisher on
  // another build of the contract assembly writes another version there; the stored ids must still agree.
  [Test]
  public async Task OwnedComposite_StoredConstituentIds_AreTheIdsARawExpansionFromAnotherBuildDerivesAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _build(ownTestNamespace: true, strategy);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
    };

    await dispatcher.PublishAsync(composite);

    var wireRow = strategy.Rows.Single(r => r.MessageType.Contains(nameof(OwnedComposite), StringComparison.Ordinal));
    var innerType = typeof(InnerEvt);
    var otherBuild = $"{innerType.FullName}, {innerType.Assembly.GetName().Name}, Version=9.8.7.6, Culture=neutral, PublicKeyToken=null";
    var bundle = new RawBundle((otherBuild, "{\"Id\":\"a\"}"), (otherBuild, "{\"Id\":\"b\"}"));
    var received = CompositeInboxFanout.TryExpand(bundle, wireRow.Envelope, dispatcher.Services);
    var producerIds = dispatcher.LocalEventStores.Where(c => c.Message is InnerEvt).Select(c => c.EventId).ToList();
    var receiverIds = received.Children.Select(c => (Guid?)c.MessageId).ToList();
    await Assert.That(received.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(producerIds).IsEquivalentTo(receiverIds, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the publisher's local fan-out and a raw expansion render the child's type the one canonical way");
  }

  private sealed class RawBundle(params (string Type, string Json)[] inner) : IRawInnerComposite {
    public IEnumerable<IMessage> InnerEvents => [];
    public IReadOnlyList<JsonElement> InnerPayloads { get; } = [.. inner.Select(i => JsonSerializer.Deserialize<JsonElement>(i.Json))];
    public IReadOnlyList<string> InnerTypeNames { get; } = [.. inner.Select(i => i.Type)];
  }

  [Test]
  public async Task OwnedComposite_NullInner_KeepsTheProducersOrdinalsAlignedWithTheReceiversAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _build(ownTestNamespace: true, strategy);
    var composite = new OwnedComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), null!, new InnerEvt("c")],
    };

    await dispatcher.PublishAsync(composite);

    var wireRow = strategy.Rows.Single(r => r.MessageType.Contains(nameof(OwnedComposite), StringComparison.Ordinal));
    var received = CompositeInboxFanout.TryExpand(composite, wireRow.Envelope, dispatcher.Services);
    var producerIds = dispatcher.LocalEventStores.Where(c => c.Message is InnerEvt).Select(c => c.EventId).ToList();
    var receiverIds = received.Children.Select(c => (Guid?)c.MessageId).ToList();
    await Assert.That(producerIds).IsEquivalentTo(receiverIds, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a skipped null still holds its position, so the children after it derive the receiver's ordinals.");
  }

  [Test]
  public async Task OwnedIdentityPreservingComposite_StoresEachConstituentUnderItsCarriedIdAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    Guid first = TrackedGuid.New();
    Guid second = TrackedGuid.New();
    var composite = new IdentityComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
      InnerEventIds = [first, second],
    };

    await dispatcher.PublishAsync(composite);

    var producerIds = dispatcher.LocalEventStores.Where(c => c.Message is InnerEvt).Select(c => c.EventId).ToList();
    await Assert.That(producerIds).IsEquivalentTo(new List<Guid?> { first, second }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a composite that carries its children's ids is stored under them here, as every receiver stores it.");
  }

  [Test]
  public async Task OwnedIdentityPreservingComposite_IdCountMismatch_ThrowsAtPublishAsync() {
    var dispatcher = _build(ownTestNamespace: true);
    var composite = new IdentityComposite {
      StreamId = (Guid)TrackedGuid.New(),
      Inner = [new InnerEvt("a"), new InnerEvt("b")],
      InnerEventIds = [TrackedGuid.New()],
    };

    await Assert.That(async () => await dispatcher.PublishAsync(composite))
      .ThrowsExactly<InvalidOperationException>()
      .Because("every receiver refuses a bundle whose ids do not pair with its children, so the producer fails fast too.");
    await Assert.That(dispatcher.LocalEventStores.Any(c => c.Message is InnerEvt)).IsFalse()
      .Because("the pairing is checked before any child is stored.");
  }
}
