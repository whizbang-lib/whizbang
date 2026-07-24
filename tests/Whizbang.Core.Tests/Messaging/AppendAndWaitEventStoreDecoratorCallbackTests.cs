using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for callback functionality in <see cref="AppendAndWaitEventStoreDecorator"/>.
/// These tests verify:
/// - onWaiting is called ONLY when actual waiting occurs
/// - onWaiting is NOT called for NoPendingEvents outcomes
/// - onDecisionMade is ALWAYS called regardless of outcome
/// - Context values are correct
/// </summary>
/// <docs>core-concepts/event-store#append-and-wait-callbacks</docs>
[Category("EventStore")]
[Category("Sync")]
[Category("Callbacks")]
public sealed class AppendAndWaitEventStoreDecoratorCallbackTests {
  private sealed record TestEvent(string Value) : IEvent;
  private sealed class FakePerspective { }

  #region AppendAndWaitAsync<TMessage, TPerspective> Callback Tests

  [Test]
  public async Task AppendAndWaitAsync_Perspective_InvokesOnWaitingCallbackAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter {
      ResultToReturn = new SyncResult(SyncOutcome.Synced, 1, TimeSpan.FromMilliseconds(50))
    };
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    // Act
    await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert - onWaiting SHOULD be called for perspective-specific waits
    await Assert.That(capturedWaiting).IsNotNull();
    await Assert.That(capturedWaiting!.PerspectiveType).IsEqualTo(typeof(FakePerspective));
    await Assert.That(capturedWaiting.EventCount).IsEqualTo(1);
    await Assert.That(capturedWaiting.StreamIds.Count).IsEqualTo(1);
    await Assert.That(capturedWaiting.StreamIds[0]).IsEqualTo(streamId);
    await Assert.That(capturedWaiting.Timeout).IsEqualTo(TimeSpan.FromSeconds(5));

