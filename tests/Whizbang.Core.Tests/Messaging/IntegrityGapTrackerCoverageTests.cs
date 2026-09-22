using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round-23 targets for <see cref="IntegrityGapTracker"/>'s two bounded-memory eviction
/// paths. This tracker exists to notice missing events — failing open (silently forgetting a real
/// deficit, or leaking memory until the process is killed) is far worse than the alternative of a
/// slightly noisier warning. Both caps are only meaningfully tested by actually exceeding them; a
/// wrong eviction target (newest instead of oldest) or a cap that stops tracking instead of resetting
/// would either forget a fresh, still-real gap or permanently silence a genuine later regression.
/// </summary>
public class IntegrityGapTrackerCoverageTests {
  [Test]
  public async Task AddPending_AtCapacity_DropsTheOldestPendingGapAsync() {
    var tracker = new IntegrityGapTracker();
    var originId = Guid.NewGuid();
    for (var i = 0; i < 1000; i++) {
      tracker.AddPending(_gap(originId, eventType: $"gap-{i}"));
    }
    // One more over the 1,000 cap: the OLDEST entry (gap-0) must be the one evicted.
    tracker.AddPending(_gap(originId, eventType: "gap-1000"));

    var remaining = tracker.TakePending(originId);

    await Assert.That(remaining.Count).IsEqualTo(1000)
      .Because("pending deficits are capped — an unbounded list under sustained gaps is a memory leak "
        + "that eventually takes the consumer down, which hides the very gaps it exists to surface");
    await Assert.That(remaining.Any(g => g.EventType == "gap-0")).IsFalse()
      .Because("overflow must drop the OLDEST pending deficit (documented eviction policy) — dropping "
        + "the newest instead would silently forget a fresh, still-real gap in favor of a stale one");
    await Assert.That(remaining.Any(g => g.EventType == "gap-1000")).IsTrue()
      .Because("the newly-added gap must survive the eviction that made room for it");
  }

  [Test]
  public async Task MarkConfirmed_AtCapacity_ResetsSoAPriorWindowCanReConfirmAsync() {
    var tracker = new IntegrityGapTracker();
    var firstGap = _gap(Guid.NewGuid(), eventType: "e", fromSeq: -1);
    await Assert.That(tracker.MarkConfirmed(firstGap)).IsTrue()
      .Because("the first confirmation of any window is always reported as new");

    for (var i = 0; i < 9_999; i++) {
      tracker.MarkConfirmed(_gap(Guid.NewGuid(), eventType: "e", fromSeq: i));
    }
    // The confirmed-windows set is now exactly at MAX_CONFIRMED_KEYS (10,000: firstGap + 9,999 more).
    // This next call must clear the set FIRST, then record its own key.
    var overflowResult = tracker.MarkConfirmed(_gap(Guid.NewGuid(), eventType: "e", fromSeq: 9_999));
    await Assert.That(overflowResult).IsTrue()
      .Because("the call that trips the cap is still a genuine first confirmation of ITS OWN window");

    // The reset is only observable by re-confirming a window recorded BEFORE the reset: if the set
    // had merely stopped growing (capped without clearing) instead of resetting, this would return
    // false (already confirmed) — the exact once-per-window suppression this method exists to provide.
    var reConfirmedAfterReset = tracker.MarkConfirmed(firstGap);

    await Assert.That(reConfirmedAfterReset).IsTrue()
      .Because("bounded means the set RESETS at the cap (worst case a long-lived gap re-warns once) — "
        + "if it silently stopped tracking instead, a genuine LATER regression of an old window would "
        + "never warn again; if it grew unbounded instead, that is the same memory leak the cap exists "
        + "to prevent");
  }

  private static IntegrityGapTracker.PendingGap _gap(
      Guid originServiceId, string eventType = "TestEvent", long fromSeq = 0, long toSeq = 100) =>
    new() {
      OriginServiceId = originServiceId,
      OriginServiceName = "test-origin",
      EventType = eventType,
      FromCommitSequence = fromSeq,
      ToCommitSequence = toSeq,
      ExpectedCount = 1
    };
}
