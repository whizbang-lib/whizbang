using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 26.8 — RED-first locks for the parallel commit_sequence cache on
/// <see cref="PerspectiveCursorCache"/>. Runs alongside the existing event_id cache;
/// drain-mode worker stores both, prefers commit_sequence for inversion detection
/// when available (slice 26.10).
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class PerspectiveCursorCacheCommitSequenceTests {

  [Test]
  public async Task TryGetCommitSequence_NotCached_ReturnsFalseAsync() {
    var cache = new PerspectiveCursorCache();
    var result = cache.TryGetCommitSequence(Guid.NewGuid(), "P", out var seq);
    await Assert.That(result).IsFalse();
    await Assert.That(seq).IsNull();
  }

  [Test]
  public async Task SetCommitSequence_ThenTryGet_ReturnsValueAsync() {
    var cache = new PerspectiveCursorCache();
    var sid = Guid.NewGuid();
    cache.SetCommitSequence(sid, "P", 42L);
    var result = cache.TryGetCommitSequence(sid, "P", out var seq);
    await Assert.That(result).IsTrue();
    await Assert.That(seq).IsEqualTo((long?)42L);
  }

  [Test]
  public async Task EventIdAndCommitSequenceCaches_AreIndependentAsync() {
    // The event_id cache and commit_sequence cache are parallel — setting one
    // does NOT populate the other. Drain-mode worker is responsible for keeping
    // both in sync after each apply.
    var cache = new PerspectiveCursorCache();
    var sid = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    cache.Set(sid, "P", eventId);
    var hasSeq = cache.TryGetCommitSequence(sid, "P", out _);
    await Assert.That(hasSeq).IsFalse()
      .Because("Set populates only the event_id cache; commit_sequence stays unset");

    cache.SetCommitSequence(sid, "P", 42L);
    var hasEventId = cache.TryGet(sid, "P", out var cachedEventId);
    await Assert.That(hasEventId).IsTrue();
    await Assert.That(cachedEventId).IsEqualTo((Guid?)eventId);
  }

  [Test]
  public async Task Invalidate_RemovesBothCachesAsync() {
    var cache = new PerspectiveCursorCache();
    var sid = Guid.NewGuid();
    cache.Set(sid, "P", Guid.NewGuid());
    cache.SetCommitSequence(sid, "P", 42L);

    cache.Invalidate(sid, "P");

    await Assert.That(cache.TryGet(sid, "P", out _)).IsFalse();
    await Assert.That(cache.TryGetCommitSequence(sid, "P", out _)).IsFalse();
  }

  [Test]
  public async Task InvalidateStream_RemovesAllPerspectivesBothCachesAsync() {
    var cache = new PerspectiveCursorCache();
    var sid = Guid.NewGuid();
    cache.Set(sid, "P1", Guid.NewGuid());
    cache.Set(sid, "P2", Guid.NewGuid());
    cache.SetCommitSequence(sid, "P1", 10L);
    cache.SetCommitSequence(sid, "P2", 20L);

    cache.InvalidateStream(sid);

    await Assert.That(cache.TryGet(sid, "P1", out _)).IsFalse();
    await Assert.That(cache.TryGet(sid, "P2", out _)).IsFalse();
    await Assert.That(cache.TryGetCommitSequence(sid, "P1", out _)).IsFalse();
    await Assert.That(cache.TryGetCommitSequence(sid, "P2", out _)).IsFalse();
  }

  [Test]
  public async Task Clear_RemovesAllBothCachesAsync() {
    var cache = new PerspectiveCursorCache();
    cache.Set(Guid.NewGuid(), "P", Guid.NewGuid());
    cache.SetCommitSequence(Guid.NewGuid(), "P", 42L);

    cache.Clear();

    await Assert.That(cache.Count).IsEqualTo(0);
  }
}
