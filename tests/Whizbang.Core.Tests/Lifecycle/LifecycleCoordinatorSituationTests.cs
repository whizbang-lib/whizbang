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
      Hops = []
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
