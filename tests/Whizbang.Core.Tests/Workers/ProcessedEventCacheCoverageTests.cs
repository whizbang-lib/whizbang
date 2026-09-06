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
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
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
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
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
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
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

  // The locked re-check exists because multiple threads can pass the unlocked "count > cap"
  // check together. Without the re-check, every thread that raced past it would run a full
  // eviction batch — over-evicting entries that are still within TTL under concurrent load,
  // forcing needless duplicate-processing fallbacks in the perspective drainer.
  [Test]
  public async Task MarkProcessed_ConcurrentInsertsAtCap_StaysBoundedAsync() {
    // Arrange — prime the cache to exactly the cap so the very first wave of concurrent
    // inserts is already over it, maximizing the chance multiple threads pass the unlocked
    // check together and race into the locked re-check this test targets.
    var provider = _fakeProvider(out _);
    const int maxEntries = 20;
    var cache = new RecentlyProcessedEventCache(provider, ttl: TimeSpan.FromMinutes(60), maxEntries: maxEntries);
    for (var i = 0; i < maxEntries; i++) {
      cache.MarkProcessed((Guid)TrackedGuid.NewMedo());
    }

    // Act — many threads insert concurrently, all racing the cap-eviction lock.
    var tasks = Enumerable.Range(0, 100)
      .Select(_ => Task.Run(() => cache.MarkProcessed((Guid)TrackedGuid.NewMedo())))
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
