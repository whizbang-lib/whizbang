using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Coverage for the "events pending but zero inquiries" edge in both
/// <see cref="PerspectiveSyncAwaiter.IsCaughtUpAsync"/> and <see cref="PerspectiveSyncAwaiter.WaitAsync"/>.
/// The internal <c>_buildSyncInquiries</c> groups the tracked events by stream, so it can only
/// produce zero inquiries from a non-empty event list if the tracker's reported
/// <see cref="IReadOnlyCollection{T}.Count"/> and its actual enumeration disagree — the one real
/// way that happens is a race between reading the count and enumerating the same live,
/// concurrently-mutated tracker. A sync awaiter's job is to tell a caller a projection has caught
/// up, and returning early is worse than returning slowly (the caller would read a model that has
/// not been written yet) — but here there is nothing left to wait FOR, so the correct answer is to
/// return immediately rather than block on a query the helper can never issue.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/PerspectiveSyncAwaiter.cs</code-under-test>
public class PerspectiveSyncAwaiterCoverageTests {
  private sealed class _testPerspective;

  // A stand-in for the one real way _buildSyncInquiries sees zero groups after a Count-based
  // non-empty check has already passed: Count and the actual enumeration disagree.
  private sealed class _countDisagreesWithEnumerationList : IReadOnlyList<TrackedEvent> {
    public int Count => 1;
    public TrackedEvent this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<TrackedEvent> GetEnumerator() { yield break; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
  }

  private sealed class _raceyScopedEventTracker : IScopedEventTracker {
    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) { }
    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => [];
    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => new _countDisagreesWithEnumerationList();
    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) => true;
  }

  private static PerspectiveSyncAwaiter _awaiter(IScopedEventTracker tracker) =>
    new(
      new MockWorkCoordinator((_, _) => throw new InvalidOperationException(
        "the database must never be queried when there are no inquiries to resolve")),
      new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      new SyncEventTracker(),
      tracker);

  // A one-shot status check that treated an inconsistent snapshot as "still pending" would report
  // false forever for events that were never actually there — callers polling IsCaughtUpAsync
  // would spin until their own timeout instead of seeing "nothing to wait on" on the first check.
  [Test]
  public async Task IsCaughtUpAsync_TrackerReportsEventsButEnumeratesNone_ReturnsTrueWithoutQueryingAsync() {
    var awaiter = _awaiter(new _raceyScopedEventTracker());

    var isCaughtUp = await awaiter.IsCaughtUpAsync(typeof(_testPerspective), SyncFilter.All().Build());

    await Assert.That(isCaughtUp).IsTrue();
  }

  // Same inconsistency on the blocking wait path: if this fell through to the event-driven wait
  // instead of returning immediately, a caller could block for the full sync timeout waiting on
  // zero events that were never going to arrive.
  [Test]
  public async Task WaitAsync_TrackerReportsEventsButEnumeratesNone_ReturnsNoPendingEventsWithoutBlockingAsync() {
    var awaiter = _awaiter(new _raceyScopedEventTracker());

    var result = await awaiter.WaitAsync(typeof(_testPerspective), SyncFilter.All().Build());

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsAwaited).IsEqualTo(0);
  }
}
