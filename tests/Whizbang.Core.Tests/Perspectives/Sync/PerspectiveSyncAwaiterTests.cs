using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IPerspectiveSyncAwaiter"/> and <see cref="PerspectiveSyncAwaiter"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class PerspectiveSyncAwaiterTests {
  // Dummy perspective type for testing
  private sealed class TestPerspective { }

  // ==========================================================================
  // SyncOutcome enum tests
  // ==========================================================================

  [Test]
  public async Task SyncOutcome_HasExpectedValuesAsync() {
    await Assert.That(Enum.IsDefined(SyncOutcome.Synced)).IsTrue();
    await Assert.That(Enum.IsDefined(SyncOutcome.TimedOut)).IsTrue();
    await Assert.That(Enum.IsDefined(SyncOutcome.NoPendingEvents)).IsTrue();
  }

  // ==========================================================================
  // SyncResult record tests
  // ==========================================================================

  [Test]
  public async Task SyncResult_StoresAllPropertiesAsync() {
    var outcome = SyncOutcome.Synced;
    var eventsAwaited = 5;
    var elapsed = TimeSpan.FromMilliseconds(100);

    var result = new SyncResult(outcome, eventsAwaited, elapsed);

    await Assert.That(result.Outcome).IsEqualTo(outcome);
    await Assert.That(result.EventsAwaited).IsEqualTo(eventsAwaited);
    await Assert.That(result.ElapsedTime).IsEqualTo(elapsed);
  }

  [Test]
  public async Task SyncResult_IsValueTypeAsync() {
    var result = new SyncResult(SyncOutcome.Synced, 0, TimeSpan.Zero);

    await Assert.That(result.GetType().IsValueType).IsTrue();
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - IsCaughtUpAsync tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_WithNoTrackedEvents_ReturnsTrueAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    var options = SyncFilter.All().Local().Build();

    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    await Assert.That(isCaughtUp).IsTrue();
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_WithUnprocessedEvents_ReturnsFalseAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    // Track an event that hasn't been processed
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var options = SyncFilter.All().Local().Build();

    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    await Assert.That(isCaughtUp).IsFalse();
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync with no pending events tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithNoTrackedEvents_ReturnsNoPendingEventsAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    var options = SyncFilter.All().Local().Build();

    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithFilteredNoMatch_ReturnsNoPendingEventsAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    // Track event that won't match filter
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var options = SyncFilter.ForEventTypes<int>().Local().Build(); // Filter for int, not string

    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync with signal tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_CompletesWhenSignaledAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    var options = SyncFilter.All()
        .Local()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    // Start waiting in background
    var waitTask = awaiter.WaitAsync(typeof(TestPerspective), options);

    // Signal that the event was processed
    await Task.Delay(50); // Give time for subscription to be set up
    signaler.SignalCheckpointUpdated(typeof(TestPerspective), streamId, eventId);

    var result = await waitTask;

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WaitsForAllMatchingEventsAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId1);
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId2);

    var options = SyncFilter.All()
        .Local()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    var waitTask = awaiter.WaitAsync(typeof(TestPerspective), options);

    await Task.Delay(50);

    // Signal first event
    signaler.SignalCheckpointUpdated(typeof(TestPerspective), streamId, eventId1);
    await Task.Delay(50);

    // Signal second event
    signaler.SignalCheckpointUpdated(typeof(TestPerspective), streamId, eventId2);

    var result = await waitTask;

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(2);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync timeout tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_TimesOutWhenNotSignaledAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var options = SyncFilter.All()
        .Local()
        .WithTimeout(TimeSpan.FromMilliseconds(100))
        .Build();

    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(90);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - Stream filter tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithStreamFilter_OnlyWaitsForMatchingStreamAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    var targetStreamId = Guid.NewGuid();
    var otherStreamId = Guid.NewGuid();
    var targetEventId = Guid.NewGuid();

    tracker.TrackEmittedEvent(targetStreamId, typeof(string), targetEventId);
    tracker.TrackEmittedEvent(otherStreamId, typeof(string), Guid.NewGuid()); // Should not affect wait

    var options = SyncFilter.ForStream(targetStreamId)
        .Local()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    var waitTask = awaiter.WaitAsync(typeof(TestPerspective), options);

    await Task.Delay(50);
    signaler.SignalCheckpointUpdated(typeof(TestPerspective), targetStreamId, targetEventId);

    var result = await waitTask;

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - Cancellation tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_RespectsСancellationAsync() {
    var tracker = new ScopedEventTracker();
    var signaler = new LocalSyncSignaler();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(tracker, signaler, clock);

    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var options = SyncFilter.All()
        .Local()
        .WithTimeout(TimeSpan.FromSeconds(30))
        .Build();

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await awaiter.WaitAsync(typeof(TestPerspective), options, cts.Token));
  }
}
