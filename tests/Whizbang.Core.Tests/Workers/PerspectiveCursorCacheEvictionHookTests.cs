using Microsoft.Extensions.Time.Testing;
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
///
/// <para>Deterministic time: every test uses <see cref="FakeTimeProvider"/> and advances time
/// explicitly via <c>Advance</c>. No <c>Task.Delay</c>, no wall-clock dependency — the tests
/// pass identically on any runner regardless of speed.</para>
/// </summary>
[Category("Unit")]
[Category("Workers")]
public class PerspectiveCursorCacheEvictionHookTests {

  private static PerspectiveStreamAffinityOptions _testOptions() => new() {
    IdleEvictionWindow = TimeSpan.FromMinutes(15),
    SweepInterval = TimeSpan.FromMinutes(1)
  };

  [Test]
  public async Task RunSweepNowForTests_IdleStreamEvicted_RaisesEventWithStreamIdAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var cache = new PerspectiveCursorCache(_testOptions(), clock);
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    cache.Set(streamA, "TestPerspective", Guid.NewGuid());
    cache.Set(streamB, "TestPerspective", Guid.NewGuid());

    var evictedReports = new List<IReadOnlyList<Guid>>();
    cache.OnStreamsEvicted += list => evictedReports.Add(list);

    // Advance past IdleEvictionWindow (15 min). Both streams are now eligible.
    clock.Advance(TimeSpan.FromMinutes(16));

    var evicted = cache.RunSweepNowForTests();

    await Assert.That(evicted.Count).IsEqualTo(2)
      .Because("Both streams were idle past IdleEvictionWindow, sweep must remove both.");
    await Assert.That(evicted).Contains(streamA);
    await Assert.That(evicted).Contains(streamB);

    // Hook is NOT called by RunSweepNowForTests — that's the test path, returns the list directly.
    await Assert.That(evictedReports.Count).IsEqualTo(0)
      .Because("RunSweepNowForTests intentionally bypasses the hook so tests can decide when to wire subscribers.");
  }

  [Test]
  public async Task ActivityTriggeredSweep_FiresOnStreamsEvictedHook_WithEvictedIdsAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var cache = new PerspectiveCursorCache(_testOptions(), clock);
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    var evictedReports = new List<IReadOnlyList<Guid>>();
    cache.OnStreamsEvicted += list => evictedReports.Add(list);

    cache.Set(streamA, "TestPerspective", Guid.NewGuid());
    cache.Set(streamB, "TestPerspective", Guid.NewGuid());

    // Advance past IdleEvictionWindow AND SweepInterval, then trigger a new activity — the
    // touch path runs the sweep.
    clock.Advance(TimeSpan.FromMinutes(16));

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
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var cache = new PerspectiveCursorCache(_testOptions(), clock);
    var streamHot = Guid.NewGuid();
    var streamCold = Guid.NewGuid();

    cache.Set(streamHot, "TestPerspective", Guid.NewGuid());
    cache.Set(streamCold, "TestPerspective", Guid.NewGuid());

    // Advance 10 min — neither stream is yet idle past the 15-min window.
    clock.Advance(TimeSpan.FromMinutes(10));
    cache.Set(streamHot, "TestPerspective", Guid.NewGuid()); // keep hot — re-touched at t=10m
    // Advance another 10 min — streamCold's last activity is at t=0 (now 20m old, past window);
    // streamHot's last activity is at t=10m (10m old, fresh).
    clock.Advance(TimeSpan.FromMinutes(10));

    var evicted = cache.RunSweepNowForTests();

    await Assert.That(evicted).Contains(streamCold)
      .Because("Cold stream past idle window must evict.");
    await Assert.That(evicted).DoesNotContain(streamHot)
      .Because("Hot stream re-touched within the idle window must survive.");
  }

  [Test]
  public async Task SubscriberThrows_OtherSubscribersStillInvokedAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var cache = new PerspectiveCursorCache(_testOptions(), clock);
    var streamA = Guid.NewGuid();
    cache.Set(streamA, "TestPerspective", Guid.NewGuid());

    var goodSubscriberCalled = false;
    cache.OnStreamsEvicted += _ => throw new InvalidOperationException("intentional");
    cache.OnStreamsEvicted += _ => goodSubscriberCalled = true;

    clock.Advance(TimeSpan.FromMinutes(16));
    cache.Set(Guid.NewGuid(), "TestPerspective", Guid.NewGuid()); // trigger sweep

    await Assert.That(goodSubscriberCalled)
      .IsTrue()
      .Because("A throwing subscriber must not prevent later subscribers from being invoked — " +
               "the cache catches and swallows so the eviction pass stays healthy.");
  }

  [Test]
  public async Task InvalidateStream_AlsoRemovesActivityTrackerEntryAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var cache = new PerspectiveCursorCache(_testOptions(), clock);
    var streamId = Guid.NewGuid();
    cache.Set(streamId, "TestPerspective", Guid.NewGuid());

    cache.InvalidateStream(streamId);

    var evictedReports = new List<IReadOnlyList<Guid>>();
    cache.OnStreamsEvicted += list => evictedReports.Add(list);

    clock.Advance(TimeSpan.FromMinutes(16));
    cache.Set(Guid.NewGuid(), "TestPerspective", Guid.NewGuid());

    foreach (var report in evictedReports) {
      await Assert.That(report).DoesNotContain(streamId)
        .Because("InvalidateStream removes the activity-tracker entry, so the sweep can't surface it.");
    }
  }
}
