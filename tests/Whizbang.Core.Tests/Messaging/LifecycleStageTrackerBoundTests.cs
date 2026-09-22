using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The tracker must bound itself, because nothing else does.
/// </summary>
/// <remarks>
/// <para>
/// This type is registered as a singleton and consumed by the receptor invoker, which only ever
/// calls <c>TryClaim</c>. Its two ways of shedding entries were both dead: <c>Release</c> had no
/// callers at all, and <c>Purge</c> had exactly one — its own unit test. The doc comment stated
/// the requirement ("Call periodically to prevent unbounded memory growth") that nothing in the
/// framework satisfied, so every claim ever made stayed resident for the life of the process.
/// </para>
/// <para>
/// The growth was proportional to messages multiplied by stages multiplied by perspectives,
/// because perspective-scoped stages key per-perspective. It therefore accelerated with fan-out
/// as well as with volume — the systems most able to generate load leaked the fastest.
/// </para>
/// <para>
/// Eviction is safe here for a specific reason: the tracker exists to stop two workers firing the
/// same stage for the same message CONCURRENTLY. Only recent history is load-bearing. Entries
/// evicted at capacity are far older than anything still in flight, so dropping them cannot
/// resurrect a double-fire. Insertion order is also touch order, since claims are added and never
/// refreshed — which is why eviction is FIFO rather than a scan for the oldest timestamp. That
/// keeps the hot path lock-free; the receptor invoker consults this for every message, and
/// serializing it behind a lock to find a minimum would cost more than the leak did.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/LifecycleStageTracker.cs</code-under-test>
[Category("Messaging")]
public class LifecycleStageTrackerBoundTests {

  [Test]
  public async Task Claims_PastCapacity_DoNotAccumulateForeverAsync() {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: 100);

    for (var i = 0; i < 5_000; i++) {
      tracker.TryClaim(Guid.NewGuid(), LifecycleStage.PostAllPerspectivesInline);
    }

    await Assert.That(tracker.TrackedClaims).IsLessThanOrEqualTo(100)
      .Because("a singleton that gains an entry per message and sheds none grows without limit "
             + "for the life of the process — the structure has to bound itself, because the "
             + "only two release paths it offered were never called by anything");
  }

  [Test]
  public async Task RecentClaims_SurviveEvictionOfOlderOnesAsync() {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: 100);
    for (var i = 0; i < 500; i++) {
      tracker.TryClaim(Guid.NewGuid(), LifecycleStage.PostAllPerspectivesInline);
    }

    var recent = Guid.NewGuid();
    tracker.TryClaim(recent, LifecycleStage.PostAllPerspectivesInline);

    await Assert.That(tracker.TryClaim(recent, LifecycleStage.PostAllPerspectivesInline)).IsFalse()
      .Because("dedup is the entire point — evicting the OLDEST is safe precisely because a "
             + "just-claimed message is the one still in flight, and it must still be refused");
  }

  [Test]
  public async Task PerspectiveScopedClaims_RemainDistinctUnderBoundingAsync() {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: 100);
    var messageId = Guid.NewGuid();

    var first = tracker.TryClaim(messageId, LifecycleStage.PostPerspectiveInline, typeof(string));
    var second = tracker.TryClaim(messageId, LifecycleStage.PostPerspectiveInline, typeof(int));

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsTrue()
      .Because("N perspectives on one event each need their own claim; collapsing them would "
             + "silently stop every perspective after the first from firing");
  }

  [Test]
  public async Task Release_StillFreesAClaimForRetryAsync() {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: 100);
    var messageId = Guid.NewGuid();

    tracker.TryClaim(messageId, LifecycleStage.PostAllPerspectivesInline);
    tracker.Release(messageId, LifecycleStage.PostAllPerspectivesInline);

    await Assert.That(tracker.TryClaim(messageId, LifecycleStage.PostAllPerspectivesInline)).IsTrue()
      .Because("bounding must not break the retry path — a released claim is reclaimable, and "
             + "the eviction bookkeeping must tolerate a key that was already removed");
  }

  [Test]
  public async Task EvictionBookkeeping_ToleratesReleasedKeysAsync() {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: 50);

    // Release half of what is claimed, so the eviction record holds keys the map no longer has.
    for (var i = 0; i < 500; i++) {
      var id = Guid.NewGuid();
      tracker.TryClaim(id, LifecycleStage.PostAllPerspectivesInline);
      if (i % 2 == 0) {
        tracker.Release(id, LifecycleStage.PostAllPerspectivesInline);
      }
    }

    await Assert.That(tracker.TrackedClaims).IsLessThanOrEqualTo(50)
      .Because("stale eviction entries must not stall the bound — if evicting a released key "
             + "counted as freeing a slot, the map would drift above capacity and leak again "
             + "at exactly the rate that retries occur");
  }

  [Test]
  public async Task Purge_StillRemovesAgedEntriesAsync() {
    var tracker = new LifecycleStageTracker(maxTrackedClaims: 100);
    tracker.TryClaim(Guid.NewGuid(), LifecycleStage.PostAllPerspectivesInline);

    tracker.Purge(TimeSpan.Zero);

    await Assert.That(tracker.TrackedClaims).IsEqualTo(0)
      .Because("Purge stays supported for callers that want age-based trimming; the capacity "
             + "bound is a floor under the leak, not a replacement for the existing API");
  }
}
