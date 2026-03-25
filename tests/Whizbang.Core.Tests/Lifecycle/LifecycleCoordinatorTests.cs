using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Unit tests for <see cref="LifecycleCoordinator"/> — the centralized coordinator
/// for event lifecycle stage transitions.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
public class LifecycleCoordinatorTests {
  private sealed record TestEvent(Guid Id, string Data) : IEvent;

  private static MessageEnvelope<T> _createEnvelope<T>(T payload) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = payload,
      Hops = []
    };
  }

  private static IServiceProvider _createScopedProvider(
    InvocationTracker? tracker = null,
    IMessageTagProcessor? tagProcessor = null) {
    var services = new ServiceCollection();
    if (tracker is not null) {
      var registry = new TrackingReceptorRegistry(tracker);
      services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    } else {
      // Provide a no-op invoker when no tracker needed
      var emptyRegistry = new TrackingReceptorRegistry(new InvocationTracker());
      services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(emptyRegistry, sp));
    }
    if (tagProcessor is not null) {
      services.AddSingleton(tagProcessor);
    }
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    return provider.CreateScope().ServiceProvider;
  }

  #region BeginTracking

  [Test]
  public async Task BeginTracking_CreatesTracking_AtEntryStageAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));

    // Act
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);

    // Assert
    await Assert.That(tracking).IsNotNull();
    await Assert.That(tracking.EventId).IsEqualTo(eventId);
    await Assert.That(tracking.CurrentStage).IsEqualTo(LifecycleStage.PrePerspectiveAsync);
    await Assert.That(tracking.IsComplete).IsFalse();
  }

  [Test]
  public async Task GetTracking_ReturnsTracking_WhenExistsAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PreInboxAsync, MessageSource.Inbox);

    // Act
    var tracking = coordinator.GetTracking(eventId);

    // Assert
    await Assert.That(tracking).IsNotNull();
    await Assert.That(tracking!.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task GetTracking_ReturnsNull_WhenNotTrackedAsync() {
    var coordinator = new LifecycleCoordinator();
    var tracking = coordinator.GetTracking(Guid.NewGuid());
    await Assert.That(tracking).IsNull();
  }

  #endregion

  #region AbandonTracking

  [Test]
  public async Task AbandonTracking_RemovesFromDictionaryAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);

    // Act
    coordinator.AbandonTracking(eventId);

    // Assert
    var tracking = coordinator.GetTracking(eventId);
    await Assert.That(tracking).IsNull();
  }

  [Test]
  public async Task AbandonTracking_NoOp_WhenNotTrackedAsync() {
    // Should not throw
    var coordinator = new LifecycleCoordinator();
    coordinator.AbandonTracking(Guid.NewGuid());

    // Verify no tracking exists
    var tracking = coordinator.GetTracking(Guid.NewGuid());
    await Assert.That(tracking).IsNull();
  }

  #endregion

  #region AdvanceToAsync

  [Test]
  public async Task AdvanceTo_InvokesReceptors_AndUpdatesCurrentStageAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var scopedProvider = _createScopedProvider(tracker);
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));

    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);

    // Act
    await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveAsync, scopedProvider, CancellationToken.None);

    // Assert
    await Assert.That(tracking.CurrentStage).IsEqualTo(LifecycleStage.PrePerspectiveAsync);
  }

  [Test]
  public async Task AdvanceTo_FiresImmediateAsync_AfterEachStageAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    // Register receptors at both PrePerspectiveAsync and ImmediateAsync
    registry.RegisterReceptor<TestEvent>("PrePerspReceptor", LifecycleStage.PrePerspectiveAsync);
    registry.RegisterReceptor<TestEvent>("ImmediateReceptor", LifecycleStage.ImmediateAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));

    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);

    // Act
    await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveAsync, scopedProvider, CancellationToken.None);

    // Assert - Both the stage receptor and ImmediateAsync should fire
    await Assert.That(tracker.Invocations).Count().IsEqualTo(2);
    await Assert.That(tracker.Invocations[0].Stage).IsEqualTo(LifecycleStage.PrePerspectiveAsync);
    await Assert.That(tracker.Invocations[1].Stage).IsEqualTo(LifecycleStage.ImmediateAsync);
  }

  #endregion

  #region GetTracking during processing

  [Test]
  public async Task GetTracking_ReturnsCurrentStage_DuringProcessingAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));
    var scopedProvider = _createScopedProvider();

    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);

    // Act
    await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveInline, scopedProvider, CancellationToken.None);
    var retrieved = coordinator.GetTracking(eventId);

    // Assert
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.CurrentStage).IsEqualTo(LifecycleStage.PrePerspectiveInline);
  }

  #endregion

  #region Thread Safety

  [Test]
  public async Task AdvanceTo_ThreadSafe_ConcurrentAccessAsync() {
    // Arrange - multiple events tracked concurrently
    var coordinator = new LifecycleCoordinator();
    var eventCount = 10;
    var trackings = new List<ILifecycleTracking>();
    var scopedProvider = _createScopedProvider();

    for (int i = 0; i < eventCount; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"test-{i}"));
      trackings.Add(coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local));
    }

    // Act - advance all concurrently
    var tasks = trackings.Select(t =>
      t.AdvanceToAsync(LifecycleStage.PostPerspectiveInline, scopedProvider, CancellationToken.None).AsTask());
    await Task.WhenAll(tasks);

    // Assert - all should have advanced
    foreach (var tracking in trackings) {
      await Assert.That(tracking.CurrentStage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
    }
  }

  #endregion

  #region WhenAll Pattern

  [Test]
  public async Task SignalSegmentComplete_NoWhenAll_FiresPostLifecycleImmediatelyAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);

    // Act - signal without ExpectCompletionsFrom
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);

    // Assert - PostLifecycle should fire immediately
    await Assert.That(tracker.Invocations.Any(i => i.Stage == LifecycleStage.PostLifecycleAsync)).IsTrue();
  }

  [Test]
  public async Task WhenAll_LocalAlone_DoesNotFirePostLifecycleAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);

    // Register WhenAll — both Local and Distributed must complete
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act - only Local completes
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);

    // Assert - PostLifecycle should NOT fire yet
    await Assert.That(tracker.Invocations.Any(i => i.Stage == LifecycleStage.PostLifecycleAsync)).IsFalse();
  }

  [Test]
  public async Task WhenAll_BothComplete_FiresPostLifecycleOnceAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "test"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);

    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act - both complete
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scopedProvider, CancellationToken.None);

    // Assert - PostLifecycle should fire exactly once
    var postLifecycleFirings = tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync);
    await Assert.That(postLifecycleFirings).IsEqualTo(1);
  }

  #endregion

  #region AdvanceToAsync — null invoker

  [Test]
  public async Task AdvanceTo_NullInvoker_StillUpdatesStageAsync() {
    // Arrange — no IReceptorInvoker registered
    var services = new ServiceCollection();
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "null-invoker"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);

    // Act
    await tracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scopedProvider, CancellationToken.None);

    // Assert — stage updated despite no invoker
    await Assert.That(tracking.CurrentStage).IsEqualTo(LifecycleStage.PostOutboxInline);
  }

  #endregion

  #region AdvanceBatchAsync

  [Test]
  public async Task AdvanceBatchAsync_AdvancesAllTrackingsAsync() {
    // Arrange
    var scopedProvider = _createScopedProvider();
    var coordinator = new LifecycleCoordinator();
    var trackings = new List<ILifecycleTracking>();
    for (int i = 0; i < 5; i++) {
      var id = Guid.NewGuid();
      trackings.Add(coordinator.BeginTracking(
        id, _createEnvelope(new TestEvent(id, $"batch-{i}")),
        LifecycleStage.PrePerspectiveAsync, MessageSource.Local));
    }

    // Act
    await ILifecycleTracking.AdvanceBatchAsync(
      trackings, LifecycleStage.PostPerspectiveInline, scopedProvider, CancellationToken.None);

    // Assert
    foreach (var t in trackings) {
      await Assert.That(t.CurrentStage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
    }
  }

  [Test]
  public async Task AdvanceBatchAsync_EmptyCollection_NoOpAsync() {
    // Should not throw
    var scopedProvider = _createScopedProvider();
    await ILifecycleTracking.AdvanceBatchAsync(
      [], LifecycleStage.PostPerspectiveInline, scopedProvider, CancellationToken.None);
    // No exception = pass
    await Assert.That(scopedProvider).IsNotNull();
  }

  #endregion

  #region BeginTracking — edge cases

  [Test]
  public async Task BeginTracking_SameEventIdTwice_ReturnsFirstTrackingAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope1 = _createEnvelope(new TestEvent(eventId, "first"));
    var envelope2 = _createEnvelope(new TestEvent(eventId, "second"));

    // Act
    var tracking1 = coordinator.BeginTracking(
      eventId, envelope1, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    var tracking2 = coordinator.BeginTracking(
      eventId, envelope2, LifecycleStage.PostInboxAsync, MessageSource.Inbox);

    // Assert — TryAdd returns false for duplicate, first wins
    var retrieved = coordinator.GetTracking(eventId);
    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.CurrentStage).IsEqualTo(LifecycleStage.PrePerspectiveAsync);
  }

  [Test]
  public async Task BeginTracking_WithStreamIdAndPerspectiveType_PreservesContextAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "context-test"));

    // Act
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local,
      streamId: streamId, perspectiveType: typeof(string));

    // Assert
    await Assert.That(tracking.EventId).IsEqualTo(eventId);
    await Assert.That(tracking.CurrentStage).IsEqualTo(LifecycleStage.PrePerspectiveAsync);
  }

  #endregion

  #region Context property propagation

  [Test]
  public async Task AdvanceTo_SetsCorrectContextProperties_OnReceptorInvocationAsync() {
    // Arrange — capture the context passed to InvokeAsync
    ILifecycleContext? capturedContext = null;
    var registry = new ContextCapturingRegistry(ctx => {
      // Capture only the first invocation (the actual stage, not ImmediateAsync)
      capturedContext ??= ctx;
    });

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    services.AddScoped<ILifecycleContextAccessor, TestLifecycleContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "context"));

    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostPerspectiveAsync, MessageSource.Inbox,
      streamId: streamId, perspectiveType: typeof(int));

    // Act
    await tracking.AdvanceToAsync(LifecycleStage.PostPerspectiveAsync, scopedProvider, CancellationToken.None);

    // Assert — verify context was set correctly
    await Assert.That(capturedContext).IsNotNull();
    await Assert.That(capturedContext!.CurrentStage).IsEqualTo(LifecycleStage.PostPerspectiveAsync);
    await Assert.That(capturedContext.EventId).IsEqualTo(eventId);
    await Assert.That(capturedContext.StreamId).IsEqualTo(streamId);
    await Assert.That(capturedContext.PerspectiveType).IsEqualTo(typeof(int));
    await Assert.That(capturedContext.MessageSource).IsEqualTo(MessageSource.Inbox);
    await Assert.That(capturedContext.AttemptNumber).IsEqualTo(1);
  }

  #endregion

  #region Multiple receptors at same stage

  [Test]
  public async Task AdvanceTo_MultipleReceptorsAtSameStage_AllFireAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("Receptor1", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("Receptor2", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("Receptor3", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "multi-receptor"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);

    // Act
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scopedProvider, CancellationToken.None);

    // Assert — all 3 receptors fired
    var postLifecycleFirings = tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync);
    await Assert.That(postLifecycleFirings).IsEqualTo(3);
  }

  #endregion

  #region SignalSegmentComplete — edge cases

  [Test]
  public async Task SignalSegmentComplete_NonTrackedEvent_NoOpAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var scopedProvider = _createScopedProvider();

    // Act — signal for an event that was never tracked
    await coordinator.SignalSegmentCompleteAsync(
      Guid.NewGuid(), PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);

    // Assert — no exception
    await Assert.That(coordinator.GetTracking(Guid.NewGuid())).IsNull();
  }

  [Test]
  public async Task SignalSegmentComplete_DuplicateSignal_FiresPostLifecycleOnceAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "dup-signal"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act — signal Local twice, then Distributed
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scopedProvider, CancellationToken.None);

    // Assert — PostLifecycle should fire exactly once
    var firings = tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync);
    await Assert.That(firings).IsEqualTo(1);
  }

  [Test]
  public async Task WhenAll_ThreeSources_RequiresAllAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "three-sources"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed,
      PostLifecycleCompletionSource.Outbox);

    // Act — only two of three
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scopedProvider, CancellationToken.None);

    await Assert.That(tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync)).IsEqualTo(0)
      .Because("Two of three sources is not enough");

    // Complete the third
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Outbox, scopedProvider, CancellationToken.None);

    await Assert.That(tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync)).IsEqualTo(1);
  }

  #endregion

  #region AbandonTracking cleans up WhenAll

  [Test]
  public async Task AbandonTracking_CleansUpWhenAllStateAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "cleanup-whenall"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act
    coordinator.AbandonTracking(eventId);

    // Assert — both tracking and WhenAll should be cleaned up
    await Assert.That(coordinator.GetTracking(eventId)).IsNull();

    // Signal should be a no-op now (no tracking exists)
    var scopedProvider = _createScopedProvider();
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scopedProvider, CancellationToken.None);
    // No exception = pass
  }

  #endregion

  #region Concurrent WhenAll signals

  [Test]
  public async Task WhenAll_ConcurrentSignals_FiresPostLifecycleExactlyOnceAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "concurrent-whenall"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act — signal both concurrently
    var scope1 = provider.CreateScope();
    var scope2 = provider.CreateScope();
    var task1 = coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scope1.ServiceProvider, CancellationToken.None).AsTask();
    var task2 = coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scope2.ServiceProvider, CancellationToken.None).AsTask();
    await Task.WhenAll(task1, task2);

    // Assert — PostLifecycle fires exactly once (TrySignalAndComplete guarantees atomicity)
    var firings = tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync);
    await Assert.That(firings).IsEqualTo(1);
  }

  #endregion

  #region Concurrent BeginTracking and AbandonTracking

  [Test]
  public async Task ConcurrentBeginAndAbandon_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventIds = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();

    // Act — concurrent begin + abandon
    var tasks = eventIds.Select(async id => {
      var envelope = _createEnvelope(new TestEvent(id, "concurrent"));
      coordinator.BeginTracking(id, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);
      await Task.Yield(); // Give other tasks a chance
      coordinator.AbandonTracking(id);
    });
    await Task.WhenAll(tasks);

    // Assert — all cleaned up
    foreach (var id in eventIds) {
      await Assert.That(coordinator.GetTracking(id)).IsNull();
    }
  }

  #endregion

  #region BeginTracking — same instance

  [Test]
  public async Task BeginTracking_SameEventTwice_ReturnsSameInstanceAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope1 = _createEnvelope(new TestEvent(eventId, "first"));
    var envelope2 = _createEnvelope(new TestEvent(eventId, "second"));

    // Act
    var tracking1 = coordinator.BeginTracking(
      eventId, envelope1, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    var tracking2 = coordinator.BeginTracking(
      eventId, envelope2, LifecycleStage.PostInboxAsync, MessageSource.Inbox);

    // Assert — GetOrAdd returns the exact same object
    await Assert.That(ReferenceEquals(tracking1, tracking2)).IsTrue();
  }

  #endregion

  #region AdvanceToAsync — duplicate and post-complete

  [Test]
  public async Task AdvanceToAsync_SameStageCalledTwice_InvokesOnceAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleReceptor", LifecycleStage.PostLifecycleAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "dup-advance"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);

    // Act — advance to PostLifecycleAsync twice
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scopedProvider, CancellationToken.None);
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scopedProvider, CancellationToken.None);

    // Assert — receptor fires exactly once (stage guard)
    var firings = tracker.Invocations.Count(i => i.Stage == LifecycleStage.PostLifecycleAsync);
    await Assert.That(firings).IsEqualTo(1);
  }

  [Test]
  public async Task AdvanceToAsync_AfterComplete_NoOpAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PrePerspReceptor", LifecycleStage.PrePerspectiveAsync);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    var provider = services.BuildServiceProvider();
    var scopedProvider = provider.CreateScope().ServiceProvider;

    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "post-complete"));
    var tracking = coordinator.BeginTracking(
      eventId, envelope, LifecycleStage.PostLifecycleInline, MessageSource.Local);

    // Act — advance to PostLifecycleInline (sets IsComplete=true), then try another stage
    await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, CancellationToken.None);
    await Assert.That(tracking.IsComplete).IsTrue();

    await tracking.AdvanceToAsync(LifecycleStage.PrePerspectiveAsync, scopedProvider, CancellationToken.None);

    // Assert — PrePerspectiveAsync receptor should not fire after complete
    var preFirings = tracker.Invocations.Count(i => i.Stage == LifecycleStage.PrePerspectiveAsync);
    await Assert.That(preFirings).IsEqualTo(0);
  }

  #endregion

  #region Perspective completions

  [Test]
  public async Task ExpectPerspectiveCompletions_AllSignal_ReturnsTrueAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "all-perspectives"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["P1", "P2", "P3"]);

    // Act
    coordinator.SignalPerspectiveComplete(eventId, "P1");
    coordinator.SignalPerspectiveComplete(eventId, "P2");
    var lastSignalResult = coordinator.SignalPerspectiveComplete(eventId, "P3");

    // Assert
    await Assert.That(lastSignalResult).IsTrue();
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue();
  }

  [Test]
  public async Task ExpectPerspectiveCompletions_PartialSignal_ReturnsFalseAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "partial-perspectives"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["P1", "P2", "P3"]);

    // Act — only signal 2 of 3
    coordinator.SignalPerspectiveComplete(eventId, "P1");
    coordinator.SignalPerspectiveComplete(eventId, "P2");

    // Assert
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsFalse();
  }

  [Test]
  public async Task SignalPerspectiveComplete_ExactNameTracking_Async() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "name-tracking"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["A", "B", "C"]);

    // Act & Assert — signal A and B, not yet complete
    coordinator.SignalPerspectiveComplete(eventId, "A");
    coordinator.SignalPerspectiveComplete(eventId, "B");
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsFalse();

    // Signal C — now complete
    coordinator.SignalPerspectiveComplete(eventId, "C");
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue();
  }

  [Test]
  public async Task SignalPerspectiveComplete_DuplicateSignal_IdempotentAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "dup-signal"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["X", "Y"]);

    // Act — signal X twice, then Y
    coordinator.SignalPerspectiveComplete(eventId, "X");
    coordinator.SignalPerspectiveComplete(eventId, "X"); // duplicate — should be idempotent
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsFalse();

    coordinator.SignalPerspectiveComplete(eventId, "Y");

    // Assert — completes normally despite duplicate
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue();
  }

  [Test]
  public async Task SignalPerspectiveComplete_UnknownPerspective_IgnoredAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "unknown-perspective"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["A", "B"]);

    // Act — signal an unknown perspective name that was never registered
    var result = coordinator.SignalPerspectiveComplete(eventId, "Z");

    // Assert — no error thrown, returns false (not complete)
    await Assert.That(result).IsFalse();
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsFalse();
  }

  /// <summary>
  /// When no expectations are registered for an event, AreAllPerspectivesComplete should return true.
  /// PostAllPerspectives/PostLifecycle are terminal stages that must always fire — the WhenAll gate
  /// controls timing (wait for all to complete), not whether these stages fire.
  /// Without this, events with no expectations get stuck forever (no PostLifecycle, no tag hooks).
  /// </summary>
  [Test]
  public async Task AreAllPerspectivesComplete_NoExpectationsRegistered_ReturnsTrueAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "no-expectations"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    // Note: NO call to ExpectPerspectiveCompletions — simulates event type key mismatch

    // Act
    var result = coordinator.AreAllPerspectivesComplete(eventId);

    // Assert — should return true so PostAllPerspectives/PostLifecycle fire
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task AbandonTracking_ClearsPerspectiveStateAsync() {
    // Arrange
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "abandon-perspective"));
    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectPerspectiveCompletions(eventId, ["A", "B", "C"]);
    coordinator.SignalPerspectiveComplete(eventId, "A");
    coordinator.SignalPerspectiveComplete(eventId, "B");

    // Act
    coordinator.AbandonTracking(eventId);

    // Assert — perspective state is cleared, AreAllPerspectivesComplete returns true
    // (no expectations = no WhenAll gate needed, terminal stages fire immediately)
    await Assert.That(coordinator.AreAllPerspectivesComplete(eventId)).IsTrue();
  }

  #endregion

  #region Test Helpers

  /// <summary>
  /// Test implementation of ILifecycleContextAccessor.
  /// </summary>
  private sealed class TestLifecycleContextAccessor : ILifecycleContextAccessor {
    public ILifecycleContext? Current { get; set; }
  }

  /// <summary>
  /// Registry that captures the lifecycle context passed to InvokeAsync.
  /// </summary>
  private sealed class ContextCapturingRegistry : IReceptorRegistry {
    private readonly Action<ILifecycleContext?> _onInvoke;

    public ContextCapturingRegistry(Action<ILifecycleContext?> onInvoke) {
      _onInvoke = onInvoke;
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      // Return a receptor at every stage so it always fires
      return [new ReceptorInfo(
        MessageType: messageType,
        ReceptorId: "ContextCapture",
        InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
          var accessor = sp.GetService<ILifecycleContextAccessor>();
          _onInvoke(accessor?.Current);
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

  private sealed class InvocationTracker {
    private readonly List<(string ReceptorId, LifecycleStage Stage)> _invocations = [];
    private readonly Lock _lock = new();
    public List<(string ReceptorId, LifecycleStage Stage)> Invocations {
      get {
        lock (_lock) {
          return [.. _invocations];
        }
      }
    }
    public void RecordInvocation(string receptorId, LifecycleStage stage) {
      lock (_lock) {
        _invocations.Add((receptorId, stage));
      }
    }
  }

  private sealed class TrackingReceptorRegistry(LifecycleCoordinatorTests.InvocationTracker tracker) : IReceptorRegistry {
    private readonly InvocationTracker _tracker = tracker;
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
          _tracker.RecordInvocation(receptorId, stage);
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

  #endregion
}
