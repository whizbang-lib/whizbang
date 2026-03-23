using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="ProcessedEventCache"/> — the two-phase TTL cache used by PerspectiveWorker
/// to prevent duplicate Apply calls when SQL re-delivers events during the batched completion window.
/// </summary>
/// <remarks>
/// Phase 1 (InFlight): Events added after Apply, no expiry — guards until DB confirms completion.
/// Phase 2 (Retained): After DB ack via ActivateRetention(), TTL starts counting down.
/// After TTL: Entries evicted, allowing reprocessing (correct for rewind scenarios).
/// </remarks>
public class ProcessedEventCacheTests {
  private static readonly TimeSpan _retentionPeriod = TimeSpan.FromMinutes(5);

  // ==================== InFlight Phase Tests ====================

  [Test]
  public async Task AddRange_InFlight_ContainsReturnsTrueAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    var eventId = Guid.CreateVersion7();

    // Act
    cache.AddRange([eventId]);

    // Assert
    await Assert.That(cache.Contains(eventId)).IsTrue()
      .Because("InFlight entries should be found in the cache");
  }

  [Test]
  public async Task InFlight_NeverExpires_UntilActivatedAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var eventId = Guid.CreateVersion7();
    cache.AddRange([eventId]);

    // Act — advance time well past retention period
    time.Advance(TimeSpan.FromHours(1));
    cache.EvictExpired();

    // Assert — InFlight entries survive any time advance
    await Assert.That(cache.Contains(eventId)).IsTrue()
      .Because("InFlight entries must never expire until ActivateRetention is called");
  }

  // ==================== Activation / Retained Phase Tests ====================

  [Test]
  public async Task ActivateRetention_StartsCountdownAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var eventId = Guid.CreateVersion7();
    cache.AddRange([eventId]);

    // Act — activate retention then advance past TTL
    cache.ActivateRetention();
    time.Advance(_retentionPeriod + TimeSpan.FromSeconds(1));
    cache.EvictExpired();

    // Assert
    await Assert.That(cache.Contains(eventId)).IsFalse()
      .Because("Retained entries should expire after retention period");
  }

  [Test]
  public async Task Retained_BeforeTtlExpires_ContainsReturnsTrueAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var eventId = Guid.CreateVersion7();
    cache.AddRange([eventId]);

    // Act — activate then advance to just BEFORE expiry
    cache.ActivateRetention();
    time.Advance(_retentionPeriod - TimeSpan.FromSeconds(1));

    // Assert
    await Assert.That(cache.Contains(eventId)).IsTrue()
      .Because("Retained entries within TTL window should still dedup");
  }

  [Test]
  public async Task Retained_AfterTtlExpires_ContainsReturnsFalseAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var eventId = Guid.CreateVersion7();
    cache.AddRange([eventId]);

    // Act — activate then advance past TTL
    cache.ActivateRetention();
    time.Advance(_retentionPeriod + TimeSpan.FromSeconds(1));

    // Assert — Contains itself should return false for expired (even before EvictExpired)
    await Assert.That(cache.Contains(eventId)).IsFalse()
      .Because("Retained entries past TTL should not dedup");
  }

  [Test]
  public async Task ActivateRetention_OnlyAffectsInFlightAsync() {
    // Arrange — one InFlight, one already Retained
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var earlyId = Guid.CreateVersion7();
    cache.AddRange([earlyId]);
    cache.ActivateRetention(); // earlyId is now Retained

    time.Advance(TimeSpan.FromMinutes(1)); // 1 min passes
    var lateId = Guid.CreateVersion7();
    cache.AddRange([lateId]); // lateId is InFlight

    // Act — activate again (should only affect lateId)
    cache.ActivateRetention();

    // Advance to just past earlyId's expiry (5 min from its activation)
    time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));

    // Assert — earlyId should be expired (activated 5+ mins ago)
    await Assert.That(cache.Contains(earlyId)).IsFalse()
      .Because("earlyId was activated 5+ minutes ago, should be expired");
    // lateId was activated 4 min ago, still within window
    await Assert.That(cache.Contains(lateId)).IsTrue()
      .Because("lateId was activated only ~4 minutes ago, still within retention");
  }

  // ==================== Eviction Tests ====================

  [Test]
  public async Task EvictExpired_RemovesOnlyRetainedPastTtlAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    var expiredId = Guid.CreateVersion7();
    var freshId = Guid.CreateVersion7();

    cache.AddRange([expiredId]);
    cache.ActivateRetention();
    time.Advance(_retentionPeriod + TimeSpan.FromSeconds(1)); // expiredId is past TTL

    cache.AddRange([freshId]); // freshId is InFlight (added after advance)

    // Act
    cache.EvictExpired();

    // Assert
    await Assert.That(cache.Contains(expiredId)).IsFalse()
      .Because("Expired retained entry should be evicted");
    await Assert.That(cache.Contains(freshId)).IsTrue()
      .Because("InFlight entry should survive eviction");
    await Assert.That(cache.Count).IsEqualTo(1);
  }

  [Test]
  public async Task EvictExpired_IgnoresInFlightEntriesAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    var id1 = Guid.CreateVersion7();
    var id2 = Guid.CreateVersion7();
    cache.AddRange([id1, id2]);

    // Advance well past retention but DON'T activate
    time.Advance(TimeSpan.FromHours(1));

    // Act
    cache.EvictExpired();

    // Assert
    await Assert.That(cache.Count).IsEqualTo(2)
      .Because("InFlight entries must never be evicted");
  }

  // ==================== Edge Cases ====================

  [Test]
  public async Task AddRange_DuplicateId_DoesNotThrowAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var eventId = Guid.CreateVersion7();

    // Act — add same ID twice
    cache.AddRange([eventId]);
    cache.AddRange([eventId]);

    // Assert
    await Assert.That(cache.Contains(eventId)).IsTrue();
    await Assert.That(cache.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Contains_EmptyCache_ReturnsFalseAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);

    // Act & Assert
    await Assert.That(cache.Contains(Guid.CreateVersion7())).IsFalse();
  }

  [Test]
  public async Task Remove_ClearsEntry_ForRewindAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    var eventId = Guid.CreateVersion7();
    cache.AddRange([eventId]);

    // Act
    cache.Remove(eventId);

    // Assert
    await Assert.That(cache.Contains(eventId)).IsFalse()
      .Because("Removed entries should not dedup (allows rewind replay)");
    await Assert.That(observer.RemovedEventIds).Contains(eventId);
  }

  [Test]
  public async Task Count_ExcludesExpiredEntriesAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var cache = new ProcessedEventCache(_retentionPeriod, time);
    var id1 = Guid.CreateVersion7();
    var id2 = Guid.CreateVersion7();
    cache.AddRange([id1, id2]);
    cache.ActivateRetention();

    // Expire id1 and id2
    time.Advance(_retentionPeriod + TimeSpan.FromSeconds(1));
    cache.EvictExpired();

    // Add a fresh entry
    var id3 = Guid.CreateVersion7();
    cache.AddRange([id3]);

    // Assert
    await Assert.That(cache.Count).IsEqualTo(1)
      .Because("Only non-evicted entries should count");
  }

  // ==================== Observer Hook Tests ====================

  [Test]
  public async Task Observer_OnEventsMarkedInFlight_CalledAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    var id1 = Guid.CreateVersion7();
    var id2 = Guid.CreateVersion7();

    // Act
    cache.AddRange([id1, id2]);

    // Assert
    await Assert.That(observer.InFlightCalls).Count().IsEqualTo(1);
    await Assert.That(observer.InFlightCalls[0]).Contains(id1);
    await Assert.That(observer.InFlightCalls[0]).Contains(id2);
  }

  [Test]
  public async Task Observer_OnRetentionActivated_CalledAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    cache.AddRange([Guid.CreateVersion7(), Guid.CreateVersion7()]);

    // Act
    cache.ActivateRetention();

    // Assert
    await Assert.That(observer.ActivationCounts).Count().IsEqualTo(1);
    await Assert.That(observer.ActivationCounts[0]).IsEqualTo(2);
  }

  [Test]
  public async Task Observer_OnEvicted_CalledAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    cache.AddRange([Guid.CreateVersion7()]);
    cache.ActivateRetention();
    time.Advance(_retentionPeriod + TimeSpan.FromSeconds(1));

    // Act
    cache.EvictExpired();

    // Assert
    await Assert.That(observer.EvictionCounts).Count().IsEqualTo(1);
    await Assert.That(observer.EvictionCounts[0]).IsEqualTo(1);
  }

  [Test]
  public async Task Observer_OnEventsRemoved_CalledAsync() {
    // Arrange
    var time = new FakeTimeProvider();
    var observer = new SpyObserver();
    var cache = new ProcessedEventCache(_retentionPeriod, time, observer);
    var eventId = Guid.CreateVersion7();
    cache.AddRange([eventId]);

    // Act
    cache.Remove(eventId);

    // Assert
    await Assert.That(observer.RemovedEventIds).Contains(eventId);
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
