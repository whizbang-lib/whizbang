using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Integration tests for <see cref="IPerspectiveSyncAwaiter.WaitForStreamAsync"/> verifying
/// the full event-driven sync pipeline: track → wait → mark processed → signal.
/// </summary>
/// <remarks>
/// These tests reproduce the pattern used in JDNext's OrchestratorAgent:
/// 1. Dispatcher cascade tracks event via <see cref="ISyncEventTracker.TrackEvent"/>
/// 2. <see cref="IPerspectiveSyncAwaiter.WaitForStreamAsync"/> queries pending events and waits
/// 3. PerspectiveWorker calls <see cref="ISyncEventTracker.MarkProcessedByPerspective"/> → signals waiter
/// </remarks>
[NotInParallel("SyncTests")]
public class WaitForStreamAsyncIntegrationTests {

  private sealed class CompletedEvent { }
  private sealed class StartedEvent { }
  private sealed class QuestionAnsweredEvent { }
  private sealed class TestProjection { }

  [Test]
  public async Task WaitForStreamAsync_WithTrackedEvent_CompletesWhenPerspectiveProcessesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestProjection).FullName!;

    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker);

    // Simulate dispatcher cascade tracking the event
    tracker.TrackEvent(typeof(StartedEvent), eventId, streamId, perspectiveName);

    // Simulate perspective worker processing after a delay
    _ = Task.Run(async () => {
      await Task.Delay(50);
      tracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestProjection),
        streamId,
        eventTypes: [typeof(StartedEvent)],
        timeout: TimeSpan.FromMilliseconds(500));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Should complete when perspective worker marks the event as processed");
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForStreamAsync_WithTrackedEvent_TimesOutWhenNeverProcessedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestProjection).FullName!;

    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker);

    // Track event but NEVER mark as processed
    tracker.TrackEvent(typeof(StartedEvent), eventId, streamId, perspectiveName);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestProjection),
        streamId,
        eventTypes: [typeof(StartedEvent)],
        timeout: TimeSpan.FromMilliseconds(200));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut)
      .Because("Event was never marked as processed");
  }

  [Test]
  public async Task WaitForStreamAsync_EventAlreadyProcessed_ReturnsNoPendingEventsAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestProjection).FullName!;

    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker);

    // Track event then immediately mark as processed BEFORE WaitForStreamAsync
    tracker.TrackEvent(typeof(StartedEvent), eventId, streamId, perspectiveName);
    tracker.MarkProcessedByPerspective([eventId], perspectiveName);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestProjection),
        streamId,
        eventTypes: [typeof(StartedEvent)],
        timeout: TimeSpan.FromMilliseconds(500));

    // Assert — event already removed from tracker, so nothing pending
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("Event was already processed before WaitForStreamAsync was called");
  }

  /// <summary>
  /// KEY TEST: Reproduces the OrchestratorAgent pattern where multiple events are tracked
  /// on the same stream (CompletedEvent + StartedEvent), but WaitForStreamAsync only filters
  /// for specific event types (StartedEvent + QuestionAnsweredEvent).
  /// </summary>
  [Test]
  public async Task WaitForStreamAsync_MultipleEventsOnStream_WaitsOnlyForFilteredTypesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var completedEventId = Guid.NewGuid();
    var startedEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestProjection).FullName!;

    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker);

    // Both events tracked (simulates cascade of CompleteCommand then StartCommand)
    tracker.TrackEvent(typeof(CompletedEvent), completedEventId, streamId, perspectiveName);
    tracker.TrackEvent(typeof(StartedEvent), startedEventId, streamId, perspectiveName);

    // Only mark StartedEvent as processed (simulates perspective worker processing it)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      tracker.MarkProcessedByPerspective([startedEventId], perspectiveName);
    });

    // Act — filter only for StartedEvent and QuestionAnsweredEvent
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestProjection),
        streamId,
        eventTypes: [typeof(StartedEvent), typeof(QuestionAnsweredEvent)],
        timeout: TimeSpan.FromMilliseconds(500));

    // Assert — should complete because StartedEvent was processed (CompletedEvent doesn't matter)
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Only StartedEvent is in the filter, and it was marked as processed");
    await Assert.That(result.EventsAwaited).IsEqualTo(1)
      .Because("Only the StartedEvent matched the type filter");
  }

  /// <summary>
  /// Tests that when BOTH filtered event types are tracked, WaitForStreamAsync waits for ALL of them.
  /// </summary>
  [Test]
  public async Task WaitForStreamAsync_MultipleFilteredEventsTracked_WaitsForAllAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var startedEventId = Guid.NewGuid();
    var questionEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestProjection).FullName!;

    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker);

    // Both filtered event types are tracked
    tracker.TrackEvent(typeof(StartedEvent), startedEventId, streamId, perspectiveName);
    tracker.TrackEvent(typeof(QuestionAnsweredEvent), questionEventId, streamId, perspectiveName);

    // Mark StartedEvent first, then QuestionAnsweredEvent after delay
    _ = Task.Run(async () => {
      await Task.Delay(30);
      tracker.MarkProcessedByPerspective([startedEventId], perspectiveName);
      await Task.Delay(30);
      tracker.MarkProcessedByPerspective([questionEventId], perspectiveName);
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestProjection),
        streamId,
        eventTypes: [typeof(StartedEvent), typeof(QuestionAnsweredEvent)],
        timeout: TimeSpan.FromMilliseconds(500));

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Both filtered events were marked as processed");
    await Assert.That(result.EventsAwaited).IsEqualTo(2);
  }

  /// <summary>
  /// Tests using explicit eventIdToAwait parameter (cross-scope sync pattern).
  /// </summary>
  [Test]
  public async Task WaitForStreamAsync_WithExplicitEventId_CompletesWhenProcessedAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestProjection).FullName!;

    var tracker = new SyncEventTracker();
    var awaiter = new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        tracker);

    // Track the event (would normally be done by SyncTrackingEventStoreDecorator)
    tracker.TrackEvent(typeof(StartedEvent), eventId, streamId, perspectiveName);

    // Simulate perspective worker
    _ = Task.Run(async () => {
      await Task.Delay(50);
      tracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act — pass explicit eventIdToAwait (cross-scope pattern)
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestProjection),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromMilliseconds(500),
        eventIdToAwait: eventId);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }
}
