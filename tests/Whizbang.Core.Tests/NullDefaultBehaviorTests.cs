using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Signals;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests;

/// <summary>
/// The behavior of the placeholders a host gets when it registers nothing for a replaceable service.
/// <para>
/// Each null default stands in for "no implementation" without being null: a consumer that checks
/// the availability flag takes the path it took on a missing service, and a consumer that does not
/// check gets a failure that names the missing piece instead of a NullReferenceException. These
/// tests pin both halves for every default, so a default can never quietly start answering as if
/// it were a real implementation.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/NullDefaultServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IReceptorRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/NullDeadLetterStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/IPerspectiveStreamLocker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/ScopedEventTrackerAccessor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/IMessagePublishStrategy.cs</code-under-test>
/// <docs>extending/extensibility/replaceable-services</docs>
public class NullDefaultBehaviorTests {

  private sealed record ProbeMessage : IMessage;

  private readonly record struct ProbeSignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class RecordingSignalBus : ISignalBus {
    public List<object> Published { get; } = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      Published.Add(signal);
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal =>
      NullSignalBus.Instance.Subscribe(handler);
  }

  private sealed class RecordingScopedTracker : IScopedEventTracker {
    public List<TrackedEvent> Tracked { get; } = [];
    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) => Tracked.Add(new TrackedEvent(streamId, eventType, eventId));
    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => Tracked;
    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => Tracked;
    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) => Tracked.All(t => processedEventIds.Contains(t.EventId));
  }

  // ---- event types and receptors --------------------------------------------------------------

  [Test]
  public async Task NullEventTypeProvider_ReportsUnavailableAndKnowsNoTypesAsync() {
    var provider = NullEventTypeProvider.Instance;

    await Assert.That(provider.IsAvailable).IsFalse()
      .Because("consumers classify events differently when a provider exists, so the default must not look like one");
    await Assert.That(provider.GetEventTypes()).IsEmpty();
  }

  [Test]
  public async Task NullReceptorRegistry_RegisterThrowsNamingTheMissingRegistryAsync() {
    var registry = NullReceptorRegistry.Instance;

    var plain = await Assert.That(() => registry.Register<RuntimeConsumerProbeMessage>(
        new RuntimeConsumerProbeReceptor(), LifecycleStage.ImmediateDetached))
      .Throws<InvalidOperationException>();
    await Assert.That(plain!.Message).Contains("No receptor registry is registered")
      .Because("a host that reaches a registration with no registry needs to know which assembly to reference");
    var responding = await Assert.That(() => registry.Register<ReceptorRegistryRuntimeRegistrationTests.RuntimeRegistrationTestCommand, ReceptorRegistryRuntimeRegistrationTests.RuntimeRegistrationTestResult>(
        new ReceptorRegistryRuntimeRegistrationTests.RuntimeRegistrationResponseReceptor(), LifecycleStage.ImmediateDetached))
      .Throws<InvalidOperationException>();
    await Assert.That(responding!.Message).Contains("No receptor registry is registered");
  }

  [Test]
  public async Task NullReceptorRegistry_UnregisterReportsNothingRemovedAsync() {
    var registry = NullReceptorRegistry.Instance;

    await Assert.That(registry.Unregister<RuntimeConsumerProbeMessage>(
        new RuntimeConsumerProbeReceptor(), LifecycleStage.ImmediateDetached)).IsFalse()
      .Because("nothing was ever registered, so nothing can be removed");
    await Assert.That(registry.Unregister<ReceptorRegistryRuntimeRegistrationTests.RuntimeRegistrationTestCommand, ReceptorRegistryRuntimeRegistrationTests.RuntimeRegistrationTestResult>(
        new ReceptorRegistryRuntimeRegistrationTests.RuntimeRegistrationResponseReceptor(), LifecycleStage.ImmediateDetached)).IsFalse();
    await Assert.That(registry.GetReceptorsFor(typeof(ProbeMessage), LifecycleStage.ImmediateDetached)).IsEmpty();
  }

  // ---- storage-backed services ----------------------------------------------------------------

  [Test]
  public async Task NullDeadLetterStore_MoveThrowsUntilAStoreIsRegisteredAsync() {
    var store = NullDeadLetterStore.Instance;

    await Assert.That(store.IsConfigured).IsFalse();
    var ex = await Assert.That(() => store.MoveAsync(
        Guid.NewGuid(), "wh_inbox", Guid.NewGuid(), MessageFailureReason.None, errorText: null, Guid.NewGuid(), "gen-1"))
      .Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("Check IsConfigured")
      .Because("a caller that skipped the flag is told what to check, not handed a NullReferenceException");
  }

  [Test]
  public async Task NullPerspectiveSnapshotStore_EveryOperationThrowsUntilAStoreIsRegisteredAsync() {
    var store = NullPerspectiveSnapshotStore.Instance;
    var streamId = Guid.NewGuid();
    using var snapshot = JsonDocument.Parse("{}");

    await Assert.That(store.IsConfigured).IsFalse();
    await Assert.That(() => store.CreateSnapshotAsync(streamId, "p", Guid.NewGuid(), snapshot)).Throws<InvalidOperationException>();
    await Assert.That(() => store.GetLatestSnapshotAsync(streamId, "p")).Throws<InvalidOperationException>();
    await Assert.That(() => store.GetLatestSnapshotBeforeAsync(streamId, "p", Guid.NewGuid())).Throws<InvalidOperationException>();
    await Assert.That(() => store.HasAnySnapshotAsync(streamId, "p")).Throws<InvalidOperationException>();
    await Assert.That(() => store.PruneOldSnapshotsAsync(streamId, "p", keepCount: 1)).Throws<InvalidOperationException>();
    var ex = await Assert.That(() => store.DeleteAllSnapshotsAsync(streamId, "p")).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("No perspective snapshot store is registered");
  }

  [Test]
  public async Task NullPerspectiveStreamLocker_EveryOperationThrowsUntilALockerIsRegisteredAsync() {
    var locker = NullPerspectiveStreamLocker.Instance;
    var streamId = Guid.NewGuid();
    var instanceId = Guid.NewGuid();

    await Assert.That(locker.IsConfigured).IsFalse();
    await Assert.That(() => locker.TryAcquireLockAsync(streamId, "p", instanceId, "rebuild")).Throws<InvalidOperationException>();
    await Assert.That(() => locker.RenewLockAsync(streamId, "p", instanceId)).Throws<InvalidOperationException>();
    var ex = await Assert.That(() => locker.ReleaseLockAsync(streamId, "p", instanceId)).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("No perspective stream locker is registered");
  }

  [Test]
  public async Task NullStartupAssessor_AssessThrowsUntilAnAssessorIsRegisteredAsync() {
    var assessor = NullStartupAssessor.Instance;

    await Assert.That(assessor.IsConfigured).IsFalse();
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await assessor.AssessAsync(CancellationToken.None));
    await Assert.That(ex!.Message).Contains("No startup assessor is registered");
  }

  [Test]
  public async Task NullDutyElector_TryAcquireThrowsUntilAnElectorIsRegisteredAsync() {
    var elector = NullDutyElector.Instance;

    await Assert.That(elector.IsConfigured).IsFalse();
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await elector.TryAcquireAsync("schema", CancellationToken.None));
    await Assert.That(ex!.Message).Contains("No duty elector is registered");
  }

  // ---- signaling, notifications and publishing ------------------------------------------------

  [Test]
  public async Task NullNotifySignalingGate_ReportsPollingWithNoProbeHistoryAsync() {
    var gate = NullNotifySignalingGate.Instance;

    await Assert.That(gate.IsConfigured).IsFalse();
    await Assert.That(gate.IsAvailable).IsFalse();
    await Assert.That(gate.LastVerifiedAt).IsNull();
    await Assert.That(gate.LastFailureAt).IsNull();
    await Assert.That(gate.LastFailureReason).Contains("polled")
      .Because("an operator reading the gate's state learns why signaling is off, not just that it is");
    await Assert.That(await gate.ProbeNowAsync()).IsFalse()
      .Because("there is no driver to probe, so a probe can never report signaling available");
  }

  [Test]
  public async Task NullSignalBus_PublishesNothingAndHandsOutInertSubscriptionsAsync() {
    var bus = NullSignalBus.Instance;

    await Assert.That(bus.IsConfigured).IsFalse();
    await bus.PublishAsync(new ProbeSignal(1));
    var subscription = bus.Subscribe<ProbeSignal>(_ => ValueTask.CompletedTask);

    await Assert.That(subscription).IsNotNull();
    subscription.Dispose();
    subscription.Dispose();
  }

  [Test]
  public async Task NullMessagePublishStrategy_IsNeverReadyAndFailsAPublishNamingTheCauseAsync() {
    var strategy = NullMessagePublishStrategy.Instance;
    var messageId = Guid.CreateVersion7();
    var work = new OutboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Attempts = 0,
    };

    await Assert.That(strategy.IsConfigured).IsFalse();
    await Assert.That(await strategy.IsReadyAsync()).IsFalse()
      .Because("the outbox workers go idle on an unready strategy, which is what a missing transport meant");

    var result = await strategy.PublishAsync(work, CancellationToken.None);

    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Failed);
    await Assert.That(result.Error).Contains("No transport is registered")
      .Because("a publish that reaches the default fails with the cause named, not a NullReferenceException");
  }

  // ---- routing and scoped tracking ------------------------------------------------------------

  [Test]
  public async Task StaticEventNamespaceRegistry_ReadsTheProcessWideRegistryAsync() {
    var registry = new StaticEventNamespaceRegistry();

    await Assert.That(registry.GetPerspectiveEventNamespaces().SetEquals(EventNamespaceRegistry.GetPerspectiveNamespaces())).IsTrue()
      .Because("the service form is the static fallback every consumer used to apply by hand, made replaceable");
    await Assert.That(registry.GetReceptorEventNamespaces().SetEquals(EventNamespaceRegistry.GetReceptorNamespaces())).IsTrue();
    await Assert.That(registry.GetAllEventNamespaces().SetEquals(EventNamespaceRegistry.GetAllNamespaces())).IsTrue();
  }

  [Test]
  public async Task NullScopedEventTracker_TracksNothingAndReportsEverythingProcessedAsync() {
    var tracker = NullScopedEventTracker.Instance;
    var filter = new AllPendingFilter();

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(ProbeMessage), Guid.NewGuid());

    await Assert.That(tracker.IsAvailable).IsFalse();
    await Assert.That(tracker.GetEmittedEvents()).IsEmpty();
    await Assert.That(tracker.GetEmittedEvents(filter)).IsEmpty();
    await Assert.That(tracker.AreAllProcessed(filter, new HashSet<Guid>())).IsTrue()
      .Because("with nothing tracked there is nothing left to wait for, so a sync awaiter returns at once");
  }

  [Test]
  public async Task AmbientScopedEventTracker_OutsideATrackedScope_BehavesLikeTheNullTrackerAsync() {
    var previous = ScopedEventTrackerAccessor.CurrentTracker;
    ScopedEventTrackerAccessor.CurrentTracker = null;
    try {
      var ambient = AmbientScopedEventTracker.Instance;
      var filter = new AllPendingFilter();

      ambient.TrackEmittedEvent(Guid.NewGuid(), typeof(ProbeMessage), Guid.NewGuid());

      await Assert.That(ambient.IsAvailable).IsFalse()
        .Because("a singleton-lifetime caller outside any scope has no events to wait on");
      await Assert.That(ambient.GetEmittedEvents()).IsEmpty();
      await Assert.That(ambient.GetEmittedEvents(filter)).IsEmpty();
      await Assert.That(ambient.AreAllProcessed(filter, new HashSet<Guid>())).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = previous;
    }
  }

  [Test]
  public async Task AmbientScopedEventTracker_InsideATrackedScope_ForwardsToTheScopeTrackerAsync() {
    var previous = ScopedEventTrackerAccessor.CurrentTracker;
    var scopeTracker = new RecordingScopedTracker();
    ScopedEventTrackerAccessor.CurrentTracker = scopeTracker;
    try {
      var ambient = AmbientScopedEventTracker.Instance;
      var streamId = Guid.NewGuid();
      var eventId = Guid.NewGuid();
      var filter = new StreamFilter(streamId);

      ambient.TrackEmittedEvent(streamId, typeof(ProbeMessage), eventId);

      await Assert.That(ambient.IsAvailable).IsTrue();
      await Assert.That(scopeTracker.Tracked).HasSingleItem()
        .Because("the ambient tracker records into the scope's tracker, which is what the awaiter later reads");
      await Assert.That(ambient.GetEmittedEvents()).HasSingleItem();
      await Assert.That(ambient.GetEmittedEvents(filter)).HasSingleItem();
      await Assert.That(ambient.AreAllProcessed(filter, new HashSet<Guid>())).IsFalse()
        .Because("the tracked event has not been processed, so the scope still has work outstanding");
      await Assert.That(ambient.AreAllProcessed(filter, new HashSet<Guid> { eventId })).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = previous;
    }
  }

  // ---- replacing a null default ---------------------------------------------------------------

  [Test]
  public async Task TryAddSingletonOverNullDefault_Instance_ReplacesThePlaceholderAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<ISignalBus>(NullSignalBus.Instance);
    var bus = new RecordingSignalBus();

    var returned = services.TryAddSingletonOverNullDefault<ISignalBus>(bus);

    await Assert.That(returned).IsSameReferenceAs(services)
      .Because("the extension chains like every other registration call");
    await Assert.That(services.Count(d => d.ServiceType == typeof(ISignalBus))).IsEqualTo(1)
      .Because("the placeholder is removed, not shadowed; a later GetServices must not see both");
    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetRequiredService<ISignalBus>()).IsSameReferenceAs(bus);
  }

  [Test]
  public async Task TryAddSingletonOverNullDefault_Instance_LeavesARealRegistrationAloneAsync() {
    var services = new ServiceCollection();
    var first = new RecordingSignalBus();
    services.AddSingleton<ISignalBus>(first);

    services.TryAddSingletonOverNullDefault<ISignalBus>(new RecordingSignalBus());

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetRequiredService<ISignalBus>()).IsSameReferenceAs(first)
      .Because("only a placeholder yields; a host's own registration wins over a subsystem's default");
  }

  [Test]
  public async Task TryAddSingletonOverNullDefault_Instance_RejectsMissingArgumentsAsync() {
    var services = new ServiceCollection();

    await Assert.That(() => ((IServiceCollection)null!).TryAddSingletonOverNullDefault<ISignalBus>(new RecordingSignalBus()))
      .Throws<ArgumentNullException>();
    await Assert.That(() => services.TryAddSingletonOverNullDefault<ISignalBus>((ISignalBus)null!))
      .Throws<ArgumentNullException>()
      .Because("registering a null instance would reintroduce the null this whole mechanism exists to remove");
  }
}
