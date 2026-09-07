using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-round-23 tests for <see cref="PerspectiveCursorCache"/> targeting
/// <see cref="PerspectiveCursorCache.HasStream"/> and the no-subscriber path of the
/// activity-triggered eviction hook.
/// </summary>
public class PerspectiveCursorCacheCoverageTests {

  private static PerspectiveStreamAffinityOptions _testOptions() => new() {
    IdleEvictionWindow = TimeSpan.FromMinutes(15),
    SweepInterval = TimeSpan.FromMinutes(1)
  };

  // Drain mode calls this to skip a redundant batch cursor fetch for streams already resident
  // in the cache. If it wrongly reported a cached stream as absent, drain mode would issue an
  // unnecessary DB round trip for it every batch -- correctness-preserving but a throughput
  // regression at scale.
  [Test]
  public async Task HasStream_StreamCached_ReturnsTrueAsync() {
    // Arrange
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    cache.Set(streamId, "TestPerspective", Guid.NewGuid());

    // Act
    var found = cache.HasStream(streamId);

    // Assert
    await Assert.That(found).IsTrue();
  }

  // Same invariant, the other direction: if HasStream wrongly reported an uncached stream as
  // present, drain mode would skip fetching its cursor from the DB entirely and could proceed
  // as though the stream had no prior progress recorded, when the opposite is what the caller
  // needs to know before deciding whether a DB lookup is required.
  [Test]
  public async Task HasStream_StreamNotCached_ReturnsFalseAsync() {
    // Arrange
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    // Act
    var found = cache.HasStream(streamId);

    // Assert
    await Assert.That(found).IsFalse();
  }

  // The eviction hook lets a paired cache (the per-stream affinity gate dictionary in
  // PerspectiveWorker) drop its own entries in step with this cache's sweep, instead of running
  // a second independent time-based sweep over the same stream ids. If raising that event threw
  // when nobody had subscribed yet (e.g. a worker still starting up), every idle-triggered sweep
  // would crash instead of quietly evicting -- turning routine cache housekeeping into a fault.
  [Test]
  public async Task ActivityTriggeredSweep_NoSubscribers_EvictsWithoutThrowingAsync() {
    // Arrange
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var cache = new PerspectiveCursorCache(_testOptions(), clock);
    var staleStream = Guid.NewGuid();
    cache.Set(staleStream, "TestPerspective", Guid.NewGuid());
    // Deliberately no cache.OnStreamsEvicted subscriber attached.

    // Act - advance past both the idle window and the sweep interval, then touch a different
    // stream so the activity-triggered path (_touch -> _sweepIfDue -> _sweepCore -> _raiseEvicted)
    // actually runs. RunSweepNowForTests() bypasses _raiseEvicted entirely and cannot exercise
    // the no-subscriber branch under test.
    clock.Advance(TimeSpan.FromMinutes(16));
    cache.Set(Guid.NewGuid(), "TestPerspective", Guid.NewGuid());

    // Assert - reaching this line at all proves _raiseEvicted did not throw with a null handler;
    // the removed entry proves the sweep actually ran the eviction (not merely returned early).
    await Assert.That(cache.TryGet(staleStream, "TestPerspective", out _)).IsFalse()
      .Because("the stale stream's entry must be gone once the activity-triggered sweep completes, proving the no-subscriber path in _raiseEvicted ran to completion instead of throwing");
  }
}
