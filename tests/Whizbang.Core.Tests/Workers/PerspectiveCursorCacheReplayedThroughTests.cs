// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1361: the cursor cache remembers which events a completed rewind was run for, per (stream, perspective), so
/// a pending row for one of them afterwards is not taken for a new inversion.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveCursorCache.cs</code-under-test>
public class PerspectiveCursorCacheReplayedThroughTests {
  private const string PERSPECTIVE = "Test.Perspective";

  [Test]
  public async Task WasReplayed_OnlyForTheEventsMarked_OnThatPairAsync() {
    var cache = new PerspectiveCursorCache();
    var stream = Guid.CreateVersion7();
    var replayed = Guid.CreateVersion7();

    cache.MarkReplayed(stream, PERSPECTIVE, [replayed]);

    await Assert.That(cache.WasReplayed(stream, PERSPECTIVE, replayed)).IsTrue();
    await Assert.That(cache.WasReplayed(stream, PERSPECTIVE, Guid.CreateVersion7())).IsFalse()
      .Because("an event the rewind was not run for keeps its own inversion check");
    await Assert.That(cache.WasReplayed(stream, "Other.Perspective", replayed)).IsFalse();
    await Assert.That(cache.WasReplayed(Guid.CreateVersion7(), PERSPECTIVE, replayed)).IsFalse();
  }

  [Test]
  public async Task Invalidate_KeepsTheReplayedEvents_InvalidateStreamAndClear_ForgetThemAsync() {
    // A rewind invalidates the cursor before it runs; the events earlier rewinds applied stay applied.
    var cache = new PerspectiveCursorCache();
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var c = Guid.CreateVersion7();
    var e = Guid.CreateVersion7();
    cache.MarkReplayed(a, PERSPECTIVE, [e]);
    cache.MarkReplayed(b, PERSPECTIVE, [e]);
    cache.MarkReplayed(c, PERSPECTIVE, [e]);

    cache.Invalidate(a, PERSPECTIVE);
    cache.InvalidateStream(b);
    await Assert.That(cache.WasReplayed(a, PERSPECTIVE, e)).IsTrue();
    await Assert.That(cache.WasReplayed(b, PERSPECTIVE, e)).IsFalse();

    cache.Clear();
    await Assert.That(cache.WasReplayed(c, PERSPECTIVE, e)).IsFalse();
  }

  [Test]
  public async Task APairPastTheLimit_StartsOverAsync() {
    var cache = new PerspectiveCursorCache();
    var stream = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();
    cache.MarkReplayed(stream, PERSPECTIVE, [first]);

    cache.MarkReplayed(stream, PERSPECTIVE,
      Enumerable.Range(0, PerspectiveCursorCache.MAX_REPLAYED_EVENTS_PER_PAIR).Select(_ => Guid.CreateVersion7()));

    await Assert.That(cache.WasReplayed(stream, PERSPECTIVE, first)).IsFalse()
      .Because("the set is bounded per pair; forgetting costs at most one more rewind");
  }

  [Test]
  public async Task MarkReplayed_RefusesNullEventIdsAsync() =>
    await Assert.That(() => new PerspectiveCursorCache().MarkReplayed(Guid.CreateVersion7(), PERSPECTIVE, null!))
      .Throws<ArgumentNullException>();

  [Test]
  public async Task AnIdleStream_IsEvicted_WithItsReplayedEventsAsync() {
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var options = new PerspectiveStreamAffinityOptions { IdleEvictionWindow = TimeSpan.FromMinutes(15) };
    var cache = new PerspectiveCursorCache(options, clock);
    var stream = Guid.CreateVersion7();
    var e = Guid.CreateVersion7();
    cache.MarkReplayed(stream, PERSPECTIVE, [e]);

    clock.Advance(TimeSpan.FromMinutes(16));
    var evicted = cache.RunSweepNowForTests();

    await Assert.That(evicted).Contains(stream);
    await Assert.That(cache.WasReplayed(stream, PERSPECTIVE, e)).IsFalse();
  }
}
