using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the activity-triggered eviction + <see cref="PerspectiveCursorCache.OnStreamsEvicted"/>
/// hook semantics added in v0.740.0-alpha.1. The hook is how paired caches (notably
/// PerspectiveWorker's intra-pod stream-affinity gate dictionary) stay in sync without each
/// running their own time-based sweep over the same stream ids.
/// </summary>
[Category("Unit")]
[Category("Workers")]
public class PerspectiveCursorCacheEvictionHookTests {

  private static PerspectiveStreamAffinityOptions _aggressiveSweepOptions() => new() {
    IdleEvictionWindow = TimeSpan.FromMilliseconds(1),
    SweepInterval = TimeSpan.FromMilliseconds(1)
  };

  [Test]
  public async Task RunSweepNowForTests_IdleStreamEvicted_RaisesEventWithStreamIdAsync() {
    var cache = new PerspectiveCursorCache(_aggressiveSweepOptions());
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    cache.Set(streamA, "TestPerspective", Guid.NewGuid());
    cache.Set(streamB, "TestPerspective", Guid.NewGuid());

    var evictedReports = new List<IReadOnlyList<Guid>>();
    cache.OnStreamsEvicted += list => evictedReports.Add(list);

    // Wait past the idle window so both streams are eligible. Using TUnit's wall-clock here
    // is acceptable because the idle window is in milliseconds — pinned by the test options.
    await Task.Delay(50);

    var evicted = cache.RunSweepNowForTests();

    await Assert.That(evicted.Count).IsEqualTo(2)
      .Because("Both streams were idle past IdleEvictionWindow, sweep must remove both.");
    await Assert.That(evicted).Contains(streamA);
    await Assert.That(evicted).Contains(streamB);

    // Hook is NOT called by RunSweepNowForTests — that's the test path, returns the list directly.
    // The activity-triggered path raises the event; verify next.
    await Assert.That(evictedReports.Count).IsEqualTo(0)
      .Because("RunSweepNowForTests intentionally bypasses the hook so tests can decide when to wire subscribers.");
  }

  [Test]
  public async Task ActivityTriggeredSweep_FiresOnStreamsEvictedHook_WithEvictedIdsAsync() {
    var cache = new PerspectiveCursorCache(_aggressiveSweepOptions());
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    var evictedReports = new List<IReadOnlyList<Guid>>();
    cache.OnStreamsEvicted += list => evictedReports.Add(list);

    cache.Set(streamA, "TestPerspective", Guid.NewGuid());
    cache.Set(streamB, "TestPerspective", Guid.NewGuid());

    // Idle past the window, then trigger a new activity — the touch path runs the sweep.
    await Task.Delay(50);

    var streamC = Guid.NewGuid();
    cache.Set(streamC, "TestPerspective", Guid.NewGuid());

    await Assert.That(evictedReports.Count)
      .IsEqualTo(1)
      .Because("The activity on streamC must trigger one sweep that evicts streamA + streamB and raises the hook once.");
    var reported = evictedReports[0];
    await Assert.That(reported).Contains(streamA);
    await Assert.That(reported).Contains(streamB);
    await Assert.That(reported.Count).IsEqualTo(2);

    // streamC must NOT be in the evicted set — its activity is what triggered the sweep.
    await Assert.That(reported).DoesNotContain(streamC);
  }

  [Test]
  public async Task RecentActivity_ProtectsStreamFromEvictionAsync() {
    var cache = new PerspectiveCursorCache(new PerspectiveStreamAffinityOptions {
      IdleEvictionWindow = TimeSpan.FromMilliseconds(100),
      SweepInterval = TimeSpan.FromMilliseconds(1)
    });
    var streamHot = Guid.NewGuid();
    var streamCold = Guid.NewGuid();

    cache.Set(streamHot, "TestPerspective", Guid.NewGuid());
    cache.Set(streamCold, "TestPerspective", Guid.NewGuid());

    await Task.Delay(60);
    cache.Set(streamHot, "TestPerspective", Guid.NewGuid()); // keep hot
    await Task.Delay(60); // streamCold is now > 100ms idle, streamHot is ~60ms

    var evicted = cache.RunSweepNowForTests();

    await Assert.That(evicted).Contains(streamCold)
      .Because("Cold stream past idle window must evict.");
    await Assert.That(evicted).DoesNotContain(streamHot)
      .Because("Hot stream re-touched within the idle window must survive.");
  }

  [Test]
  public async Task SubscriberThrows_OtherSubscribersStillInvokedAsync() {
    var cache = new PerspectiveCursorCache(_aggressiveSweepOptions());
    var streamA = Guid.NewGuid();
    cache.Set(streamA, "TestPerspective", Guid.NewGuid());

    var goodSubscriberCalled = false;
    cache.OnStreamsEvicted += _ => throw new InvalidOperationException("intentional");
    cache.OnStreamsEvicted += _ => goodSubscriberCalled = true;

    await Task.Delay(50);
    cache.Set(Guid.NewGuid(), "TestPerspective", Guid.NewGuid()); // trigger sweep

    await Assert.That(goodSubscriberCalled)
      .IsTrue()
      .Because("A throwing subscriber must not prevent later subscribers from being invoked — " +
               "the cache catches and swallows so the eviction pass stays healthy.");
  }

  [Test]
  public async Task InvalidateStream_AlsoRemovesActivityTrackerEntryAsync() {
    var cache = new PerspectiveCursorCache(_aggressiveSweepOptions());
    var streamId = Guid.NewGuid();
    cache.Set(streamId, "TestPerspective", Guid.NewGuid());

    cache.InvalidateStream(streamId);

    // After InvalidateStream the stream is gone; touch a different stream to drive a sweep
    // and confirm the invalidated stream is NOT in the evicted-reports list (because there's
    // nothing left to evict for it).
    var evictedReports = new List<IReadOnlyList<Guid>>();
    cache.OnStreamsEvicted += list => evictedReports.Add(list);

    await Task.Delay(50);
    cache.Set(Guid.NewGuid(), "TestPerspective", Guid.NewGuid());

    // The sweep may fire and find nothing to evict, or fire with the just-set stream once
    // it ages out — either way the invalidated stream should never appear in any report.
    foreach (var report in evictedReports) {
      await Assert.That(report).DoesNotContain(streamId)
        .Because("InvalidateStream removes the activity-tracker entry, so the sweep can't surface it.");
    }
  }
}
