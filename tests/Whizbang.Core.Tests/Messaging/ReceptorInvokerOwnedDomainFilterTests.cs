using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The owned-domain filter that decides whether a receptor fires at the PreOutbox stage.
/// </summary>
/// <remarks>
/// PreOutbox runs as a message is about to leave this service. Once owned domains are configured,
/// the namespace a message lives in says who is responsible for it, and combining that with
/// command-versus-event gives the four cases this filter separates:
///
/// <list type="bullet">
/// <item>An <b>owned event</b> is one this service is publishing — its own receptors should fire.</item>
/// <item>A <b>foreign command</b> is one this service is sending to another — likewise.</item>
/// <item>An <b>owned command</b> reaching PreOutbox is an echo of work this service already did.</item>
/// <item>A <b>foreign event</b> is somebody else's to publish; re-firing on it duplicates it.</item>
/// </list>
///
/// <para>
/// Getting this wrong is a double-fire, not a missed one, which is the harder failure to see: the
/// work happens twice and only shows up as duplicated side effects downstream.
/// </para>
/// </remarks>
[Category("Core")]
[Category("Routing")]
public class ReceptorInvokerOwnedDomainFilterTests {

  private const string OWNED_DOMAIN = "Shop.Orders";

  private sealed class FiringTracker {
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Record() => Interlocked.Increment(ref _count);
  }

