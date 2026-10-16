// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="InboxDeserializeCache"/> — Slice 15 of plans/pump-then-process.md.
/// Per-instance bounded LRU cache that holds the deserialized inbox payload across the four
/// lifecycle stages (PreInbox / PostInbox / PostAllPerspectives / PostLifecycle) and across
/// transport-redelivered or lease-re-claimed dispatches of the same <c>messageId</c>.
/// Configurable TTL (default 2 min). AOT-safe — no reflection, no Activator.
/// </summary>
public class InboxDeserializeCacheTests {

  private static SystemTimeProvider _fakeProvider(out FakeTimeProvider fake) {
    fake = new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero));
    return new SystemTimeProvider(fake);
  }

  private sealed record TestMessage(string Value);

  [Test]
  public async Task TryGet_NeverSeen_ReturnsFalseAsync() {
    var cache = new InboxDeserializeCache(_fakeProvider(out _));

    var hit = cache.TryGet((Guid)TrackedGuid.New(), out var message);

    await Assert.That(hit).IsFalse();
    await Assert.That(message).IsNull();
  }

  [Test]
  public async Task Set_ThenTryGet_WithinTtl_ReturnsCachedReferenceAsync() {
    var cache = new InboxDeserializeCache(_fakeProvider(out _));
    var messageId = (Guid)TrackedGuid.New();
    var payload = new TestMessage("Hello");

    cache.Set(messageId, payload);
    var hit = cache.TryGet(messageId, out var got);

    await Assert.That(hit).IsTrue();
    await Assert.That(got).IsSameReferenceAs(payload)
      .Because("Cache must return the same instance — no re-clone, no re-serialize.");
  }

  [Test]
  public async Task DefaultTtl_IsTwoMinutesAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new InboxDeserializeCache(provider);
    var messageId = (Guid)TrackedGuid.New();

    cache.Set(messageId, new TestMessage("x"));
    fake.Advance(TimeSpan.FromSeconds(119));
    var withinTtl = cache.TryGet(messageId, out _);
    fake.Advance(TimeSpan.FromSeconds(2));
    var pastTtl = cache.TryGet(messageId, out _);

    await Assert.That(withinTtl).IsTrue();
    await Assert.That(pastTtl).IsFalse()
      .Because("Default TTL is 2 minutes per slice 15 spec.");
  }

  [Test]
  public async Task Set_PastTtl_TryGetReturnsFalseAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new InboxDeserializeCache(provider, ttl: TimeSpan.FromMinutes(2));
    var messageId = (Guid)TrackedGuid.New();

    cache.Set(messageId, new TestMessage("x"));
    fake.Advance(TimeSpan.FromMinutes(3));
    var hit = cache.TryGet(messageId, out var got);

    await Assert.That(hit).IsFalse();
    await Assert.That(got).IsNull();
  }

  [Test]
  public async Task Set_RemarkRefreshesExpiryAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new InboxDeserializeCache(provider, ttl: TimeSpan.FromMinutes(2));
    var messageId = (Guid)TrackedGuid.New();

    cache.Set(messageId, new TestMessage("first"));
    fake.Advance(TimeSpan.FromSeconds(90));
    cache.Set(messageId, new TestMessage("second"));
    fake.Advance(TimeSpan.FromSeconds(90));
    var hit = cache.TryGet(messageId, out var got);

    await Assert.That(hit).IsTrue()
      .Because("Re-Set on an existing key resets the TTL window.");
    await Assert.That(((TestMessage)got!).Value).IsEqualTo("second");
  }

  [Test]
  public async Task SweepExpired_DropsPastTtlEntriesAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new InboxDeserializeCache(provider, ttl: TimeSpan.FromMinutes(2));
    var stale = (Guid)TrackedGuid.New();
    var fresh = (Guid)TrackedGuid.New();

    cache.Set(stale, new TestMessage("stale"));
    fake.Advance(TimeSpan.FromMinutes(3));
    cache.Set(fresh, new TestMessage("fresh"));
    cache.SweepExpired();

    await Assert.That(cache.Count).IsEqualTo(1)
      .Because("SweepExpired should drop only the past-TTL entries.");
    await Assert.That(cache.TryGet(stale, out _)).IsFalse();
    await Assert.That(cache.TryGet(fresh, out _)).IsTrue();
  }

  [Test]
  public async Task Cap_EvictsOldestWhenFullAsync() {
    var provider = _fakeProvider(out var fake);
    var cache = new InboxDeserializeCache(provider, ttl: TimeSpan.FromMinutes(2), maxEntries: 10);

    // Insert 10 — at cap. Then advance time and insert 1 more — overflow triggers eviction.
    var oldest = new List<Guid>();
    for (int i = 0; i < 10; i++) {
      var id = (Guid)TrackedGuid.New();
      oldest.Add(id);
      cache.Set(id, new TestMessage($"old{i}"));
      fake.Advance(TimeSpan.FromSeconds(1));
    }
    var newest = (Guid)TrackedGuid.New();
    cache.Set(newest, new TestMessage("new"));

    await Assert.That(cache.Count).IsLessThanOrEqualTo(10)
      .Because("Cache must self-bound to maxEntries to prevent unbounded memory growth.");
    await Assert.That(cache.TryGet(newest, out _)).IsTrue()
      .Because("Newest insert must survive eviction.");
    await Assert.That(cache.TryGet(oldest[0], out _)).IsFalse()
      .Because("Oldest entry by insertion time must be the first evicted.");
  }

  [Test]
  public async Task Constructor_RejectsNonPositiveTtlAsync() {
    var provider = _fakeProvider(out _);
    await Assert.That(() => new InboxDeserializeCache(provider, ttl: TimeSpan.Zero))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new InboxDeserializeCache(provider, ttl: TimeSpan.FromSeconds(-1)))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Constructor_RejectsZeroMaxEntriesAsync() {
    var provider = _fakeProvider(out _);
    await Assert.That(() => new InboxDeserializeCache(provider, maxEntries: 0))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Constructor_RejectsNullTimeProviderAsync() {
    await Assert.That(() => new InboxDeserializeCache(timeProvider: null!))
      .Throws<ArgumentNullException>();
  }

  #region SelectEvictionKeys — the eviction selector (AB#18949)

  /// <summary>
  /// A source that yields more entries than its <see cref="ICollection{T}.Count"/> admits, which is
  /// what a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> looks like
  /// to a reader when another thread inserts mid-read.
  /// </summary>
  /// <remarks>
  /// This is the failure AB#18949 reported, expressed without threads so it cannot flake. Buffering an
  /// ordered source, which is what LINQ does for <c>Take</c>, reads <c>Count</c>, allocates an array of
  /// that size and calls <c>CopyTo</c>; copying more entries than the array holds throws. In production
  /// the dispatch worker caught that, logged a failed deserialization and carried on, so a message
  /// stopped being processed for a reason nothing named.
  /// </remarks>
  private sealed class UndercountingSource(List<KeyValuePair<Guid, InboxDeserializeCache.Entry>> items, int reportedCount)
      : ICollection<KeyValuePair<Guid, InboxDeserializeCache.Entry>> {
    public int Count => reportedCount;
    public bool IsReadOnly => true;
    public IEnumerator<KeyValuePair<Guid, InboxDeserializeCache.Entry>> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(KeyValuePair<Guid, InboxDeserializeCache.Entry>[] array, int arrayIndex) {
      // Faithful to the real type: a destination sized from the stale Count cannot hold what is here.
      ArgumentNullException.ThrowIfNull(array);
      if (array.Length - arrayIndex < items.Count) {
        throw new ArgumentException("Destination array is not long enough.", nameof(array));
      }
      items.CopyTo(array, arrayIndex);
    }
    public void Add(KeyValuePair<Guid, InboxDeserializeCache.Entry> item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(KeyValuePair<Guid, InboxDeserializeCache.Entry> item) => items.Contains(item);
    public bool Remove(KeyValuePair<Guid, InboxDeserializeCache.Entry> item) => throw new NotSupportedException();
  }

  private static List<KeyValuePair<Guid, InboxDeserializeCache.Entry>> _entriesOldestFirst(int count, out List<Guid> ids) {
    var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var list = new List<KeyValuePair<Guid, InboxDeserializeCache.Entry>>();
    ids = [];
    for (var i = 0; i < count; i++) {
      var id = (Guid)TrackedGuid.New();
      ids.Add(id);
      list.Add(new(id, new InboxDeserializeCache.Entry($"p{i}", at.AddSeconds(i))));
    }
    return list;
  }

  [Test]
  public async Task SelectEvictionKeys_SourceYieldsMoreThanItCounts_DoesNotThrowAsync() {
    // Six entries behind a Count that still says four: the shape a concurrent insert leaves.
    var items = _entriesOldestFirst(6, out var ids);
    var source = new UndercountingSource(items, reportedCount: 4);

    var keys = InboxDeserializeCache.SelectEvictionKeys(source, batch: 3);

    await Assert.That(keys).Count().IsEqualTo(3)
      .Because("the selector walks the source once and never asks it how big it is, so a count that "
             + "moved under it is not an error. Sizing a destination from that count and copying into "
             + "it throws, and the caller turned that into a message that stopped being processed");
    await Assert.That(keys[0]).IsEqualTo(ids[0]);
    await Assert.That(keys[1]).IsEqualTo(ids[1]);
    await Assert.That(keys[2]).IsEqualTo(ids[2]);
  }

  [Test]
  public async Task SelectEvictionKeys_PicksTheOldestByExpiry_NotInsertionOrderAsync() {
    var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var youngest = (Guid)TrackedGuid.New();
    var oldest = (Guid)TrackedGuid.New();
    var middle = (Guid)TrackedGuid.New();
    // Deliberately inserted youngest first, so insertion order and expiry order disagree.
    var items = new List<KeyValuePair<Guid, InboxDeserializeCache.Entry>> {
      new(youngest, new InboxDeserializeCache.Entry("y", at.AddMinutes(30))),
      new(oldest, new InboxDeserializeCache.Entry("o", at)),
      new(middle, new InboxDeserializeCache.Entry("m", at.AddMinutes(10)))
    };

    var keys = InboxDeserializeCache.SelectEvictionKeys(items, batch: 2);

    await Assert.That(keys).Count().IsEqualTo(2);
    await Assert.That(keys[0]).IsEqualTo(oldest)
      .Because("eviction frees the entries closest to expiring; evicting by insertion order would "
             + "drop an entry a caller is about to read while keeping one nobody wants");
    await Assert.That(keys[1]).IsEqualTo(middle);
  }

  [Test]
  public async Task SelectEvictionKeys_BatchNotPositive_SelectsNothingAsync() {
    var items = _entriesOldestFirst(3, out _);

    await Assert.That(InboxDeserializeCache.SelectEvictionKeys(items, batch: 0)).IsEmpty()
      .Because("nothing was asked for, so nothing is evicted");
    await Assert.That(InboxDeserializeCache.SelectEvictionKeys(items, batch: -1)).IsEmpty()
      .Because("a negative batch is the same request as none, and must not be read as 'all'");
  }

  [Test]
  public async Task SelectEvictionKeys_BatchLargerThanTheSource_SelectsEverythingItHasAsync() {
    var items = _entriesOldestFirst(2, out var ids);

    var keys = InboxDeserializeCache.SelectEvictionKeys(items, batch: 10);

    await Assert.That(keys).Count().IsEqualTo(2)
      .Because("asking for more than exists takes what exists rather than indexing past the end");
    await Assert.That(keys).Contains(ids[0]);
    await Assert.That(keys).Contains(ids[1]);
  }

  #endregion
}
