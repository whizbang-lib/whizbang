using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for the callback functionality in LocalInvokeAndSyncAsync methods.
/// These tests verify:
/// - onWaiting is called ONLY when actual waiting occurs
/// - onWaiting is NOT called for NoPendingEvents outcomes
/// - onDecisionMade is ALWAYS called regardless of outcome
/// - Context values are correct
/// </summary>
/// <docs>core-concepts/dispatcher#local-invoke-and-sync</docs>
[Category("Dispatcher")]
[Category("Sync")]
[Category("Callbacks")]
[NotInParallel]
public sealed class DispatcherLocalInvokeAndSyncCallbackTests {
  // Test messages
  public record CallbackTestCommand(string Data);
  public record CallbackTestCommandWithResult(Guid Id);
  public record CallbackTestResult(Guid Id);

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithEvents_InvokesOnWaitingCallbackAsync() {
    // Arrange
    var awaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);
    var command = new CallbackTestCommand("test");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          onWaiting: ctx => capturedWaiting = ctx,
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert - onWaiting SHOULD be called because events exist
      await Assert.That(capturedWaiting).IsNotNull();
      await Assert.That(capturedWaiting!.PerspectiveType).IsNull(); // All perspectives
      await Assert.That(capturedWaiting.EventCount).IsEqualTo(1);
      await Assert.That(capturedWaiting.StreamIds.Count).IsEqualTo(1);

      // Assert - onDecisionMade SHOULD always be called
      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.Synced);
      await Assert.That(capturedDecision.DidWait).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithNoEvents_DoesNotInvokeOnWaitingCallbackAsync() {
    // Arrange
    var awaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker(); // Empty - no events
    var dispatcher = _createDispatcher(awaiter);
    var command = new CallbackTestCommand("test");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      // Act - NO events tracked
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          onWaiting: ctx => capturedWaiting = ctx,
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert - onWaiting should NOT be called for NoPendingEvents
      await Assert.That(capturedWaiting).IsNull();

      // Assert - onDecisionMade SHOULD still be called
      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
      await Assert.That(capturedDecision.DidWait).IsFalse();
      await Assert.That(capturedDecision.EventsAwaited).IsEqualTo(0);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WhenTimedOut_InvokesBothCallbacksAsync() {
    // Arrange
    var awaiter = new FakeEventCompletionAwaiter(completesImmediately: false); // Will timeout
    var tracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);
    var command = new CallbackTestCommand("test");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          timeout: TimeSpan.FromMilliseconds(10),
          onWaiting: ctx => capturedWaiting = ctx,
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert - onWaiting called before waiting starts
      await Assert.That(capturedWaiting).IsNotNull();

      // Assert - onDecisionMade called with timeout outcome
      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.TimedOut);
      await Assert.That(capturedDecision.DidWait).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithResult_CallbacksInvokedAsync() {
    // Arrange
    var awaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);
    var command = new CallbackTestCommandWithResult(Guid.NewGuid());

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync<CallbackTestCommandWithResult, CallbackTestResult>(
          command,
          onWaiting: ctx => capturedWaiting = ctx,
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(capturedWaiting).IsNotNull();
      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.Synced);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_CallbackExceptionDoesNotBreakSyncAsync() {
    // Arrange
    var awaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);
    var command = new CallbackTestCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act - callbacks throw exceptions, but sync should still work
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          onWaiting: _ => throw new InvalidOperationException("Callback error"),
          onDecisionMade: _ => throw new InvalidOperationException("Callback error"));

      // Assert - should still return successfully
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithMultipleEvents_CallbackHasCorrectEventCountAsync() {
    // Arrange
    var awaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var tracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);
    var command = new CallbackTestCommand("test");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      // Track 3 events on 2 different streams
      var stream1 = Guid.NewGuid();
      var stream2 = Guid.NewGuid();
      tracker.TrackEmittedEvent(stream1, typeof(object), Guid.NewGuid());
      tracker.TrackEmittedEvent(stream1, typeof(object), Guid.NewGuid());
      tracker.TrackEmittedEvent(stream2, typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          onWaiting: ctx => capturedWaiting = ctx,
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert
      await Assert.That(capturedWaiting).IsNotNull();
      await Assert.That(capturedWaiting!.EventCount).IsEqualTo(3);
      await Assert.That(capturedWaiting.StreamIds.Count).IsEqualTo(2); // 2 distinct streams

      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.EventsAwaited).IsEqualTo(3);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithoutAwaiter_InvokesDecisionCallbackWithoutWaitingAsync() {
    // Arrange - NO event completion awaiter registered
    var tracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter: null);
    var command = new CallbackTestCommand("test");

    SyncWaitingContext? capturedWaiting = null;
    SyncDecisionContext? capturedDecision = null;

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          onWaiting: ctx => capturedWaiting = ctx,
          onDecisionMade: ctx => capturedDecision = ctx);

      // Assert - onWaiting NOT called because no awaiter to wait with
      await Assert.That(capturedWaiting).IsNull();

      // Assert - onDecisionMade called with Synced (can't verify either way)
      await Assert.That(capturedDecision).IsNotNull();
      await Assert.That(capturedDecision!.Outcome).IsEqualTo(SyncOutcome.Synced);
      await Assert.That(capturedDecision.DidWait).IsFalse();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  // Helper to create dispatcher
  private static IDispatcher _createDispatcher(IEventCompletionAwaiter? eventCompletionAwaiter) {
    var services = new ServiceCollection();

    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    services.AddReceptors();

    if (eventCompletionAwaiter != null) {
      services.AddSingleton(eventCompletionAwaiter);
    }

    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Test receptors
  public class CallbackTestCommandReceptor : IReceptor<CallbackTestCommand> {
    public ValueTask HandleAsync(CallbackTestCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  public class CallbackTestCommandWithResultReceptor : IReceptor<CallbackTestCommandWithResult, CallbackTestResult> {
    public ValueTask<CallbackTestResult> HandleAsync(CallbackTestCommandWithResult message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new CallbackTestResult(Guid.NewGuid()));
    }
  }

  // Fake implementations
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
}
