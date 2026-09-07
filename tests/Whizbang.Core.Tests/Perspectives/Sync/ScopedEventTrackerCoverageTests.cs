using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Targeted coverage for <see cref="ScopedEventTracker"/>'s filter-matching default arm — reached
/// only by a <see cref="SyncFilterNode"/> subtype outside the six built-in filter kinds the
/// broader <see cref="ScopedEventTrackerTests"/> suite exercises. <see cref="SyncFilterNode"/> is
/// <c>public abstract</c> (not <c>sealed</c>), so the filter tree is an open extension point, not
/// a closed union — this test supplies the kind of custom node an external consumer could define.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/ScopedEventTracker.cs</code-under-test>
public class ScopedEventTrackerCoverageTests {

  private sealed record _unrecognizedFilter : SyncFilterNode;

  [Test]
  public async Task GetEmittedEvents_UnrecognizedFilterNodeKind_MatchesNothingRatherThanThrowingAsync() {
    // A custom SyncFilterNode subtype nobody wired matching logic for must not crash the perspective
    // that emitted the event, and must not be silently treated as "matches everything" either —
    // either extreme is wrong: one breaks the emitting perspective, the other lets a waiter resolve
    // on events it was never actually told to wait for.
    var tracker = new ScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var events = tracker.GetEmittedEvents(new _unrecognizedFilter());

    await Assert.That(events).IsEmpty()
      .Because("an unrecognized filter kind must match nothing — the safe default for an unimplemented predicate");
  }

  [Test]
  public async Task AreAllProcessed_UnrecognizedFilterNodeKind_TreatsItAsNoEventsToWaitForAsync() {
    var tracker = new ScopedEventTracker();
    tracker.TrackEmittedEvent(Guid.NewGuid(), typeof(string), Guid.NewGuid());

    var result = tracker.AreAllProcessed(new _unrecognizedFilter(), new HashSet<Guid>());

    await Assert.That(result).IsTrue()
      .Because("zero matching events means nothing to wait for, even when the filter kind itself is unrecognized");
  }
}
