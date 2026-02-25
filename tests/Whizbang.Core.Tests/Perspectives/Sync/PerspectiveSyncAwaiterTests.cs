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
    // Arrange
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    tracker.TrackEmittedEvent(streamId, typeof(string), Guid.NewGuid());

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

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
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    // Create mock that always returns pending (simulates waiting)
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
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker);

    var options = SyncFilter.All()
        .WithTimeout(TimeSpan.FromSeconds(30))
        .Build();

    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await awaiter.WaitAsync(typeof(TestPerspective), options, cts.Token));
  }

  // ==========================================================================
  // PerspectiveSyncAwaiter - Constructor validation tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_AcceptsNullTrackerAsync() {
    // Tracker is now optional - null is valid for stream-based sync
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker: null);

    await Assert.That(awaiter).IsNotNull();
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullCoordinatorAsync() {
    var tracker = new ScopedEventTracker();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(null!, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker)));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullClockAsync() {
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(coordinator, null!, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker)));
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_Constructor_ThrowsOnNullLoggerAsync() {
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

    await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await Task.FromResult(new PerspectiveSyncAwaiter(coordinator, clock, null!, tracker)));
  }

  // ==========================================================================
  // WaitForStreamAsync - Cross-scope/cross-request sync tests
  // ==========================================================================
  // These tests verify the scenario where:
  // 1. Request A emits an event (StartedEvent) on stream X
  // 2. Request B handles a command on stream X with [AwaitPerspectiveSync]
  // 3. Request B's scope has NO tracked events (they were emitted in Request A)
  // 4. The sync should discover pending events from the event store and wait

  [Test]
  public async Task WaitForStreamAsync_CrossScope_WithPendingEvents_WaitsUntilProcessedAsync() {
    // Arrange: Simulate cross-request scenario
    // - No tracker (or empty tracker) - events were emitted in a different scope
    // - SQL returns pending_count > 0 (events exist in event_store but not processed)
    var streamId = Guid.NewGuid();
    var callCount = 0;

    // First call returns pending, second call returns synced
    var coordinator = new MockWorkCoordinator((request, _) => {
      callCount++;
      // Verify DiscoverPendingFromOutbox is set when EventTypes specified but no explicit IDs
      var inquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      var pendingCount = callCount == 1 ? 1 : 0; // First call: pending, second: synced

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = inquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = pendingCount,
            ProcessedCount = callCount == 1 ? 0 : 1
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    // NO tracker - simulating cross-request scenario
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker: null);

    // Act: Call WaitForStreamAsync with EventTypes but no explicit EventId
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)], // Specify event types to wait for
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null); // No explicit EventId - cross-scope scenario

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(callCount).IsGreaterThanOrEqualTo(2); // Should have polled at least twice
  }

  [Test]
  public async Task WaitForStreamAsync_CrossScope_WithNoPendingEvents_ReturnsSyncedImmediatelyAsync() {
    // Arrange: No pending events in event store
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          StreamId = streamId,
          PendingCount = 0, // No pending events
          ProcessedCount = 1 // Already processed
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker: null);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task WaitForStreamAsync_CrossScope_SetsDiscoverPendingFromOutboxFlagAsync() {
    // Arrange: Verify that DiscoverPendingFromOutbox flag is set in the inquiry
    var streamId = Guid.NewGuid();
    SyncInquiry? capturedInquiry = null;

    var coordinator = new MockWorkCoordinator((request, _) => {
      capturedInquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = capturedInquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker: null);

    // Act
    await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)], // Has event types
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: null); // No explicit EventId

    // Assert: DiscoverPendingFromOutbox should be true when EventTypes specified but no EventIds
    await Assert.That(capturedInquiry).IsNotNull();
    await Assert.That(capturedInquiry!.DiscoverPendingFromOutbox).IsTrue();
    await Assert.That(capturedInquiry.EventTypeFilter).IsNotNull();
    await Assert.That(capturedInquiry.EventIds).IsNull(); // No explicit IDs
  }

  [Test]
  public async Task WaitForStreamAsync_WithExplicitEventId_DoesNotSetDiscoverFlagAsync() {
    // Arrange: When explicit EventId is provided, don't use discovery
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    SyncInquiry? capturedInquiry = null;

    var coordinator = new MockWorkCoordinator((request, _) => {
      capturedInquiry = request.PerspectiveSyncInquiries?.FirstOrDefault();
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        SyncInquiryResults = [
          new SyncInquiryResult {
            InquiryId = capturedInquiry?.InquiryId ?? Guid.NewGuid(),
            StreamId = streamId,
            PendingCount = 0,
            ProcessedEventIds = [eventId]
          }
        ]
      });
    });

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker: null);

    // Act
    await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromSeconds(5),
        eventIdToAwait: eventId); // Explicit EventId provided

    // Assert: DiscoverPendingFromOutbox should be false when explicit EventId is provided
    await Assert.That(capturedInquiry).IsNotNull();
    await Assert.That(capturedInquiry!.DiscoverPendingFromOutbox).IsFalse();
    await Assert.That(capturedInquiry.EventIds).IsNotNull();
    await Assert.That(capturedInquiry.EventIds).Contains(eventId);
  }

  [Test]
  public async Task WaitForStreamAsync_CrossScope_TimesOutWhenPendingNeverClearsAsync() {
    // Arrange: Events are pending forever
    var streamId = Guid.NewGuid();

    var coordinator = new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          StreamId = streamId,
          PendingCount = 1 // Always pending
        }
      ]
    }));

    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, tracker: null);

    // Act
    var result = await awaiter.WaitForStreamAsync(
        typeof(TestPerspective),
        streamId,
        eventTypes: [typeof(string)],
        timeout: TimeSpan.FromMilliseconds(200), // Short timeout
        eventIdToAwait: null);

    // Assert
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(150);
  }
}
