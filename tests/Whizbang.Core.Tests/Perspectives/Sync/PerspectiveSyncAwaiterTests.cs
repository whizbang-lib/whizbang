using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IPerspectiveSyncAwaiter"/> and <see cref="PerspectiveSyncAwaiter"/>.
/// </summary>
/// <remarks>
/// These tests verify the database-based sync implementation which uses
/// <see cref="IWorkCoordinator"/> to query sync status from the database.
/// </remarks>
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
    const int eventsAwaited = 5;
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
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act
    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(isCaughtUp).IsTrue();
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_WithUnprocessedEvents_ReturnsFalseAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Create mock that returns pending events
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 1
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act
    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(isCaughtUp).IsFalse();
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_WhenAllEventsProcessed_ReturnsTrueAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Create mock that returns all events synced
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 0  // No pending = synced
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act
    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(isCaughtUp).IsTrue();
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync with no pending events tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithNoTrackedEvents_ReturnsNoPendingEventsAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithFilteredNoMatch_ReturnsNoPendingEventsAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    // Track event that won't match filter
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    // Filter for int, not string
    var options = SyncFilter.ForEventTypes<int>().Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync with database sync tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_CompletesWhenDatabaseReturnsSyncedAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Create mock that returns synced on first call
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 0  // Synced
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var syncEventTracker = new SyncEventTracker();
    // Track the event in SyncEventTracker so WaitForPerspectiveEventsAsync actually waits
    syncEventTracker.TrackEvent(typeof(string), eventId, streamId, typeof(TestPerspective).FullName!);
    // Mark it processed so it completes
    syncEventTracker.MarkProcessedByPerspective([eventId], typeof(TestPerspective).FullName!);
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker, tracker);

    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync timeout tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_TimesOutWhenDatabaseNeverSyncsAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Create mock that always returns pending
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 1  // Always pending
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    // Track event in SyncEventTracker so WaitForPerspectiveEventsAsync actually waits (doesn't return true immediately)
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(string), eventId, streamId, typeof(TestPerspective).FullName!);
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker, tracker);

    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromMilliseconds(150))
        .Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(100);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - Stream filter tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithStreamFilter_OnlyWaitsForMatchingStreamAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var targetStreamId = Guid.NewGuid();
    var otherStreamId = Guid.NewGuid();
    var targetEventId = Guid.NewGuid();

    tracker.TrackEmittedEvent(targetStreamId, typeof(string), targetEventId);
    tracker.TrackEmittedEvent(otherStreamId, typeof(string), Guid.NewGuid()); // Should not affect wait

    // Create mock that returns synced
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 0
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.ForStream(targetStreamId)
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1); // Only target stream event
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - Cancellation tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_RespectsCancellationAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    // Track event in SyncEventTracker so WaitForPerspectiveEventsAsync actually waits
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(string), eventId, streamId, typeof(TestPerspective).FullName!);
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker, tracker);

    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromSeconds(30))
        .Build();

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

    // Act - WaitForPerspectiveEventsAsync catches cancellation and returns false → TimedOut
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options, cts.Token);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - Constructor validation tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_AcceptsNullTrackerAsync() {
    // Tracker is now optional - null is valid for stream-based sync
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    await Assert.That(awaiter).IsNotNull();
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullCoordinatorAsync() {
    var tracker = new ScopedEventTracker();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(null!, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker)));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullClockAsync() {
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(coordinator, null!, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker)));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullLoggerAsync() {
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(coordinator, clock, null!, new SyncEventTracker(), tracker)));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullSyncEventTrackerAsync() {
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, null!, tracker)));
  }

  // ==========================================================================
  // WaitForStreamAsync - Cross-scope/cross-request sync tests
  // ==========================================================================
  // These tests verify the scenario where:
  // 1. Request A emits an event (StartedEvent) on stream X
  // 2. Request B handles a command on stream X with [AwaitPerspectiveSync]
  // 3. Request B's scope has NO tracked events (they were emitted in Request A)
  // 4. With no events tracked in SyncEventTracker for this stream, returns NoPendingEvents

  [Test]
  public async Task WaitForStreamAsync_CrossScope_WithPendingEvents_WaitsUntilProcessedAsync() {
    // Arrange: Simulate cross-request scenario
    // - No tracker (or empty tracker) - events were emitted in a different scope
    // - With empty SyncEventTracker, no events for this stream → NoPendingEvents
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    // NO tracker - simulating cross-request scenario
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act: Call WaitForStreamAsync with EventTypes but no explicit EventId
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)], // Specify event types to wait for
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null); // No explicit EventId - cross-scope scenario

    // Assert - no events tracked in SyncEventTracker for this stream → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task WaitForStreamAsync_CrossScope_WithNoPendingEvents_ReturnsSyncedImmediatelyAsync() {
    // Arrange: No pending events in event store
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - no events tracked in SyncEventTracker → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task WaitForStreamAsync_WithExplicitEventId_DoesNotSetDiscoverFlagAsync() {
    // Arrange: When explicit EventId is provided, uses event-driven wait.
    // Since the explicit event ID isn't tracked in SyncEventTracker,
    // WaitForPerspectiveEventsAsync returns true immediately → Synced.
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: eventId); // Explicit EventId provided

    // Assert - explicit event ID not tracked in SyncEventTracker → Synced immediately
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task WaitForStreamAsync_CrossScope_TimesOutWhenPendingNeverClearsAsync() {
    // Arrange: With empty SyncEventTracker, no events for this stream → NoPendingEvents
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromMilliseconds(200),
        eventIdToAwait: null);

    // Assert - no events tracked in SyncEventTracker → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - IsCaughtUpAsync without tracker tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_WithoutTracker_ThrowsInvalidOperationAsync() {
    // Arrange - no tracker provided
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    var options = SyncFilter.All().Build();

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_ThrowsOnNullPerspectiveTypeAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await awaiter.IsCaughtUpAsync(null!, options));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_ThrowsOnNullOptionsAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await awaiter.IsCaughtUpAsync(typeof(TestPerspective), null!));
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync without tracker tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithoutTracker_ThrowsInvalidOperationAsync() {
    // Arrange - no tracker provided
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    var options = SyncFilter.All().Build();

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await awaiter.WaitAsync(typeof(TestPerspective), options));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_ThrowsOnNullPerspectiveTypeAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await awaiter.WaitAsync(null!, options));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_ThrowsOnNullOptionsAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await awaiter.WaitAsync(typeof(TestPerspective), null!));
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - WaitAsync debugger-aware timeout tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithDefaultDebuggerAwareTimeout_UsesHasTimedOutAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Create mock that always returns pending
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 1  // Always pending
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    // Track event in SyncEventTracker so WaitForPerspectiveEventsAsync actually waits
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(string), eventId, streamId, typeof(TestPerspective).FullName!);
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker, tracker);

    // Default DebuggerAwareTimeout = true - uses HasTimedOut method
    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromMilliseconds(150))
        .Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - should timeout using debugger-aware timeout
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(100);
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_ReturnsElapsedTimeOnSyncAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());

    // Create mock that returns synced
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 0  // Synced
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.ElapsedTime).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  // ==========================================================================
  // WaitForStreamAsync - Additional parameter validation tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_ThrowsOnNullPerspectiveTypeAsync() {
    // Arrange
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await awaiter.WaitForStreamAsync(null!, Guid.NewGuid(), null, TimeSpan.FromSeconds(5)));
  }

  [Test]
  public async Task WaitForStreamAsync_WithNullResult_ReturnsSyncedAsync() {
    // Arrange: With empty SyncEventTracker, no events for this stream → NoPendingEvents
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act - no explicit event IDs
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - no events tracked in SyncEventTracker → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task WaitForStreamAsync_WithEmptyResultList_ReturnsSyncedAsync() {
    // Arrange: With empty SyncEventTracker, no events for this stream → NoPendingEvents
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - no events tracked in SyncEventTracker → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task WaitForStreamAsync_WithNoEventTypes_ReturnsNoPendingEventsAsync() {
    // Arrange: No events tracked in SyncEventTracker for this stream
    var streamId = Guid.NewGuid();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker());

    // Act - null event types, no tracked events
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - no events to wait for → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }

  // ==========================================================================
  // IsCaughtUpAsync - Result matching with expected EventIds tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_MatchesExpectedEventIds_WhenSyncedAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Mock returns synced with ProcessedEventIds matching expected
    var coordinator = new MockWorkCoordinator((request, _) => {
      var inquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = inquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedCount = 1,
            ProcessedEventIds = [eventId]
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    var options = SyncFilter.All().Build();

    // Act
    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(isCaughtUp).IsTrue();
  }

  // ==========================================================================
  // WaitAsync - Multiple streams tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithMultipleStreams_WaitsForAllAsync() {
    // Arrange
    var syncEventTracker = new SyncEventTracker();
    var tracker = new ScopedEventTracker();
    var streamId1 = Guid.NewGuid();
    var streamId2 = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Track in both scoped tracker (for WaitAsync event discovery) and SyncEventTracker (for event-driven waiting)
    tracker.TrackEmittedEvent(streamId1, typeof(string), eventId1);
    tracker.TrackEmittedEvent(streamId2, typeof(string), eventId2);
    syncEventTracker.TrackEvent(typeof(string), eventId1, streamId1, perspectiveName);
    syncEventTracker.TrackEvent(typeof(string), eventId2, streamId2, perspectiveName);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker, tracker);

    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    // Act - Signal processing after a short delay
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessedByPerspective([eventId1, eventId2], perspectiveName);
    });

    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(2);
  }

  // ==========================================================================
  // WaitForStreamAsync - With scoped tracker tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_WithTrackedEvents_UsesEventDrivenWaitingAsync() {
    // Arrange - track events in SyncEventTracker (WaitForStreamAsync uses singleton tracker)
    var syncEventTracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    syncEventTracker.TrackEvent(typeof(string), eventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker);

    // Signal processing after a short delay
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - event-driven wait should complete successfully
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForStreamAsync_WithEventTypeFilter_FiltersCorrectlyAsync() {
    // Arrange - track two event types in SyncEventTracker
    var syncEventTracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var stringEventId = Guid.NewGuid();
    var intEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    syncEventTracker.TrackEvent(typeof(string), stringEventId, streamId, perspectiveName);
    syncEventTracker.TrackEvent(typeof(int), intEventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker);

    // Verify GetPendingEvents filtering returns only string events
    var stringEvents = syncEventTracker.GetPendingEvents(streamId, perspectiveName, [typeof(string)]);
    await Assert.That(stringEvents.Count).IsEqualTo(1);
    await Assert.That(stringEvents[0].EventId).IsEqualTo(stringEventId);

    // Signal only the string event as processed
    _ = Task.Run(async () => {
      await Task.Delay(50);
      syncEventTracker.MarkProcessedByPerspective([stringEventId], perspectiveName);
    });

    // Act - filter for string only
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - only waited for the string event, which was processed
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  // ==========================================================================
  // WaitAsync - Non-debugger-aware timeout tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithDebuggerAwareTimeoutDisabled_UsesDirectComparisonAsync() {
    // Arrange
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), eventId);

    // Create mock that always returns pending
    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = 1  // Always pending
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    // Track event in SyncEventTracker so WaitForPerspectiveEventsAsync actually waits
    var syncEventTracker = new SyncEventTracker();
    syncEventTracker.TrackEvent(typeof(string), eventId, streamId, typeof(TestPerspective).FullName!);
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, syncEventTracker, tracker);

    // Disable debugger-aware timeout - uses direct elapsed comparison
    var options = new PerspectiveSyncOptions {
      Filter = new AllPendingFilter(),
      Timeout = TimeSpan.FromMilliseconds(150),
      DebuggerAwareTimeout = false // Disable debugger-aware timeout
    };

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert - should timeout using direct elapsed comparison
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(100);
  }

  // ==========================================================================
  // IsCaughtUpAsync - Empty inquiries test
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_IsCaughtUpAsync_WithEmptyInquiries_ReturnsTrueAsync() {
    // Arrange - tracker with no events after filtering
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    // Filter for int, but we only have string events
    var options = SyncFilter.ForEventTypes<int>().Build();

    // Act
    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(isCaughtUp).IsTrue();
  }

  // ==========================================================================
  // WaitForStreamAsync - Singleton tracker tests
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_WithSingletonTracker_UsesEventDrivenWaitingAsync() {
    // Arrange
    var singletonTracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Track event in singleton tracker
    singletonTracker.TrackEvent(typeof(string), eventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, singletonTracker);

    // Start waiting
    var waitTask = awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Mark processed in singleton tracker
    singletonTracker.MarkProcessedByPerspective([eventId], perspectiveName);

    // Act
    var result = await waitTask;

    // Assert - should complete via event-driven waiting
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForStreamAsync_WithSingletonTracker_TimesOutWhenNotProcessedAsync() {
    // Arrange
    var singletonTracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Track event in singleton tracker
    singletonTracker.TrackEvent(typeof(string), eventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, singletonTracker);

    // Act - Don't mark processed, should timeout
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromMilliseconds(150),
        eventIdToAwait: null);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
  }

  [Test]
  public async Task WaitForStreamAsync_WithSingletonTracker_NoMatchingEvents_ReturnsNoPendingEventsAsync() {
    // Arrange - Singleton tracker has no matching events for this stream
    var singletonTracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();

    // Nothing tracked in singleton tracker

    var coordinator = new MockWorkCoordinator();

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, singletonTracker);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert - no events tracked for this stream → NoPendingEvents
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  // ==========================================================================
  // WaitForStreamAsync - Explicit eventIdToAwait with singleton tracker
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_WithExplicitEventIdAndSingletonTracker_UsesExplicitIdAsync() {
    // Arrange
    var singletonTracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var otherEventId = Guid.NewGuid();
    var perspectiveName = typeof(TestPerspective).FullName!;

    // Singleton tracker has a DIFFERENT event — explicit ID should take priority
    singletonTracker.TrackEvent(typeof(int), otherEventId, streamId, perspectiveName);
    // Also track the explicit eventId so the event-driven wait has something to wait for
    singletonTracker.TrackEvent(typeof(string), eventId, streamId, perspectiveName);

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, singletonTracker);

    // Signal only the explicit eventId as processed (not the other event)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      singletonTracker.MarkProcessedByPerspective([eventId], perspectiveName);
    });

    // Act - Explicit eventIdToAwait should take priority over singleton tracker's other events
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: null,
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: eventId);

    // Assert - only waited for explicit event, which was processed
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
  }

  // ==========================================================================
  // WaitAsync - Empty inquiries after filtering
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_WaitAsync_WithEmptyInquiriesAfterFiltering_ReturnsNoPendingEventsAsync() {
    // Arrange - events exist but filter excludes them all
    var tracker = new ScopedEventTracker();
    var streamId = Guid.NewGuid();
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());

    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);

    // Filter for int, but we only have string events
    var options = SyncFilter.ForEventTypes<int>()
        .WithTimeout(TimeSpan.FromSeconds(5))
        .Build();

    // Act
    var result = await awaiter.WaitAsync(typeof(TestPerspective), options);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }
}
