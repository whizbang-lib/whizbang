#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// Proves PostLifecycle fires at the end of EVERY pipeline path.
/// Each worker (Dispatcher, OutboxWorker, TransportConsumer, PerspectiveWorker)
/// is tested in two scenarios: direct (no WhenAll) and WhenAll (Route.Both).
///
/// These tests simulate the ACTUAL code path each worker takes and verify
/// PostLifecycle fires exactly once per event at the pipeline endpoint.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
/// <tests>src/Whizbang.Core/Lifecycle/LifecycleCoordinator.cs</tests>
public class PostLifecyclePipelineTests {
  private sealed record TestEvent(Guid Id, string Data) : IEvent;

  private static MessageEnvelope<T> _createEnvelope<T>(T payload) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = payload,
      Hops = []
    };
  }

  private static (ServiceProvider Provider, StageInvocationTracker Tracker) _createTestInfra() {
    var tracker = new StageInvocationTracker();
    var registry = new TrackingReceptorRegistry(tracker);
    registry.RegisterReceptor<TestEvent>("PostLifecycleAsync", LifecycleStage.PostLifecycleAsync);
    registry.RegisterReceptor<TestEvent>("PostLifecycleInline", LifecycleStage.PostLifecycleInline);

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddScoped<IMessageContextAccessor, MessageContextAccessor>();
    return (services.BuildServiceProvider(), tracker);
  }

  // ════════════════════════════════════════════════════════════════════════
  //  DISPATCHER — Local dispatch path
  // ════════════════════════════════════════════════════════════════════════

  #region Dispatcher — No WhenAll

  /// <summary>
  /// Dispatcher is the last worker for locally dispatched events (Route.Local).
  /// PostLifecycle fires directly via the coordinator — no WhenAll needed.
  /// </summary>
  [Test]
  public async Task Dispatcher_NoWhenAll_FiresPostLifecycleAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();

    // Act — simulate Dispatcher._invokePostLifecycleReceptorsAsync
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
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3)
      .Because("Dispatcher fires PostLifecycle for each locally dispatched event");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(3);
  }

  #endregion

  #region Dispatcher — WhenAll (Route.Both)

  /// <summary>
  /// With Route.Both(), Dispatcher signals Local completion but does NOT fire
  /// PostLifecycle until the distributed path also completes.
  /// </summary>
  [Test]
  public async Task Dispatcher_WhenAll_WaitsForDistributedPathAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "route-both"));

    coordinator.BeginTracking(eventId, envelope, LifecycleStage.LocalImmediateAsync, MessageSource.Local);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act — Dispatcher signals local completion
    using var scope = provider.CreateScope();
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);

    // Assert — PostLifecycle has NOT fired yet
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(0)
      .Because("Dispatcher alone should NOT fire PostLifecycle when WhenAll is active — waiting for distributed path");

    // Act — distributed path completes later
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOW PostLifecycle fires
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(1)
      .Because("PostLifecycle fires exactly once after both paths complete");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(1);
  }

  #endregion

  // ════════════════════════════════════════════════════════════════════════
  //  OUTBOX WORKER — Publishing path
  // ════════════════════════════════════════════════════════════════════════

  #region OutboxWorker — No WhenAll

  /// <summary>
  /// OutboxWorker is the last worker when an event leaves the service via transport.
  /// PostLifecycle fires after PostOutbox stages.
  /// </summary>
  [Test]
  public async Task OutboxWorker_NoWhenAll_FiresPostLifecycleAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();

    // Act — simulate OutboxWorker publishing 2 events that leave the service
    using var scope = provider.CreateScope();
    for (int i = 0; i < 2; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"outbox-{i}"));

      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);
      await tracking.AdvanceToAsync(LifecycleStage.PreOutboxAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scope.ServiceProvider, CancellationToken.None);
      // Event published to transport — this service is done with it
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(2)
      .Because("OutboxWorker fires PostLifecycle when event leaves the service");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(2);
  }

  #endregion

  #region OutboxWorker — WhenAll

  /// <summary>
  /// With WhenAll, OutboxWorker signals Outbox completion but PostLifecycle
  /// waits for all paths to complete.
  /// </summary>
  [Test]
  public async Task OutboxWorker_WhenAll_WaitsForOtherPathsAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "outbox-whenall"));

    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Outbox);

    // Act — Outbox signals completion
    using var scope = provider.CreateScope();
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Outbox, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOT yet, Local path still pending
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(0)
      .Because("OutboxWorker alone should NOT fire PostLifecycle when WhenAll is active");

    // Act — Local path completes
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOW fires
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(1);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(1);
  }

  #endregion

  // ════════════════════════════════════════════════════════════════════════
  //  TRANSPORT CONSUMER — Inbox path (events without perspectives)
  // ════════════════════════════════════════════════════════════════════════

  #region TransportConsumer — No WhenAll

  /// <summary>
  /// TransportConsumer is the last worker for events WITHOUT perspectives.
  /// PostLifecycle fires after PostInbox stages.
  /// </summary>
  [Test]
  public async Task TransportConsumer_NoWhenAll_FiresPostLifecycleAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();

    // Act — simulate TransportConsumer processing 3 events without perspectives
    using var scope = provider.CreateScope();
    for (int i = 0; i < 3; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"inbox-{i}"));

      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3)
      .Because("TransportConsumer fires PostLifecycle for events without perspectives");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(3);
  }

  #endregion

  #region TransportConsumer — WhenAll

  /// <summary>
  /// With WhenAll, TransportConsumer signals Distributed completion.
  /// PostLifecycle waits for all paths.
  /// </summary>
  [Test]
  public async Task TransportConsumer_WhenAll_WaitsForLocalPathAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "inbox-whenall"));

    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PostInboxAsync, MessageSource.Inbox);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act — Distributed path signals (TransportConsumer finished)
    using var scope = provider.CreateScope();
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOT yet
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(0)
      .Because("TransportConsumer alone should NOT fire PostLifecycle when WhenAll is active");

    // Act — Local path completes
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOW fires
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(1);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(1);
  }

  #endregion

  // ════════════════════════════════════════════════════════════════════════
  //  PERSPECTIVE WORKER — Perspective path (events with perspectives)
  // ════════════════════════════════════════════════════════════════════════

  #region PerspectiveWorker — No WhenAll

  /// <summary>
  /// PerspectiveWorker is the last worker for events WITH perspectives.
  /// PostLifecycle fires once per unique event at batch end.
  /// </summary>
  [Test]
  public async Task PerspectiveWorker_NoWhenAll_FiresPostLifecycleAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();

    // Act — simulate PerspectiveWorker Phase 5: 3 unique events
    using var scope = provider.CreateScope();
    for (int i = 0; i < 3; i++) {
      var eventId = Guid.NewGuid();
      var envelope = _createEnvelope(new TestEvent(eventId, $"perspective-{i}"));

      var tracking = coordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleAsync, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, CancellationToken.None);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, CancellationToken.None);
      coordinator.AbandonTracking(eventId);
    }

    // Assert
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(3)
      .Because("PerspectiveWorker fires PostLifecycle once per unique event after all perspectives complete");
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(3);
  }

  #endregion

  #region PerspectiveWorker — WhenAll

  /// <summary>
  /// With WhenAll, PerspectiveWorker signals Distributed completion.
  /// PostLifecycle waits for all paths (e.g., Local path from Dispatcher).
  /// </summary>
  [Test]
  public async Task PerspectiveWorker_WhenAll_WaitsForLocalPathAsync() {
    // Arrange
    var (provider, tracker) = _createTestInfra();
    var coordinator = new LifecycleCoordinator();
    var eventId = Guid.NewGuid();
    var envelope = _createEnvelope(new TestEvent(eventId, "perspective-whenall"));

    coordinator.BeginTracking(eventId, envelope, LifecycleStage.PrePerspectiveAsync, MessageSource.Local);
    coordinator.ExpectCompletionsFrom(eventId,
      PostLifecycleCompletionSource.Local,
      PostLifecycleCompletionSource.Distributed);

    // Act — PerspectiveWorker signals Distributed completion
    using var scope = provider.CreateScope();
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Distributed, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOT yet
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(0)
      .Because("PerspectiveWorker alone should NOT fire PostLifecycle when WhenAll is active");

    // Act — Local path completes (Dispatcher finished its part)
    await coordinator.SignalSegmentCompleteAsync(
      eventId, PostLifecycleCompletionSource.Local, scope.ServiceProvider, CancellationToken.None);

    // Assert — NOW fires
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleAsync)).IsEqualTo(1);
    await Assert.That(tracker.GetFiringsForStage(LifecycleStage.PostLifecycleInline)).IsEqualTo(1);
  }

  #endregion

  // ════════════════════════════════════════════════════════════════════════
  //  Test Helpers
  // ════════════════════════════════════════════════════════════════════════

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

  private sealed class TrackingReceptorRegistry(PostLifecyclePipelineTests.StageInvocationTracker tracker) : IReceptorRegistry {
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

  #endregion
}