    // Assert - onDecisionMade SHOULD always be called
    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.PerspectiveType).IsEqualTo(typeof(FakePerspective));
    await Assert.That(capturedDecision.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(capturedDecision.DidWait).IsTrue();
    await Assert.That(capturedDecision.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task AppendAndWaitAsync_Perspective_WhenTimedOut_InvokesBothCallbacksAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter {
      ResultToReturn = new SyncResult(SyncOutcome.TimedOut, 0, TimeSpan.FromMilliseconds(100))
    };
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    // Act
    await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromMilliseconds(100),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert - onWaiting called before waiting starts
    await Assert.That(capturedWaiting).IsNotNull();

    // Assert - onDecisionMade called with timeout outcome
    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(capturedDecision.DidWait).IsTrue();
  }

  [Test]
  public async Task AppendAndWaitAsync_Perspective_CallbackExceptionDoesNotBreakSyncAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter {
      ResultToReturn = new SyncResult(SyncOutcome.Synced, 1, TimeSpan.FromMilliseconds(10))
    };
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    // Act - callbacks throw exceptions, but sync should still work
    var result = await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: _ => throw new InvalidOperationException("Callback error"),
        onDecisionMade: _ => throw new InvalidOperationException("Callback error"));

    // Assert - should still return successfully
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  #endregion

  #region AppendAndWaitAsync<TMessage> (All Perspectives) Callback Tests

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_WithEvents_InvokesOnWaitingCallbackAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    // Track events before calling
    tracker.TrackEmittedEvent(streamId, typeof(TestEvent), Guid.NewGuid());

    // Act
    var result = await decorator.AppendAndWaitAsync(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert - onWaiting SHOULD be called because events exist
    await Assert.That(capturedWaiting).IsNotNull();
    await Assert.That(capturedWaiting!.PerspectiveType).IsNull(); // All perspectives
    await Assert.That(capturedWaiting.EventCount).IsEqualTo(1);
    await Assert.That(capturedWaiting.StreamIds.Count).IsEqualTo(1);

    // Assert - onDecisionMade SHOULD always be called
    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.PerspectiveType).IsNull(); // All perspectives
    await Assert.That(capturedDecision.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(capturedDecision.DidWait).IsTrue();
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_WithNoEvents_DoesNotInvokeOnWaitingCallbackAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker(); // Empty - no events
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    // Act - NO events tracked
    var result = await decorator.AppendAndWaitAsync(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert - onWaiting should NOT be called for NoPendingEvents
    await Assert.That(capturedWaiting).IsNull();

    // Assert - onDecisionMade SHOULD still be called
    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(capturedDecision.DidWait).IsFalse();
    await Assert.That(capturedDecision.EventsAwaited).IsEqualTo(0);
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_WithoutEventCompletionAwaiter_InvokesDecisionCallbackOnlyAsync() {
    // Arrange - NO event completion awaiter registered
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var tracker = new FakeScopedEventTracker();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter: null, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    tracker.TrackEmittedEvent(streamId, typeof(TestEvent), Guid.NewGuid());

    // Act
    var result = await decorator.AppendAndWaitAsync(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert - onWaiting NOT called because no awaiter to wait with
    await Assert.That(capturedWaiting).IsNull();

    // Assert - onDecisionMade called with Synced (can't verify either way)
    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(capturedDecision.DidWait).IsFalse();
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_WhenTimedOut_InvokesBothCallbacksAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: false); // Will timeout
    var tracker = new FakeScopedEventTracker();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    tracker.TrackEmittedEvent(streamId, typeof(TestEvent), Guid.NewGuid());

    // Act
    var result = await decorator.AppendAndWaitAsync(
        streamId,
        message,
        TimeSpan.FromMilliseconds(10),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert - onWaiting called before waiting starts
    await Assert.That(capturedWaiting).IsNotNull();

    // Assert - onDecisionMade called with timeout outcome
    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(capturedDecision.DidWait).IsTrue();
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_WithMultipleEvents_CallbackHasCorrectEventCountAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter, tracker);

    var stream1 = Guid.NewGuid();
    var stream2 = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    // Track 3 events on 2 different streams
    tracker.TrackEmittedEvent(stream1, typeof(TestEvent), Guid.NewGuid());
    tracker.TrackEmittedEvent(stream1, typeof(TestEvent), Guid.NewGuid());
    tracker.TrackEmittedEvent(stream2, typeof(TestEvent), Guid.NewGuid());

    // Act
    var result = await decorator.AppendAndWaitAsync(
        stream1,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: ctx => capturedWaiting = ctx,
        onDecisionMade: ctx => capturedDecision = ctx);

    // Assert
    await Assert.That(capturedWaiting).IsNotNull();
    await Assert.That(capturedWaiting!.EventCount).IsEqualTo(3);
    await Assert.That(capturedWaiting.StreamIds.Count).IsEqualTo(2); // 2 distinct streams

    await Assert.That(capturedDecision).IsNotNull();
    await Assert.That(capturedDecision!.EventsAwaited).IsEqualTo(3);
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_CallbackExceptionDoesNotBreakSyncAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    tracker.TrackEmittedEvent(streamId, typeof(TestEvent), Guid.NewGuid());

    // Act - callbacks throw exceptions, but sync should still work
    var result = await decorator.AppendAndWaitAsync(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        onWaiting: _ => throw new InvalidOperationException("Callback error"),
        onDecisionMade: _ => throw new InvalidOperationException("Callback error"));

    // Assert - should still return successfully
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_UsesScopedEventTrackerAccessorWhenNoTrackerProvidedAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    // Note: NO scopedEventTracker passed to constructor
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    SyncDecisionContext? capturedDecision = null;

    // Set up the ambient tracker
    var tracker = new FakeScopedEventTracker();
    ScopedEventTrackerAccessor.CurrentTracker = tracker;

    try {
      tracker.TrackEmittedEvent(streamId, typeof(TestEvent), Guid.NewGuid());

      // Act
      var result = await decorator.AppendAndWaitAsync(
          streamId,
          message,
          TimeSpan.FromSeconds(5),
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert - Should use the ambient tracker
      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.EventsAwaited).IsEqualTo(1);
      await Assert.That(capturedDecision.Outcome).IsEqualTo(SyncOutcome.Synced);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_AppendsEventBeforeWaitingAsync() {
    // Arrange
    var inner = new InMemoryEventStore();
    var syncAwaiter = new FakePerspectiveSyncAwaiter();
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, syncAwaiter, eventCompletionAwaiter, tracker);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data-append-first");

    tracker.TrackEmittedEvent(streamId, typeof(TestEvent), Guid.NewGuid());

    // Act
    await decorator.AppendAndWaitAsync(
        streamId,
        message,
        TimeSpan.FromSeconds(5));

    // Assert - Event should be appended
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in inner.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Payload.Value).IsEqualTo("test-data-append-first");
  }

  #endregion

  #region Fake Implementations

  private sealed class FakePerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();
    public SyncResult ResultToReturn { get; set; } = new(SyncOutcome.Synced, 1, TimeSpan.FromMilliseconds(10));

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) {
      throw new NotImplementedException("Not used by AppendAndWaitAsync");
    }

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) {
      throw new NotImplementedException("Not used by AppendAndWaitAsync");
    }

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      return Task.FromResult(ResultToReturn);
    }
  }

  private sealed class FakeEventCompletionAwaiter(bool completesImmediately) : IEventCompletionAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, CancellationToken cancellationToken = default) {
      return Task.FromResult(completesImmediately);
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => completesImmediately;
  }

  private sealed class FakeScopedEventTracker : IScopedEventTracker {
    private readonly List<TrackedEvent> _events = [];

    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) {
      _events.Add(new TrackedEvent(streamId, eventType, eventId));
    }

    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => _events;

    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => _events;

    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) {
      return _events.All(e => processedEventIds.Contains(e.EventId));
    }
  }

  #endregion
}
