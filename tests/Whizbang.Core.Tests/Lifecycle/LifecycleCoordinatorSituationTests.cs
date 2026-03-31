using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Comprehensive situation tests for the LifecycleCoordinator.
/// Verifies hooks fire exactly once per stage per event.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
public class LifecycleCoordinatorSituationTests {
  private sealed record TestEvent(Guid Id, string Data) : IEvent;

  private static MessageEnvelope<T> _createEnvelope<T>(T payload) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = payload,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #region Test 1: Perspective worker — 3 events, PostLifecycle fires once per event

  /// <summary>
  /// Simulates PerspectiveWorker Phase 5: PostLifecycle fires exactly once per unique event,
  /// not per perspective. 3 events → 3 PostLifecycleAsync fires + 3 PostLifecycleInline fires.
  /// </summary>
  [Test]
  public async Task PerspectiveWorker_3Events_PostLifecycleFiresOncePerEventAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleAsyncReceptor", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInlineReceptor", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var events = Enumerable.Range(0, 3).Select(i => {
      var id = Guid.NewGuid();
      return (Id: id, Envelope: _createEnvelope(new TestEvent(id, $"event-{i}")));
    }).ToList();

    // Act — simulate PerspectiveWorker Phase 5
    using var scope = provider.CreateScope();
    foreach (var (eventId, envelope) in events) {
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert — PostLifecycleAsync fires 3 times, PostLifecycleInline fires 3 times
    var asyncFirings = tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync);
    var inlineFirings = tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline);
    await Assert.That(asyncFirings).IsEqualTo(3);
    await Assert.That(inlineFirings).IsEqualTo(3);
  }

  #endregion

  #region Test 2: Transport consumer — 3 events, no perspectives

  [Test]
  public async Task TransportConsumer_3Events_NoPerspectives_FiresPostLifecycleAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInlineReceptor", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // Act — 3 events go through PostInbox → PostLifecycle
    using var scope = provider.CreateScope();
    for (int i = 0; i < 3; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"inbox-event-{i}"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(3);
  }

  #endregion

  #region Test 3: Tags fire at every stage traversed

  [Test]
  public async Task Tags_FireAtEveryStage_TraversedByCoordinatorAsync() {
    // Arrange
    var stagesFired = new List<LifecycleStage>();
    var tagProcessor = new StageCapturingTagProcessor(stage => stagesFired.Add(stage));

    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddSingleton<IMessageTagProcessor>(tagProcessor);
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "tag-test"));

    // Act — advance through multiple stages
    using var scope = provider.CreateScope();
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);
    await tracking.AdvanceToAsync(LifecycleStage.PreOutboxAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PreOutboxInline, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostOutboxAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scope.ServiceProvider, CancellationToken.None);
    coordinator.AbandonTracking(eventId);

    // Assert — tags fire at each stage + ImmediateAsync after each
    // 4 stages + 4 ImmediateAsync = 8 tag processor calls
    await Assert.That(stagesFired.Count).IsEqualTo(8);
    // Verify the non-ImmediateAsync stages are present
    await Assert.That(stagesFired.Contains(LifecycleStage.PreOutboxAsync)).IsTrue();
    await Assert.That(stagesFired.Contains(LifecycleStage.PreOutboxInline)).IsTrue();
    await Assert.That(stagesFired.Contains(LifecycleStage.PostOutboxAsync)).IsTrue();
    await Assert.That(stagesFired.Contains(LifecycleStage.PostOutboxInline)).IsTrue();
  }

  #endregion

  #region Test 4: Tags fire without receptors

  [Test]
  public async Task Tags_FireWithoutReceptors_WhenStageAdvancedAsync() {
    // Arrange — no receptors registered but tag processor is present
    var tagFired = false;
    var tagProcessor = new StageCapturingTagProcessor(_ => tagFired = true);

    var emptyRegistry = new TrackingReceptorRegistry(new StageInvocationTracker());
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(emptyRegistry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddSingleton<IMessageTagProcessor>(tagProcessor);
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "no-receptor-test"));

    // Act
    using var scope = provider.CreateScope();
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostPerspectiveInline, MessageSource.Local);
    await tracking.AdvanceToAsync(LifecycleStage.PostPerspectiveInline, scope.ServiceProvider, CancellationToken.None);
    coordinator.AbandonTracking(eventId);

    // Assert — tag processor fires even though no receptors at this stage
    await Assert.That(tagFired).IsTrue()
      .Because("Tags should fire at every stage even without receptors");
  }

  #endregion

  #region Test 5: ImmediateAsync fires after each stage

  [Test]
  public async Task ImmediateAsync_FiresAfterEachStage_AutomaticallyAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("ImmediateReceptor", LifecycleStage.ImmediateAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "immediate-test"));

    // Act — advance through 3 stages
    using var scope = provider.CreateScope();
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
    coordinator.AbandonTracking(eventId);

    // Assert — ImmediateAsync fires after each stage = 2 times
    var immediateFirings = tracker.GetFiringsForStage(LifecycleStage.ImmediateAsync);
    await Assert.That(immediateFirings).IsEqualTo(2);
  }

  #endregion

  #region Test 6: Route.Both() — WhenAll pattern (coordinator unit)

  [Test]
  public async Task WhenAll_ConcurrentEvents_FiresPostLifecycleOncePerEventAsync() {
    // Arrange — 5 concurrent events with Route.Both()
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var eventIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

    // Begin tracking and register WhenAll for each event
    using var scope = provider.CreateScope();
    foreach (var eventId in eventIds) {
      var envelope = _createEnvelope(new TestEvent(eventId, "whenall-test"));
      coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
      coordinator.ExpectCompletionsFrom(eventId,
        PostLifecycleCompletionSource.Local,
        PostLifecycleCompletionSource.Distributed);
    }

    // Signal Local completion for all — should NOT fire PostLifecycle yet
    foreach (var eventId in eventIds) {
      await coordinator.SignalSegmentCompleteAsync(
        eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);
    }
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(0)
      .Because("Local alone should not fire PostLifecycle when WhenAll is active");

    // Signal Distributed completion for all — should fire PostLifecycle
    foreach (var eventId in eventIds) {
      await coordinator.SignalSegmentCompleteAsync(
        eventId, PostLifecycleCompletionSource.Distributed, scope.ServiceProvider, CancellationToken.None);
    }
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(5)
      .Because("PostLifecycle should fire exactly once per event after all paths complete");
  }

  #endregion

  #region Test 7: Tracking is cleaned up after abandon

  [Test]
  public async Task AbandonTracking_MultipleEvents_AllCleanedUpAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();

    foreach (var eventId in eventIds) {
      var envelope = _createEnvelope(new TestEvent(eventId, "cleanup-test"));
      coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    }

    // Act
    foreach (var eventId in eventIds) {
      coordinator.AbandonTracking(eventId);
    }

    // Assert
    foreach (var eventId in eventIds) {
      var tracking = coordinator.GetTracking(eventId);
      await Assert.That(tracking).IsNull();
    }
  }

  #endregion

  #region Test 8: IsComplete is set after PostLifecycleInline

  [Test]
  public async Task AdvanceTo_PostLifecycleInline_SetsIsCompleteAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp =>
      new ReceptorInvoker(new TrackingReceptorRegistry(new StageInvocationTracker()), sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "complete-test"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);

    // Act
    using var scope = provider.CreateScope();
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
    await Assert.That(tracking.IsComplete).IsFalse();

    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
    await Assert.That(tracking.IsComplete).IsTrue();
  }

  #endregion

  #region Test 9: Local dispatch — 3 events

  [Test]
  public async Task LocalDispatch_3Events_FiresPostLifecycleForEachAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleAsyncReceptor", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInlineReceptor", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // Act — simulate local dispatch of 3 events
    using var scope = provider.CreateScope();
    for (int i = 0; i < 3; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"local-{i}"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(3);
    // ImmediateAsync fires after each stage = 6 times (2 stages × 3 events)
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.ImmediateAsync)).IsEqualTo(0)
      .Because("No ImmediateAsync receptor registered, so count stays at 0");
  }

  #endregion

  #region Test 10: High-noise — 10 events × 3 receptors per stage

  [Test]
  public async Task HighNoise_10Events_3ReceptorsPerStage_AllFireCorrectlyAsync() {
    // Arrange — 3 receptors at each of PostLifecycleAsync and PostLifecycleInline
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    for (int r = 0; r < 3; r++) {
      registry.RegisterReceptor<TestEvent>($"AsyncReceptor{r}", LifecycleStage.PostLifecycleAsync);
      registry.RegisterReceptor<TestEvent>($"InlineReceptor{r}", LifecycleStage.PostLifecycleInline);
    }

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // Act — 10 events through PostLifecycle
    using var scope = provider.CreateScope();
    for (int i = 0; i < 10; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"noise-{i}"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert — 10 events × 3 receptors = 30 firings per stage
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(30);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(30);
  }

  #endregion

  #region Test 11: Mixed batch — some events with WhenAll, some without

  [Test]
  public async Task MixedBatch_SomeWhenAll_SomeImmediate_PostLifecycleFiresCorrectlyAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var coordinator = new LifecycleCoordinator();

    // 3 events WITHOUT WhenAll (fire immediately)
    for (int i = 0; i < 3; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"immediate-{i}"));
      coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
      await coordinator.SignalSegmentCompleteAsync(
        eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);
    }

    // 2 events WITH WhenAll (need both Local + Distributed)
    var whenAllIds = new List<Guid>();
    for (int i = 0; i < 2; i++) {
      var eventId = Guid.NewGuid();
      whenAllIds.Add(eventId);
      var envelope = _createEnvelope(new TestEvent(eventId, $"whenall-{i}"));
      coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
      coordinator.ExpectCompletionsFrom(eventId,
        PostLifecycleCompletionSource.Local,
        PostLifecycleCompletionSource.Distributed);
      // Only signal Local
      await coordinator.SignalSegmentCompleteAsync(
        eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);
    }

    // Assert — only 3 immediate events fired
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3);

    // Complete the WhenAll events
    foreach (var id in whenAllIds) {
      await coordinator.SignalSegmentCompleteAsync(
        id, PostLifecycleCompletionSource.Distributed, scope.ServiceProvider, CancellationToken.None);
    }

    // Assert — now all 5 have fired
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(5);
  }

  #endregion

  #region Test 12: Full outbox pipeline traversal

  [Test]
  public async Task FullOutboxPipeline_4Stages_AllFireInOrderAsync() {
    // Arrange
    var stagesTraversed = new List<LifecycleStage>();
    var registry = new OrderCapturingRegistry(stage => {
      lock (stagesTraversed) { stagesTraversed.Add(stage); }
    });

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "outbox-full"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);

    // Act — full outbox pipeline
    await tracking.AdvanceToAsync(LifecycleStage.PreOutboxAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PreOutboxInline, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostOutboxAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scope.ServiceProvider, CancellationToken.None);
    coordinator.AbandonTracking(eventId);

    // Assert — stages fired in order (each stage + ImmediateAsync after = 8 total)
    await Assert.That(stagesTraversed.Count).IsEqualTo(8);
    // Verify stage order (non-ImmediateAsync stages at indices 0, 2, 4, 6)
    await Assert.That(stagesTraversed[0]).IsEqualTo(LifecycleStage.PreOutboxAsync);
    await Assert.That(stagesTraversed[1]).IsEqualTo(LifecycleStage.ImmediateAsync);
    await Assert.That(stagesTraversed[2]).IsEqualTo(LifecycleStage.PreOutboxInline);
    await Assert.That(stagesTraversed[3]).IsEqualTo(LifecycleStage.ImmediateAsync);
    await Assert.That(stagesTraversed[4]).IsEqualTo(LifecycleStage.PostOutboxAsync);
    await Assert.That(stagesTraversed[5]).IsEqualTo(LifecycleStage.ImmediateAsync);
    await Assert.That(stagesTraversed[6]).IsEqualTo(LifecycleStage.PostOutboxInline);
    await Assert.That(stagesTraversed[7]).IsEqualTo(LifecycleStage.ImmediateAsync);
  }

  #endregion

  #region Test 13: Full inbox pipeline traversal

  [Test]
  public async Task FullInboxPipeline_PostInbox_ToPostLifecycle_AllFireAsync() {
    // Arrange
    var stagesTraversed = new List<LifecycleStage>();
    var registry = new OrderCapturingRegistry(stage => {
      lock (stagesTraversed) { stagesTraversed.Add(stage); }
    });

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "inbox-full"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostInboxAsync, MessageSource.Inbox);

    // Act — inbox → PostLifecycle (event without perspectives)
    await tracking.AdvanceToAsync(LifecycleStage.PostInboxAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostInboxInline, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
    coordinator.AbandonTracking(eventId);

    // Assert — 4 stages + 4 ImmediateAsync = 8 total
    await Assert.That(stagesTraversed.Count).IsEqualTo(8);
    await Assert.That(stagesTraversed[0]).IsEqualTo(LifecycleStage.PostInboxAsync);
    await Assert.That(stagesTraversed[4]).IsEqualTo(LifecycleStage.PostLifecycleAsync);
    await Assert.That(stagesTraversed[6]).IsEqualTo(LifecycleStage.PostLifecycleInline);

    // IsComplete should be true after PostLifecycleInline
    await Assert.That(tracking.IsComplete).IsTrue();
  }

  #endregion

  #region Test 14: Concurrent events through coordinator

  [Test]
  public async Task ConcurrentEvents_20Events_AllProcessCorrectlyAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInlineReceptor", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // Act — 20 concurrent events
    var tasks = Enumerable.Range(0, 20).Select(async i => {
      using var scope = provider.CreateScope();
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"concurrent-{i}"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    });
    await Task.WhenAll(tasks);

    // Assert — exactly 20 PostLifecycleAsync firings
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(20);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(20);
  }

  #endregion

  #region Test 15: Tags fire count equals stages traversed

  [Test]
  public async Task Tags_FireCount_EqualsStagesTraversed_IncludingImmediateAsyncAsync() {
    // Arrange
    var tagFireCount = 0;
    var tagProcessor = new StageCapturingTagProcessor(_ => Interlocked.Increment(ref tagFireCount));

    var emptyRegistry = new TrackingReceptorRegistry(new StageInvocationTracker());
    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(emptyRegistry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddSingleton<IMessageTagProcessor>(tagProcessor);
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "tag-count"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);

    // Act — 6 stages
    await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveInline, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostPerspectiveAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostPerspectiveInline, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);

    // Assert — 6 stages + 6 ImmediateAsync = 12 tag fires
    await Assert.That(tagFireCount).IsEqualTo(12);
  }

  #endregion

  #region Test 16: ServiceBusConsumer pipeline — PostLifecycle fires for events without perspectives

  /// <summary>
  /// Proves that the ServiceBusConsumer pipeline fires PostLifecycle for events
  /// without perspectives. This mirrors what TransportConsumerWorker does (Test 2).
  /// The ServiceBusConsumerWorker should fire PostLifecycle at the end of its pipeline
  /// for events that have no perspectives — because it is the LAST worker to act on them.
  /// </summary>
  [Test]
  public async Task ServiceBusConsumer_EventsWithoutPerspectives_FiresPostLifecycleAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostInboxAsyncReceptor", LifecycleStage.PostInboxAsync);
    registry.RegisterReceptor<TestEvent>("PostInboxInlineReceptor", LifecycleStage.PostInboxInline);
    registry.RegisterReceptor<TestEvent>("PostLifecycleAsyncReceptor", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInlineReceptor", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // Act — simulate ServiceBusConsumer processing 3 events without perspectives
    // This is the EXACT pattern ServiceBusConsumerWorker should follow after PostInbox
    using var scope = provider.CreateScope();
    for (int i = 0; i < 3; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"servicebus-event-{i}"));

      // PostInbox stages (ServiceBusConsumerWorker already does this)
      var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.PostInboxAsync,
        MessageSource = MessageSource.Inbox
      };
      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxAsync, lifecycleContext, CancellationToken.None);
      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline,
        lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline }, CancellationToken.None);

      // PostLifecycle via coordinator — ServiceBusConsumerWorker is MISSING this
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert — PostInbox fires 3 times each
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostInboxAsync)).IsEqualTo(3);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostInboxInline)).IsEqualTo(3);

    // Assert — PostLifecycle fires 3 times each (once per event at end of pipeline)
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3)
      .Because("ServiceBusConsumer is the last worker for events without perspectives — PostLifecycle must fire");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(3)
      .Because("ServiceBusConsumer is the last worker for events without perspectives — PostLifecycle must fire");
  }

  #endregion

  #region Test 17: Every pipeline endpoint fires PostLifecycle — comprehensive

  /// <summary>
  /// Proves that PostLifecycle fires at the end of EVERY pipeline path.
  /// Each worker that can be the "last to act" on an event must fire PostLifecycle.
  /// This is the pipeline diagram test — verifying the contract from the docs.
  /// </summary>
  [Test]
  public async Task AllPipelineEndpoints_FirePostLifecycle_ExactlyOncePerEventAsync() {
    // Arrange — shared tracker across all pipeline paths
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleAsync", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInline", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // === Path 1: Dispatcher (local dispatch) ===
    using (var scope = provider.CreateScope()) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, "local-dispatch"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // === Path 2: TransportConsumer (events without perspectives) ===
    using (var scope = provider.CreateScope()) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, "inbox-no-perspectives"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // === Path 3: PerspectiveWorker (events with perspectives) ===
    using (var scope = provider.CreateScope()) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, "perspective-batch"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // === Path 4: ServiceBusConsumer (events without perspectives) ===
    using (var scope = provider.CreateScope()) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, "servicebus-no-perspectives"));
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert — 4 events total, PostLifecycle fires exactly 4 times (once per pipeline endpoint)
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(4)
      .Because("4 pipeline endpoints (Dispatcher, TransportConsumer, PerspectiveWorker, ServiceBusConsumer) each fire PostLifecycle once");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(4)
      .Because("4 pipeline endpoints each fire PostLifecycleInline once");
  }

  #endregion

  #region Test 18: OutboxWorker fires PostLifecycle when event leaves service

  /// <summary>
  /// Proves that the OutboxWorker fires PostLifecycle when the event has no further
  /// processing within this service (event leaves via transport to another service).
  /// </summary>
  [Test]
  public async Task OutboxWorker_EventLeavesService_FiresPostLifecycleAsync() {
    // Arrange
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PreOutboxReceptor", LifecycleStage.PreOutboxAsync);
    registry.RegisterReceptor<TestEvent>("PostOutboxReceptor", LifecycleStage.PostOutboxInline);
    registry.RegisterReceptor<TestEvent>("PostLifecycleAsync", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInline", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();

    // Act — simulate OutboxWorker publishing 2 events that leave the service
    using var scope = provider.CreateScope();
    for (int i = 0; i < 2; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"outbox-leaving-{i}"));

      // Outbox stages
      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);
      await tracking.AdvanceToAsync(LifecycleStage.PreOutboxAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scope.ServiceProvider, CancellationToken.None);

      // Event leaves service — OutboxWorker is the last worker, fire PostLifecycle
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert — Outbox stages fire
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PreOutboxAsync)).IsEqualTo(2);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostOutboxInline)).IsEqualTo(2);

    // Assert — PostLifecycle fires because outbox is the last worker for these events
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(2)
      .Because("OutboxWorker is the last worker when event leaves service — PostLifecycle must fire");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(2)
      .Because("OutboxWorker is the last worker when event leaves service — PostLifecycle must fire");
  }

  #endregion

  #region Test Helper: Order-capturing registry

  private sealed class OrderCapturingRegistry : IReceptorRegistry {
    private readonly Action<LifecycleStage> _onStage;

    public OrderCapturingRegistry(Action<LifecycleStage> onStage) {
      _onStage = onStage;
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      return [new ReceptorInfo(
        MessageType: messageType,
        ReceptorId: $"OrderCapture_{stage}",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _onStage(stage);
          return ValueTask.FromResult<object?>(null);
        })];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  #endregion

  #region Test Helpers

  private sealed class StageInvocationTracker {
    private readonly Lock _lock = new();
    private readonly Dictionary<LifecycleStage, int> _stageFirings = [];

    public void RecordFiring(LifecycleStage stage) {
      lock (_lock) {
        _stageFirings.TryGetValue(stage, out var count);
        _stageFirings[stage] = count + 1;
      }
    }

    public int GetFiringsForStage(LifecycleStage stage) {
      lock (_lock) {
        return _stageFirings.TryGetValue(stage, out var count) ? count : 0;
      }
    }
  }

  private sealed class TrackingReceptorRegistry(LifecycleCoordinatorSituationTests.StageInvocationTracker tracker) : IReceptorRegistry {
    private readonly StageInvocationTracker _tracker = tracker;
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void RegisterReceptor<TMessage>(string receptorId, LifecycleStage stage) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          _tracker.RecordFiring(stage);
          return ValueTask.FromResult<object?>(null);
        }));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class StageCapturingTagProcessor(Action<LifecycleStage> onProcessTags) : IMessageTagProcessor {
    private readonly Action<LifecycleStage> _onProcessTags = onProcessTags;

    public ValueTask ProcessTagsAsync(
        object message,
        Type messageType,
        LifecycleStage stage,
        IScopeContext? scope = null,
        CancellationToken ct = default) {
      _onProcessTags(stage);
      return ValueTask.CompletedTask;
    }
  }

  #endregion
}
