// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage tests for <see cref="ProcessedEventCache.RemoveRange"/> and the constructor
/// argument guards on <see cref="RecentlyProcessedEventCache"/>, plus its cap-eviction
/// lock re-check.
/// </summary>
[Category("Workers")]
public class ProcessedEventCacheCoverageTests {
  private static readonly TimeSpan _retentionPeriod = TimeSpan.FromMinutes(5);

  private static SystemTimeProvider _fakeProvider(out FakeTimeProvider fake) {
    fake = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));
    return new SystemTimeProvider(fake);
  }

  // ==================== ProcessedEventCache.RemoveRange ====================

  // If a bulk rewind failed to actually clear entries here, Contains would keep reporting
  // "already processed" for every event in the batch — SQL re-delivery during the rewind
  // would be silently swallowed as a duplicate instead of being replayed.
  [Test]
  public async Task RemoveRange_MultipleTrackedIds_ClearsAllForReplayAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(retentionPeriod: _retentionPeriod, observer: observer, timeProvider: time);
    var idA = Guid.CreateVersion7();
    var idB = Guid.CreateVersion7();
    var idC = Guid.CreateVersion7();
    cache.AddRange([idA, idB, idC]);

    // Act
    cache.RemoveRange([idA, idB, idC]);

    // Assert
    await Assert.That(cache.Contains(idA)).IsFalse()
      .Because("removed entries must allow replay after a rewind");
    await Assert.That(cache.Contains(idB)).IsFalse()
      .Because("removed entries must allow replay after a rewind");
    await Assert.That(cache.Contains(idC)).IsFalse()
      .Because("removed entries must allow replay after a rewind");
    await Assert.That(observer.RemovedEventIds).Contains(idA);
    await Assert.That(observer.RemovedEventIds).Contains(idB);
    await Assert.That(observer.RemovedEventIds).Contains(idC);
    await Assert.That(observer.RemovedEventIds).Count().IsEqualTo(3);
  }

  // An id that was never cached shouldn't be reported to the observer as removed — over-reporting
  // removal activity would mislead any monitoring or rewind-scope accounting built on this hook
  // into believing more events were unlocked for replay than actually were.
  [Test]
  public async Task RemoveRange_MixOfPresentAndAbsentIds_OnlyReportsPresentAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(retentionPeriod: _retentionPeriod, observer: observer, timeProvider: time);
    var present1 = Guid.CreateVersion7();
    var present2 = Guid.CreateVersion7();
    var neverAdded = Guid.CreateVersion7();
    cache.AddRange([present1, present2]);

    // Act
    cache.RemoveRange([present1, present2, neverAdded]);

    // Assert
    await Assert.That(cache.Contains(present1)).IsFalse();
    await Assert.That(cache.Contains(present2)).IsFalse();
    await Assert.That(observer.RemovedEventIds).Contains(present1);
    await Assert.That(observer.RemovedEventIds).Contains(present2);
    await Assert.That(observer.RemovedEventIds).DoesNotContain(neverAdded)
      .Because("ids that were never cached must not be reported as removed");
    await Assert.That(observer.RemovedEventIds).Count().IsEqualTo(2);
  }

  // If the observer fired even when nothing was actually removed, external systems reacting to
  // "removed" (audit trails, replay counters) would record phantom rewind activity for events
  // that were never in the cache in the first place.
  [Test]
  public async Task RemoveRange_NoMatchingIds_ObserverNotNotifiedAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(retentionPeriod: _retentionPeriod, observer: observer, timeProvider: time);
    var neverAdded1 = Guid.CreateVersion7();
    var neverAdded2 = Guid.CreateVersion7();

    // Act
    cache.RemoveRange([neverAdded1, neverAdded2]);

    // Assert
    await Assert.That(observer.RemovedEventIds).IsEmpty()
      .Because("no entries were removed, so OnEventsRemoved must not fire");
  }

  // ==================== RecentlyProcessedEventCache constructor guards ====================

  // If this guard regressed, a misconfigured maxEntries of zero (or negative) would either
  // disable capacity bounding entirely or feed the eviction batch math (maxEntries / 10)
  // nonsense, letting the cache grow without bound in a long-running drain process.
  [Test]
  public async Task Constructor_MaxEntriesLessThanOne_ThrowsArgumentOutOfRangeAsync() {
    await Assert.That(() => new RecentlyProcessedEventCache(_fakeProvider(out _), maxEntries: 0))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new RecentlyProcessedEventCache(_fakeProvider(out _), maxEntries: -1))
      .Throws<ArgumentOutOfRangeException>();
  }

  // A zero or negative TTL would make every mark expire immediately (or before it's ever
  // checked), silently disabling the whole dedup cache — real duplicates would sail straight
  // through WasRecentlyProcessed as "not recently processed."
  [Test]
  public async Task Constructor_TtlZeroOrNegative_ThrowsArgumentOutOfRangeAsync() {
    await Assert.That(() => new RecentlyProcessedEventCache(_fakeProvider(out _), ttl: TimeSpan.Zero))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new RecentlyProcessedEventCache(_fakeProvider(out _), ttl: TimeSpan.FromSeconds(-1)))
      .Throws<ArgumentOutOfRangeException>();
  }

  // The locked re-check exists because two inserts can both see the cache over its cap before
  // either takes the eviction lock. The first to take the lock evicts the overflow; the second must
  // find the overflow gone and evict nothing. Without the re-check the second would still run a
  // batch of at least one entry (Math.Max(overflow, maxEntries / 10)) and drop an entry that is
  // still inside its TTL, so that event would be fetched and applied again. The interleaving is
  // fixed by signals, not by luck: the held insert parks in OnCapacityExceeded, which runs after
  // the unlocked "count > cap" check and before the lock, while a second insert evicts.
  [Test]
  public async Task MarkProcessed_OverflowEvictedWhileInsertWaitsForLock_EvictsNothingAsync() {
    // Arrange: fill to the cap one second apart, so eviction order (oldest expiry first) is known.
    var provider = _fakeProvider(out var fake);
    const int maxEntries = 10;
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(60), maxEntries: maxEntries);
    var primed = new Guid[maxEntries];
    for (var i = 0; i < maxEntries; i++) {
      primed[i] = (Guid)TrackedGuid.New();
      cache.MarkProcessed(primed[i]);
      fake.Advance(TimeSpan.FromSeconds(1));
    }

    // RunContinuationsAsynchronously keeps the test's continuation off the held thread; inline, it
    // would await the held insert from inside it and never return.
    var heldPastUnlockedCheck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var releaseHeld = new SemaphoreSlim(0, 1);
    var observedCounts = new ConcurrentQueue<int>();
    var holdNext = 1;
    cache.OnCapacityExceeded += residentCount => {
      observedCounts.Enqueue(residentCount);
      if (Interlocked.Exchange(ref holdNext, 0) == 1) {
        heldPastUnlockedCheck.SetResult(residentCount);
        releaseHeld.Wait();
      }
    };

    var heldId = (Guid)TrackedGuid.New();
    var evictingId = (Guid)TrackedGuid.New();

    // Act
    var heldInsert = Task.Run(() => cache.MarkProcessed(heldId));
    try {
      // If the held insert finished without ever raising, there is nothing to hold; fail instead of
      // waiting forever.
      var first = await Task.WhenAny(heldPastUnlockedCheck.Task, heldInsert);
      await Assert.That(first == heldPastUnlockedCheck.Task).IsTrue()
        .Because("an insert that takes the cache past its cap must raise OnCapacityExceeded before the lock");

      // Held thread has passed the unlocked check and owns no lock. This insert takes the cache to
      // 12 and evicts the overflow of 2 (the two oldest primed ids) back down to the cap.
      cache.MarkProcessed(evictingId);
      await Assert.That(cache.Count).IsEqualTo(maxEntries)
        .Because("the second insert evicts the whole overflow while the first is held");
    } finally {
      releaseHeld.Release();
    }
    await heldInsert;

    // Assert: the held insert re-checked under the lock, found no overflow, and evicted nothing.
    await Assert.That(observedCounts.ToArray()).IsEquivalentTo([maxEntries + 1, maxEntries + 2])
      .Because("the held insert saw 11 resident entries and the evicting insert saw 12");
    await Assert.That(cache.Count).IsEqualTo(maxEntries)
      .Because("the held insert found the overflow already evicted and must not run a batch of its own");
    await Assert.That(cache.WasRecentlyProcessed(primed[0])).IsFalse();
    await Assert.That(cache.WasRecentlyProcessed(primed[1])).IsFalse();
    for (var i = 2; i < maxEntries; i++) {
      await Assert.That(cache.WasRecentlyProcessed(primed[i])).IsTrue()
        .Because($"primed id {i} is inside its TTL and no eviction was owed when the held insert ran");
    }
    await Assert.That(cache.WasRecentlyProcessed(heldId)).IsTrue();
    await Assert.That(cache.WasRecentlyProcessed(evictingId)).IsTrue();
  }

  // A stress check of the same lock under real contention: whatever interleaving the scheduler
  // picks, concurrent inserts racing the cap eviction never leave the cache over its cap. The
  // re-check's outcome is pinned deterministically by the test above; this one only guards the bound.
  [Test]
  public async Task MarkProcessed_ConcurrentInsertsAtCap_StaysBoundedAsync() {
    // Arrange: prime the cache to exactly the cap so every concurrent insert lands over it.
    var provider = _fakeProvider(out _);
    const int maxEntries = 20;
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(60), maxEntries: maxEntries);
    for (var i = 0; i < maxEntries; i++) {
      cache.MarkProcessed((Guid)TrackedGuid.New());
    }

    // Act: many threads insert concurrently, all racing the cap-eviction lock.
    var tasks = Enumerable.Range(0, 100)
      .Select(_ => Task.Run(() => cache.MarkProcessed((Guid)TrackedGuid.New())))
      .ToArray();
    await Task.WhenAll(tasks);

    // Assert
    await Assert.That(cache.Count).IsLessThanOrEqualTo(maxEntries)
      .Because("concurrent inserts racing the cap-eviction lock must never leave the cache unbounded");
  }

  // ==================== Test Fakes ====================

  private sealed class SpyObserver : IProcessedEventCacheObserver {
    public List<(IReadOnlyList<Guid> EventIds, string PerspectiveName, Guid StreamId)> DedupCalls { get; } = [];
    public List<IReadOnlyList<Guid>> InFlightCalls { get; } = [];
    public List<int> ActivationCounts { get; } = [];
    public List<int> EvictionCounts { get; } = [];
    public List<Guid> RemovedEventIds { get; } = [];

    public void OnEventsDeduped(IReadOnlyList<Guid> dedupedEventIds, string perspectiveName, Guid streamId) =>
      DedupCalls.Add((dedupedEventIds, perspectiveName, streamId));

    public void OnEventsMarkedInFlight(IReadOnlyList<Guid> eventIds) =>
      InFlightCalls.Add(eventIds);

    public void OnRetentionActivated(int count) =>
      ActivationCounts.Add(count);

    public void OnEvicted(int count) =>
      EvictionCounts.Add(count);

    public void OnEventsRemoved(IReadOnlyList<Guid> eventIds) =>
      RemovedEventIds.AddRange(eventIds);
  }
}
