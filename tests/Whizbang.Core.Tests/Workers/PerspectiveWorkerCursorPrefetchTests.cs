using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 26.13 — locks <c>PerspectiveWorker._hydrateCursorCacheEntry</c>, the cold-cache
/// hydration helper used by <c>_prefetchMissingDrainModeCursorsAsync</c>. Both halves of
/// <see cref="PerspectiveCursorCache"/> (event_id + commit_sequence) must be populated when
/// the cursor info has them; otherwise the inversion detector falls through to the event_id
/// path on cold caches and surfaces UUIDv7 same-millisecond false positives (a production
/// regression).
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class PerspectiveWorkerCursorPrefetchTests {

  [Test]
  public async Task HydrateCursorCacheEntry_BothPopulated_SetsBothCacheHalvesAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    const long commitSeq = 12345L;

    PerspectiveWorker._hydrateCursorCacheEntry(cache, new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "P",
      LastEventId = eventId,
      LastCommitSequence = commitSeq,
      Status = PerspectiveProcessingStatus.Completed,
    });

    await Assert.That(cache.TryGet(streamId, "P", out var cachedEventId)).IsTrue();
    await Assert.That(cachedEventId).IsEqualTo(eventId);

    await Assert.That(cache.TryGetCommitSequence(streamId, "P", out var cachedSeq)).IsTrue();
    await Assert.That(cachedSeq).IsEqualTo(commitSeq);
  }

  [Test]
  public async Task HydrateCursorCacheEntry_OnlyEventIdPopulated_LeavesCommitSequenceUnsetAsync() {
    // Pre-slice-26 cursors with last_event_id but no joined commit_sequence
    // (unstamped event, or row created before stamper landed). Worker still populates
    // event_id half; commit_sequence half stays missing so the detector knows to skip
    // the commit-sequence path until apply-success path warms it.
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    PerspectiveWorker._hydrateCursorCacheEntry(cache, new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "P",
      LastEventId = eventId,
      LastCommitSequence = null,
      Status = PerspectiveProcessingStatus.Completed,
    });

    await Assert.That(cache.TryGet(streamId, "P", out var cachedEventId)).IsTrue();
    await Assert.That(cachedEventId).IsEqualTo(eventId);

    await Assert.That(cache.TryGetCommitSequence(streamId, "P", out _)).IsFalse();
  }

  [Test]
  public async Task HydrateCursorCacheEntry_NoLastEventId_LeavesBothHalvesUnsetAsync() {
    // Cursor row exists but perspective hasn't advanced. Nothing to cache.
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    PerspectiveWorker._hydrateCursorCacheEntry(cache, new PerspectiveCursorInfo {
      StreamId = streamId,
      PerspectiveName = "P",
      LastEventId = null,
      LastCommitSequence = null,
      Status = PerspectiveProcessingStatus.Completed,
    });

    await Assert.That(cache.TryGet(streamId, "P", out _)).IsFalse();
    await Assert.That(cache.TryGetCommitSequence(streamId, "P", out _)).IsFalse();
  }
}
