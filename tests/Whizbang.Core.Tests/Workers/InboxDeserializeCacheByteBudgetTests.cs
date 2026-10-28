// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1793: the deserialize cache was bounded by entry count and time only, so its memory scaled with message
/// size; at 10,000 entries of re-delivery bundles (up to 192 KB each) it could hold about 1.9 GB. It is now bounded
/// by the bytes the payloads were parsed from, and an entry is dropped when its row completes.
/// </summary>
[Category("Workers")]
public sealed class InboxDeserializeCacheByteBudgetTests {
  private static (FakeTimeProvider Clock, InboxDeserializeCache Cache) _cache(long maxBytes, int maxEntries = 10_000) {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
    return (clock, new InboxDeserializeCache(new SystemTimeProvider(clock), maxEntries: maxEntries, maxBytes: maxBytes));
  }

  private static Guid _id() => (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();

  [Test]
  public async Task Bytes_AreTheSumOfTheResidentEntriesAsync() {
    var (_, cache) = _cache(maxBytes: 1_000);
    cache.Set(_id(), new object(), 100);
    cache.Set(_id(), new object(), 200);
    await Assert.That(cache.Bytes).IsEqualTo(300);
  }

  [Test]
  public async Task OverTheBudget_TheOldestEntriesAreEvictedUntilItFitsAsync() {
    var (clock, cache) = _cache(maxBytes: 250);
    var a = _id();
    var b = _id();
    var c = _id();
    cache.Set(a, "a", 100);
    clock.Advance(TimeSpan.FromSeconds(1));
    cache.Set(b, "b", 100);
    clock.Advance(TimeSpan.FromSeconds(1));
    cache.Set(c, "c", 100);

    await Assert.That(cache.TryGet(a, out _)).IsFalse()
      .Because("an entry count far below its cap must not let the cache outgrow its byte budget");
    await Assert.That(cache.TryGet(b, out _)).IsTrue();
    await Assert.That(cache.TryGet(c, out _)).IsTrue();
    await Assert.That(cache.Bytes).IsEqualTo(200);
  }

  [Test]
  public async Task APayloadLargerThanTheWholeBudget_IsNotCachedAsync() {
    var (_, cache) = _cache(maxBytes: 100);
    var big = _id();
    cache.Set(big, "big", 150);
    await Assert.That(cache.TryGet(big, out _)).IsFalse();
    await Assert.That(cache.Bytes).IsEqualTo(0);
  }

  [Test]
  public async Task Remove_DropsTheEntryAndItsBytesAsync() {
    var (_, cache) = _cache(maxBytes: 1_000);
    var id = _id();
    cache.Set(id, "x", 100);
    cache.Remove(id);
    cache.Remove(id);
    await Assert.That(cache.TryGet(id, out _)).IsFalse();
    await Assert.That(cache.Bytes).IsEqualTo(0);
  }

  [Test]
  public async Task ReSettingAKey_ReplacesItsBytes_RatherThanAddingToThemAsync() {
    var (_, cache) = _cache(maxBytes: 1_000);
    var id = _id();
    cache.Set(id, "x", 100);
    cache.Set(id, "y", 40);
    await Assert.That(cache.Bytes).IsEqualTo(40);
  }

  [Test]
  public async Task ReSettingAKey_WithAPayloadTooLarge_DropsTheOldEntryAsync() {
    var (_, cache) = _cache(maxBytes: 100);
    var id = _id();
    cache.Set(id, "x", 50);
    cache.Set(id, "y", 500);
    await Assert.That(cache.TryGet(id, out _)).IsFalse();
    await Assert.That(cache.Bytes).IsEqualTo(0);
  }

  [Test]
  public async Task SweepAndTheEntryCap_ReleaseTheBytesOfWhatTheyEvictAsync() {
    var (clock, cache) = _cache(maxBytes: 1_000_000, maxEntries: 1);
    cache.Set(_id(), "a", 100);
    clock.Advance(TimeSpan.FromSeconds(1));
    cache.Set(_id(), "b", 100);
    await Assert.That(cache.Bytes).IsEqualTo(100)
      .Because("the entry-count cap evicted one entry and must release its bytes");

    clock.Advance(TimeSpan.FromHours(1));
    cache.SweepExpired();
    await Assert.That(cache.Bytes).IsEqualTo(0);
  }

  [Test]
  public async Task ANonPositiveBudget_IsRefusedAsync() {
    await Assert.That(() => _cache(maxBytes: 0)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Options_DefaultTheBudgetTo64MebibytesAsync() {
    await Assert.That(new InboxDeserializeCacheOptions().MaxBytes).IsEqualTo(64L * 1024 * 1024);
  }
}