  private sealed class StubRegistry(FiringTracker tracker) : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void RegisterReceptor<TMessage>(LifecycleStage stage) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: typeof(TMessage).Name,
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          tracker.Record();
          return ValueTask.FromResult<object?>(null);
        }));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage)
      => _receptors.TryGetValue((messageType, stage), out var list) ? list : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private static (ReceptorInvoker Invoker, FiringTracker Tracker) _invoker<TMessage>(
      LifecycleStage stage, params string[] ownedDomains)
    => _invoker<TMessage>(stage, stageTracker: null, ownedDomains);

  private static (ReceptorInvoker Invoker, FiringTracker Tracker) _invoker<TMessage>(
      LifecycleStage stage, LifecycleStageTracker? stageTracker, params string[] ownedDomains) {
    var tracker = new FiringTracker();
    var registry = new StubRegistry(tracker);
    registry.RegisterReceptor<TMessage>(stage);

    var services = new ServiceCollection();
    if (ownedDomains.Length > 0) {
      services.AddSingleton<IOptions<RoutingOptions>>(
        Options.Create(new RoutingOptions().OwnDomains(ownedDomains)));
    }
    if (stageTracker is not null) {
      services.AddSingleton(stageTracker);
    }
    var sp = services.BuildServiceProvider();
    return (new ReceptorInvoker(registry, sp), tracker);
  }

  private static MessageEnvelope<TPayload> _envelope<TPayload>(TPayload payload) => new() {
    MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
  };

  private static LifecycleExecutionContext _context(LifecycleStage stage) => new() {
    CurrentStage = stage,
    MessageSource = MessageSource.Local,
    AttemptNumber = 0,
  };

  // ============================================================
  // With owned domains configured
  // ============================================================

  [Test]
  public async Task PreOutbox_OwnedEvent_FiresAsync() {
    // This service is publishing its own event — its PreOutbox receptors are the last chance to
    // act before it leaves.
    var (invoker, tracker) = _invoker<Shop.Orders.OrderPlaced>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.OrderPlaced()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PreOutbox_ForeignCommand_FiresAsync() {
    // A command addressed to another service is this service's own outbound work.
    var (invoker, tracker) = _invoker<Billing.Invoices.IssueInvoice>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Billing.Invoices.IssueInvoice()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PreOutbox_OwnedCommand_IsSkippedAsync() {
    // An owned command reaching PreOutbox is an echo of work this service already handled at
    // LocalImmediate. Firing again runs the same side effects twice.
    var (invoker, tracker) = _invoker<Shop.Orders.PlaceOrder>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.PlaceOrder()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(0)
      .Because("the owning service already ran this at LocalImmediate — firing again duplicates it");
  }

  [Test]
  public async Task PreOutbox_ForeignEvent_IsSkippedAsync() {
    // Somebody else's event is theirs to publish. Re-firing on it here would emit a second copy
    // from a service that does not own it.
    var (invoker, tracker) = _invoker<Billing.Invoices.InvoiceIssued>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Billing.Invoices.InvoiceIssued()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PreOutbox_ChildNamespaceOfAnOwnedDomain_CountsAsOwnedAsync() {
    // Ownership is hierarchical: a service that owns Shop.Orders owns everything under it, or
    // every new sub-namespace would silently fall out of its own domain.
    var (invoker, tracker) = _invoker<Shop.Orders.Fulfillment.OrderShipped>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.Fulfillment.OrderShipped()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1)
      .Because("Shop.Orders.Fulfillment is inside Shop.Orders — an owned event there still fires");
  }

  [Test]
  public async Task PreOutbox_NamespaceMerelySharingThePrefix_IsNotOwnedAsync() {
    // The match appends a '.' before comparing, so Shop.OrdersArchive is a different domain —
    // without that, an unrelated service's namespace would be swept into this one's ownership.
    var (invoker, tracker) = _invoker<Shop.OrdersArchive.ArchivedOrderRecorded>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Shop.OrdersArchive.ArchivedOrderRecorded()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(0)
      .Because("Shop.OrdersArchive is not inside Shop.Orders — treating it as owned would fire "
             + "on another service's events");
  }

  [Test]
  public async Task PreOutbox_OwnershipMatchIsCaseInsensitiveAsync() {
    var (invoker, tracker) = _invoker<Shop.Orders.OrderPlaced>(
      LifecycleStage.PreOutboxInline, "shop.orders");

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.OrderPlaced()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PreOutbox_MultipleOwnedDomains_AreAllHonoredAsync() {
    var (invoker, tracker) = _invoker<Billing.Invoices.InvoiceIssued>(
      LifecycleStage.PreOutboxInline, OWNED_DOMAIN, "Billing.Invoices");

    await invoker.InvokeAsync(
      _envelope(new Billing.Invoices.InvoiceIssued()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1)
      .Because("a service can own more than one domain, and an event in either is its own");
  }

  [Test]
  public async Task DetachedPreOutbox_IsFilteredTheSameWayAsync() {
    // Both PreOutbox stages are the same decision point; filtering only the inline one would let
    // the detached receptor double-fire.
    var (invoker, tracker) = _invoker<Shop.Orders.PlaceOrder>(
      LifecycleStage.PreOutboxDetached, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.PlaceOrder()),
      LifecycleStage.PreOutboxDetached,
      _context(LifecycleStage.PreOutboxDetached));

    await Assert.That(tracker.Count).IsEqualTo(0);
  }

  // ============================================================
  // Without owned domains — the filter is inert
  // ============================================================

  [Test]
  public async Task WithoutOwnedDomains_NothingIsFilteredAsync() {
    // Backward compatibility: a service that never configured ownership must behave exactly as
    // it did before the filter existed, or enabling the feature elsewhere changes it here.
    var (invoker, tracker) = _invoker<Shop.Orders.PlaceOrder>(LifecycleStage.PreOutboxInline);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.PlaceOrder()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1);
  }

  [Test]
  public async Task WithoutOwnedDomains_AForeignEventStillFiresAsync() {
    var (invoker, tracker) = _invoker<Billing.Invoices.InvoiceIssued>(LifecycleStage.PreOutboxInline);

    await invoker.InvokeAsync(
      _envelope(new Billing.Invoices.InvoiceIssued()),
      LifecycleStage.PreOutboxInline,
      _context(LifecycleStage.PreOutboxInline));

    await Assert.That(tracker.Count).IsEqualTo(1);
  }

  // ============================================================
  // Other stages are untouched
  // ============================================================

  [Test]
  public async Task LocalImmediate_OwnedCommand_StillFiresAsync() {
    // The filter is scoped to PreOutbox. LocalImmediate is where an owned command is supposed to
    // run — filtering it there would mean the command never executes at all.
    var (invoker, tracker) = _invoker<Shop.Orders.PlaceOrder>(
      LifecycleStage.LocalImmediateInline, OWNED_DOMAIN);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.PlaceOrder()),
      LifecycleStage.LocalImmediateInline,
      _context(LifecycleStage.LocalImmediateInline));

    await Assert.That(tracker.Count).IsEqualTo(1)
      .Because("skipping here would mean the owned command is never handled by anyone");
  }

  // ============================================================
  // Cross-worker stage dedup
  // ============================================================
  //
  // The same message can reach more than one worker — an inbox dispatcher and a drain pass, say —
  // and both would fire the same lifecycle stage for it. The stage tracker is what makes the
  // second one a no-op. Without it, every receptor at that stage runs twice for one message, and
  // the effects a lifecycle receptor produces are usually not idempotent.

  [Test]
  public async Task AStageAlreadyClaimedElsewhere_IsNotFiredAgainAsync() {
    var stageTracker = new LifecycleStageTracker();
    var (invoker, firings) = _invoker<Shop.Orders.OrderPlaced>(
      LifecycleStage.PostInboxInline, stageTracker);
    var envelope = _envelope(new Shop.Orders.OrderPlaced());

    // Another worker got there first.
    var claimed = stageTracker.TryClaim(envelope.MessageId.Value, LifecycleStage.PostInboxInline);

    await invoker.InvokeAsync(
      envelope, LifecycleStage.PostInboxInline, _context(LifecycleStage.PostInboxInline));

    await Assert.That(claimed).IsTrue();
    await Assert.That(firings.Count).IsEqualTo(0)
      .Because("two workers reaching the same message must not run its lifecycle receptors twice");
  }

  [Test]
  public async Task AnUnclaimedStage_FiresAndThenClaimsItAsync() {
    // The first arrival does the work and takes the claim, so a later one is the one that skips.
    var stageTracker = new LifecycleStageTracker();
    var (invoker, firings) = _invoker<Shop.Orders.OrderPlaced>(
      LifecycleStage.PostInboxInline, stageTracker);
    var envelope = _envelope(new Shop.Orders.OrderPlaced());

    await invoker.InvokeAsync(
      envelope, LifecycleStage.PostInboxInline, _context(LifecycleStage.PostInboxInline));

    await Assert.That(firings.Count).IsEqualTo(1);
    await Assert.That(stageTracker.TryClaim(envelope.MessageId.Value, LifecycleStage.PostInboxInline))
      .IsFalse()
      .Because("the invocation takes the claim, which is what makes a later worker skip");
  }

  [Test]
  public async Task ADifferentStageOnTheSameMessage_IsNotBlockedAsync() {
    // Dedup is per (message, stage). Keying on the message alone would let one stage's claim
    // suppress every other stage for that message.
    var stageTracker = new LifecycleStageTracker();
    var (invoker, firings) = _invoker<Shop.Orders.OrderPlaced>(
      LifecycleStage.PostInboxInline, stageTracker);
    var envelope = _envelope(new Shop.Orders.OrderPlaced());

    _ = stageTracker.TryClaim(envelope.MessageId.Value, LifecycleStage.PreOutboxInline);

    await invoker.InvokeAsync(
      envelope, LifecycleStage.PostInboxInline, _context(LifecycleStage.PostInboxInline));

    await Assert.That(firings.Count).IsEqualTo(1);
  }

  [Test]
  public async Task ADifferentMessageAtTheSameStage_IsNotBlockedAsync() {
    var stageTracker = new LifecycleStageTracker();
    var (invoker, firings) = _invoker<Shop.Orders.OrderPlaced>(
      LifecycleStage.PostInboxInline, stageTracker);

    _ = stageTracker.TryClaim(
      (Guid)TrackedGuid.NewMedo(), LifecycleStage.PostInboxInline);

    await invoker.InvokeAsync(
      _envelope(new Shop.Orders.OrderPlaced()),
      LifecycleStage.PostInboxInline, _context(LifecycleStage.PostInboxInline));

    await Assert.That(firings.Count).IsEqualTo(1);
  }

  [Test]
  public async Task WithNoStageTrackerRegistered_EveryInvocationFiresAsync() {
    // A host built without the tracker keeps the pre-dedup behavior rather than silently
    // dropping stages it cannot deduplicate.
    var (invoker, firings) = _invoker<Shop.Orders.OrderPlaced>(LifecycleStage.PostInboxInline);
    var envelope = _envelope(new Shop.Orders.OrderPlaced());

    await invoker.InvokeAsync(
      envelope, LifecycleStage.PostInboxInline, _context(LifecycleStage.PostInboxInline));
    await invoker.InvokeAsync(
      envelope, LifecycleStage.PostInboxInline, _context(LifecycleStage.PostInboxInline));

    await Assert.That(firings.Count).IsEqualTo(2);
  }
}
