using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="RecentlyProcessedEventCache"/> — Phase H step 7 slice 3.
/// In-memory cooldown that short-circuits duplicate work-id processing in the perspective drainer
/// (cursor-flush race, orphan re-claim, MaintenanceWorker scans). Layer 2 of the step 7 fix:
/// catches duplicates before they reach the runner template's defensive idempotency guard.
/// </summary>
public class RecentlyProcessedEventCacheTests {

  private static SystemTimeProvider _fakeProvider(out FakeTimeProvider fake) {
    fake = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));
    return new SystemTimeProvider(fake);
  }

  [Test]
  public async Task WasRecentlyProcessed_NeverSeen_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_fakeProvider(out _));

    var seen = cache.WasRecentlyProcessed((Guid)TrackedGuid.NewMedo());

    await Assert.That(seen).IsFalse();
  }

  [Test]
  public async Task MarkProcessed_ThenWasRecentlyProcessed_WithinTtl_ReturnsTrueAsync() {
    var cache = new RecentlyProcessedEventCache(_fakeProvider(out _));
    var workId = (Guid)TrackedGuid.NewMedo();

    cache.MarkProcessed(workId);

    await Assert.That(cache.WasRecentlyProcessed(workId)).IsTrue();
  }

  [Test]
  public async Task MarkProcessed_PastTtl_AfterSweep_ReturnsFalseAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(5));
    var workId = (Guid)TrackedGuid.NewMedo();

    cache.MarkProcessed(workId);
    fake.Advance(TimeSpan.FromMinutes(6));
    cache.SweepExpired();

    await Assert.That(cache.WasRecentlyProcessed(workId)).IsFalse();
  }

  [Test]
  public async Task MarkProcessed_PastTtl_BeforeSweep_StillReturnsFalseAsync() {
    // Lazy expiry — even without explicit sweep, lookups past TTL must return false.
    var provider = _fakeProvider(out var fake);
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(5));
    var workId = (Guid)TrackedGuid.NewMedo();

    cache.MarkProcessed(workId);
    fake.Advance(TimeSpan.FromMinutes(6));

    await Assert.That(cache.WasRecentlyProcessed(workId)).IsFalse();
  }

  [Test]
  public async Task MarkProcessed_BatchOfIds_AllSubsequentReturnsTrueAsync() {
    var cache = new RecentlyProcessedEventCache(_fakeProvider(out _));
    var workIds = Enumerable.Range(0, 50).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    cache.MarkProcessed(workIds);

    foreach (var id in workIds) {
      await Assert.That(cache.WasRecentlyProcessed(id)).IsTrue();
    }
  }

  [Test]
  public async Task SweepExpired_RemovesExpiredEntries_KeepsLiveAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(5));
    var oldId = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(oldId);

    fake.Advance(TimeSpan.FromMinutes(4)); // still alive
    var youngId = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(youngId);

    fake.Advance(TimeSpan.FromMinutes(2)); // oldId now expired (6 min total), youngId still alive (2 min total)
    cache.SweepExpired();

    await Assert.That(cache.WasRecentlyProcessed(oldId)).IsFalse();
    await Assert.That(cache.WasRecentlyProcessed(youngId)).IsTrue();
  }

  [Test]
  public async Task Cap_EvictsOldestWhenFullAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new RecentlyProcessedEventCache(
      provider,
      ttl: TimeSpan.FromMinutes(60),  // long TTL — eviction is by cap, not expiry
      maxEntries: 100);

    // Insert 100 ids at distinct times so eviction order is deterministic.
    var ids = new Guid[110];
    for (var i = 0; i < ids.Length; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
      cache.MarkProcessed(ids[i]);
      fake.Advance(TimeSpan.FromMilliseconds(10));
    }

    // Cache holds at most maxEntries.
    await Assert.That(cache.Count).IsLessThanOrEqualTo(100);

    // The 10 oldest ids should have been evicted.
    var evicted = 0;
    for (var i = 0; i < 10; i++) {
      if (!cache.WasRecentlyProcessed(ids[i])) {
        evicted++;
      }
    }
    await Assert.That(evicted).IsGreaterThan(0)
      .Because("at least some of the oldest ids must be evicted when cap reached");

    // The newest 50 should all still be present (well under the cap headroom).
    for (var i = 60; i < ids.Length; i++) {
      await Assert.That(cache.WasRecentlyProcessed(ids[i])).IsTrue()
        .Because($"id {i} (recently inserted) should not be evicted");
    }
  }

  [Test]
  public async Task Count_ReflectsMarkAndSweepAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(5));

    await Assert.That(cache.Count).IsEqualTo(0);

    cache.MarkProcessed((Guid)TrackedGuid.NewMedo());
    cache.MarkProcessed((Guid)TrackedGuid.NewMedo());
    cache.MarkProcessed((Guid)TrackedGuid.NewMedo());
    await Assert.That(cache.Count).IsEqualTo(3);

    fake.Advance(TimeSpan.FromMinutes(6));
    cache.SweepExpired();
    await Assert.That(cache.Count).IsEqualTo(0);
  }

  [Test]
  public async Task MarkProcessed_SameIdTwice_RefreshesExpiryAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(5));
    var workId = (Guid)TrackedGuid.NewMedo();

    cache.MarkProcessed(workId);
    fake.Advance(TimeSpan.FromMinutes(4));
    cache.MarkProcessed(workId);  // refresh
    fake.Advance(TimeSpan.FromMinutes(3));

    // 7 minutes since first mark, but only 3 minutes since refresh — still alive.
    await Assert.That(cache.WasRecentlyProcessed(workId)).IsTrue();
  }
}
