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

    var hit = cache.TryGet((Guid)TrackedGuid.NewMedo(), out var message);

    await Assert.That(hit).IsFalse();
    await Assert.That(message).IsNull();
  }

  [Test]
  public async Task Set_ThenTryGet_WithinTtl_ReturnsCachedReferenceAsync() {
    var cache = new InboxDeserializeCache(_fakeProvider(out _));
    var messageId = (Guid)TrackedGuid.NewMedo();
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
    var messageId = (Guid)TrackedGuid.NewMedo();

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
    var messageId = (Guid)TrackedGuid.NewMedo();

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
    var messageId = (Guid)TrackedGuid.NewMedo();

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
    var stale = (Guid)TrackedGuid.NewMedo();
    var fresh = (Guid)TrackedGuid.NewMedo();

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
      var id = (Guid)TrackedGuid.NewMedo();
      oldest.Add(id);
      cache.Set(id, new TestMessage($"old{i}"));
      fake.Advance(TimeSpan.FromSeconds(1));
    }
    var newest = (Guid)TrackedGuid.NewMedo();
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
}
