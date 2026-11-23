// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The write-behind model cache (#2247): bounded by models and bytes, a held model's later batches merged into one
/// write, and asked what is due (interval, batch cap, bounds) and what has outlived its lease.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public class PerspectiveWriteBehindCacheTests {
  private const string TABLE = "wh_per_counter";
  private const string NAME = "Tests.Counter";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(270);

  private static readonly PerspectiveRowValues _values = new(
    Data: "{}", Metadata: "{}", Scope: "{}", ForceUpdateScope: false, OrdersOnEventId: false,
    CreatedAt: _t0.UtcDateTime, UpdatedAt: _t0.UtcDateTime, SuppressActivity: false, BumpVersion: true, ExpiresAt: null,
    PhysicalValues: []);

  private sealed class Row(long bytes, Guid last) : PerspectiveWriteBehindRow(PerspectiveApplyRead.Unchecked, last, bytes) {
    public override PerspectiveStoredRow<TModel>? ReadAs<TModel>() => null;
  }

  private static PerspectiveWriteBehindCache<string> _cache(int models = 10, long bytes = 1000, int batches = 32, int intervalMs = 1000) =>
    new(new PerspectiveWriteBehindOptions {
      MaxModels = models,
      MaxBytes = bytes,
      MaxBatchesPerModel = batches,
      MaxFlushIntervalMilliseconds = intervalMs,
    }, _lease);

  private static WriteBehindHold<string> _hold(Guid stream, long bytes, string item = "batch", string data = "{}") {
    var eventId = Guid.CreateVersion7();
    return new(item, PendingPerspectiveWrite.Write(TABLE, stream, PerspectiveRowVersion.Of(3), _values with { Data = data }),
      new Row(bytes, eventId), [eventId]);
  }

  [Test]
  public async Task ANewModel_IsHeld_OnlyWhenItFitsBothBoundsAsync() {
    var cache = _cache(models: 2, bytes: 100);
    var a = (Guid.CreateVersion7(), NAME);
    var b = (Guid.CreateVersion7(), NAME);
    var c = (Guid.CreateVersion7(), NAME);

    await Assert.That(cache.TryHold(a, _hold(a.Item1, 60), _t0)).IsTrue();
    await Assert.That(cache.TryHold(b, _hold(b.Item1, 50), _t0)).IsFalse()
      .Because("60 + 50 bytes is over the byte bound: the batch is written now");
    await Assert.That(cache.TryHold(b, _hold(b.Item1, 40), _t0)).IsTrue();
    await Assert.That(cache.TryHold(c, _hold(c.Item1, 0), _t0)).IsFalse().Because("a third model is over the model bound");
    await Assert.That(cache.Count).IsEqualTo(2);
    await Assert.That(cache.Bytes).IsEqualTo(100);
  }

  [Test]
  public async Task AHeldModelsLaterBatch_IsAlwaysAdded_AndItsBytesReplaceTheOlderOnesAsync() {
    var cache = _cache(models: 1, bytes: 100);
    var key = (Guid.CreateVersion7(), NAME);
    var first = _hold(key.Item1, 60, "first", "{\"n\":1}");
    var second = _hold(key.Item1, 150, "second", "{\"n\":2}");

    cache.TryHold(key, first, _t0);
    await Assert.That(cache.TryHold(key, second, _t0.AddSeconds(1))).IsTrue();

    var entry = cache.Get(key)!;
    await Assert.That(entry.Holds.Select(h => h.Item)).IsEquivalentTo(["first", "second"]);
    await Assert.That(entry.Row).IsSameReferenceAs(second.Row);
    await Assert.That(entry.FirstHeldAt).IsEqualTo(_t0);
    await Assert.That(entry.EventIds).IsEquivalentTo([.. first.EventIds, .. second.EventIds]);
    await Assert.That(entry.MergedWrite().Row!.Data).IsEqualTo("{\"n\":2}");
    await Assert.That(cache.Bytes).IsEqualTo(150).Because("only the latest model is held");
    await Assert.That(cache.Due(_t0).Flush.Single()).IsEqualTo((key, WriteBehindFlushReason.Bounds))
      .Because("the grown model put the cache over its byte bound");
  }

  [Test]
  public async Task Due_NamesTheIntervalTheBatchCapAndTheOldestModelsOverABoundAsync() {
    var cache = _cache(models: 3, bytes: 1000, batches: 2, intervalMs: 1000);
    var old = (Guid.CreateVersion7(), NAME);
    var busy = (Guid.CreateVersion7(), NAME);
    var young = (Guid.CreateVersion7(), NAME);
    cache.TryHold(old, _hold(old.Item1, 10), _t0);
    cache.TryHold(busy, _hold(busy.Item1, 10), _t0.AddMilliseconds(500));
    cache.TryHold(busy, _hold(busy.Item1, 10), _t0.AddMilliseconds(600));
    cache.TryHold(young, _hold(young.Item1, 10), _t0.AddMilliseconds(900));

    var due = cache.Due(_t0.AddMilliseconds(1000));

    await Assert.That(due.Flush).IsEquivalentTo([(old, WriteBehindFlushReason.Interval), (busy, WriteBehindFlushReason.Batches)]);
    await Assert.That(due.Expired).IsEmpty();
    await Assert.That(cache.Due(_t0.AddMilliseconds(999)).Flush).IsEquivalentTo([(busy, WriteBehindFlushReason.Batches)]);
  }

  [Test]
  public async Task Due_FlushesOnlyAsManyOldModelsAsBringTheCacheBackUnderItsBoundsAsync() {
    var cache = _cache(models: 2, bytes: 100);
    var a = (Guid.CreateVersion7(), NAME);
    var b = (Guid.CreateVersion7(), NAME);
    cache.TryHold(a, _hold(a.Item1, 30), _t0);
    cache.TryHold(b, _hold(b.Item1, 30), _t0.AddMilliseconds(1));
    cache.TryHold(b, _hold(b.Item1, 80), _t0.AddMilliseconds(2));

    var due = cache.Due(_t0.AddMilliseconds(3));

    await Assert.That(due.Flush).IsEquivalentTo([(a, WriteBehindFlushReason.Bounds)])
      .Because("flushing the oldest model leaves 80 of 100 bytes");
  }

  [Test]
  public async Task AModelPastTheLease_IsExpired_NotFlushedAsync() {
    var cache = _cache();
    var key = (Guid.CreateVersion7(), NAME);
    cache.TryHold(key, _hold(key.Item1, 10), _t0);

    var due = cache.Due(_t0 + _lease);

    await Assert.That(due.Expired).IsEquivalentTo([key])
      .Because("its rows may already be another instance's: it is never flushed by this one");
    await Assert.That(due.Flush).IsEmpty();
  }

  [Test]
  public async Task Take_RemovesTheModelAndItsBytes_AndKeysFindTheHeldEventsAsync() {
    var cache = _cache();
    var a = (Guid.CreateVersion7(), NAME);
    var b = (Guid.CreateVersion7(), NAME);
    var holdA = _hold(a.Item1, 10);
    cache.TryHold(a, holdA, _t0);
    cache.TryHold(b, _hold(b.Item1, 20), _t0);

    await Assert.That(cache.Keys()).IsEquivalentTo([a, b]);
    await Assert.That(cache.KeysHolding([holdA.EventIds[0], Guid.CreateVersion7()])).IsEquivalentTo([a]);
    await Assert.That(cache.Take(a)).IsNotNull();
    await Assert.That(cache.Take(a)).IsNull();
    await Assert.That(cache.Get(a)).IsNull();
    await Assert.That(cache.Bytes).IsEqualTo(20);
    await Assert.That(cache.Count).IsEqualTo(1);
  }

  [Test]
  public async Task TheBounds_AreRequired_AndArgumentsCheckedAsync() {
    await Assert.That(() => new PerspectiveWriteBehindCache<string>(new PerspectiveWriteBehindOptions { MaxBytes = 1 }, _lease))
      .Throws<ArgumentException>();
    await Assert.That(() => new PerspectiveWriteBehindCache<string>(new PerspectiveWriteBehindOptions { MaxModels = 1 }, _lease))
      .Throws<ArgumentException>();
    await Assert.That(() => new PerspectiveWriteBehindCache<string>(null!, _lease)).Throws<ArgumentNullException>();
    var cache = _cache();
    await Assert.That(() => cache.TryHold((Guid.CreateVersion7(), NAME), null!, _t0)).Throws<ArgumentNullException>();
    await Assert.That(() => cache.KeysHolding(null!)).Throws<ArgumentNullException>();
  }
}
