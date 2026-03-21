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
