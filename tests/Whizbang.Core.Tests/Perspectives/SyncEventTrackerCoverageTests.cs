using System.Collections.Concurrent;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Targeted coverage for <see cref="SyncEventTracker"/> branches the broader
/// <see cref="SyncEventTrackerTests"/> suite doesn't reach: the race-condition "check again after
/// registering" guard inside the shared wait helper, the concurrent-cleanup TryRemove guard, and the
/// all-perspectives / event-level waiter sweeps performed by <see cref="SyncEventTracker.CleanupStaleEntries"/>.
/// Every test here constructs its own <see cref="SyncEventTracker"/> instance — the class holds no
/// static/process-global state, so nothing here needs cross-test isolation.
/// </summary>
public class SyncEventTrackerCoverageTests {
  private sealed record TestEventA;

  // ==========================================================================
  // CleanupStaleEntries must sweep ALL waiter registries, not just the
  // per-perspective one — the all-perspectives and event-level registries too.
  // ==========================================================================

  [Test]
  public async Task CleanupStaleEntries_SignalsAllPerspectivesAndEventLevelWaitersAsync() {
    // Cleanup already signals per-perspective waiters. If the all-perspectives and event-level waiter
    // registries aren't also swept, a caller using WaitForAllPerspectivesAsync or WaitForEventsAsync on
    // an entry that TTL cleanup just reclaimed would hang for its full timeout instead of being told
    // the entry is gone — stranding the caller for no reason.
    var tracker = new SyncEventTracker();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    tracker.TrackEvent(typeof(TestEventA), eventId, streamId, "P1");

    var eventWaitTask = tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));
    var allPerspectivesWaitTask = tracker.WaitForAllPerspectivesAsync([eventId], TimeSpan.FromSeconds(5));

    var removedCount = tracker.CleanupStaleEntries(TimeSpan.Zero);

    await Assert.That(removedCount).IsEqualTo(1);
    await Assert.That(await eventWaitTask).IsTrue()
      .Because("the event-level waiter registry must be swept by cleanup, not just the per-perspective one.");
    await Assert.That(await allPerspectivesWaitTask).IsTrue()
      .Because("the all-perspectives waiter registry must be swept once no perspective remains pending for the event.");
  }

  // ==========================================================================
  // Concurrent CleanupStaleEntries sweeps must never double-count (or lose)
  // an entry that another concurrent sweep already reclaimed.
  // ==========================================================================

  [Test]
  public async Task CleanupStaleEntries_ConcurrentSweeps_ReclaimEachEntryExactlyOnceAsync() {
    // Two cleanup sweeps can legitimately overlap in a running system (a scheduled TTL sweep racing a
    // manual one). Without the TryRemove guard, a losing sweep would double-count an entry the other
    // sweep already reclaimed — corrupting the returned removedCount — and could double-invoke the
    // waiter-signaling helpers for it. Every waiter must still be signaled regardless of which sweep
    // actually wins its entry.
    var tracker = new SyncEventTracker();
    const int entryCount = 200;
    var waitTasks = new Task<bool>[entryCount];

    for (var i = 0; i < entryCount; i++) {
      var eventId = Guid.NewGuid();
      tracker.TrackEvent(typeof(TestEventA), eventId, Guid.NewGuid(), "P1");
      waitTasks[i] = tracker.WaitForEventsAsync([eventId], TimeSpan.FromSeconds(5));
    }

    var barrier = new Barrier(2);
    var removedCounts = new int[2];
    var sweep1 = Task.Run(() => {
      barrier.SignalAndWait();
      removedCounts[0] = tracker.CleanupStaleEntries(TimeSpan.Zero);
    });
    var sweep2 = Task.Run(() => {
      barrier.SignalAndWait();
      removedCounts[1] = tracker.CleanupStaleEntries(TimeSpan.Zero);
    });

    await Task.WhenAll(sweep1, sweep2);
    var waitResults = await Task.WhenAll(waitTasks);

    await Assert.That(removedCounts[0] + removedCounts[1]).IsEqualTo(entryCount)
      .Because("every stale entry must be reclaimed by exactly one of the two concurrent sweeps — never both, never neither.");
    await Assert.That(waitResults.All(r => r)).IsTrue()
      .Because("every waiter must still be signaled even when the other concurrent sweep won the race for its entry.");
  }

  // ==========================================================================
  // The "check again after registering the waiter" race-condition guard inside
  // the shared wait helper: if MarkProcessed lands on another thread between
  // the two isPending checks, the waiter must self-signal instead of waiting
  // out the full timeout.
  // ==========================================================================

  [Test]
  [Timeout(120_000)]
  public async Task WaitForEventsAsync_ConcurrentMarkProcessed_NeverStrandsAWaiterAsync(
      CancellationToken cancellationToken) {
    // Losing the "check again after registering" guard would surface as intermittent multi-second
    // stalls on the waiting side under real concurrent load (a MarkProcessed call that lands in the
    // narrow window between the tracker's two pending-checks would otherwise go unnoticed until the
    // full timeout elapses) rather than a hard failure. This races thousands of fresh registrations
    // against a continuously-draining queue of concurrent MarkProcessed calls to exercise that seam —
    // hitting the exact branch depends on real thread scheduling, so this is a best-effort volume
    // attempt rather than a guaranteed single-shot reproduction.
    var tracker = new SyncEventTracker();
    const int eventCount = 20_000;
    // Deliberately a single drain thread racing the single registration thread (this test method's
    // own thread): keeping the two sides to one lane each lets their relative progress drift and
    // cross over many iterations, instead of a multi-worker drain structurally lapping the
    // registration loop and only ever hitting the trivial "already gone" fast path.
    const int workerCount = 1;

    var eventIds = new Guid[eventCount];
    var pending = new ConcurrentQueue<Guid>();
    for (var i = 0; i < eventCount; i++) {
      var eventId = Guid.NewGuid();
      eventIds[i] = eventId;
      tracker.TrackEvent(typeof(TestEventA), eventId, Guid.NewGuid(), "P1");
      pending.Enqueue(eventId);
    }

    var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() => {
      while (pending.TryDequeue(out var id)) {
        tracker.MarkProcessed([id]);
      }
    })).ToArray();

    var waitTasks = new Task<bool>[eventCount];
    for (var i = 0; i < eventCount; i++) {
      // The timeout is a detector, not a deadline, and its absolute value is deliberately far
      // larger than anything scheduling can account for. The defect this test exists to catch
      // strands a waiter for the WHOLE timeout, so any value detects it; but a value close to
      // real-world scheduling delay detects a loaded machine too. At five seconds this failed
      // under the full parallel suite while passing every time in isolation, which is the
      // signature of a test measuring the host rather than the code.
      waitTasks[i] = tracker.WaitForEventsAsync(
        [eventIds[i]], TimeSpan.FromSeconds(90), cancellationToken: cancellationToken);
    }

    await Task.WhenAll(workers);
    var results = await Task.WhenAll(waitTasks);

    // Asserted on the count as well as the predicate, so a failure says how many were stranded
    // rather than only that one was: one is the race, twenty thousand is the guard being gone.
    await Assert.That(results.Count(r => !r)).IsEqualTo(0)
      .Because("every waiter racing a concurrent MarkProcessed drain must resolve true, never be "
        + "stranded until timeout.");
  }

  // ==========================================================================
  // The post-registration re-check inside the shared wait helper
  // ==========================================================================

  // Mark*Processed signals every waiter registered at the moment it runs. A caller that checks
  // "still pending?", is beaten to the registry by that sweep, and only then registers its own
  // TCS is therefore registered against an event nobody will ever signal again. The re-check
  // after registration is the only thing that closes that window; without it the caller waits
  // out its entire timeout and reports failure for work that had in fact completed. Driving it
  // through a predicate that flips between the two calls pins the behavior without racing two
  // threads and hoping the interleaving lands.
  [Test]
  public async Task WaitForCompletion_KeyCompletesBetweenTheCheckAndTheRegistration_ResolvesImmediatelyAsync() {
    var tracker = new SyncEventTracker();
    var waiters = new ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TaskCompletionSource<bool>>>();
    var eventId = Guid.NewGuid();
    var awaiterId = Guid.NewGuid();
    var answers = 0;

    var completed = await tracker.WaitForCompletionAsync(
      waiters,
      [eventId],
      // First answer: still pending, so a waiter is registered. Second answer (the re-check that
      // runs immediately after registration): already processed — exactly the interleaving a
      // concurrent MarkProcessed produces.
      _ => Interlocked.Increment(ref answers) == 1,
      awaiterId,
      TimeSpan.FromSeconds(30),
      CancellationToken.None);

    await Assert.That(answers).IsEqualTo(2)
      .Because("the helper has to ask a second time after registering; one answer would mean the "
        + "re-check never ran and this test proves nothing");
    await Assert.That(completed).IsTrue()
      .Because("a waiter registered after the completion sweep has already passed must be signaled "
        + "by the re-check; otherwise the caller waits out its full timeout and reports failure for "
        + "work that already finished");
  }
}
