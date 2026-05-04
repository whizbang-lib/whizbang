using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="IEffectivePermissionsStore{TUserId}"/> contract via the
/// <see cref="InMemoryEffectivePermissionsStore{TUserId}"/> reference implementation.
/// </summary>
/// <tests>IEffectivePermissionsStore,InMemoryEffectivePermissionsStore,EffectivePermissionsSnapshot</tests>
public class EffectivePermissionsStoreTests {
  private static EffectivePermissionsSnapshot _snap(string hash, long version = 1, params string[] perms) =>
    new(perms, [], [], hash, version, DateTimeOffset.UtcNow);

  [Test]
  public async Task GetAsync_UnknownUser_ReturnsNullAsync() {
    var store = new InMemoryEffectivePermissionsStore<Guid>();
    var got = await store.GetAsync(Guid.NewGuid());
    await Assert.That(got).IsNull();
  }

  [Test]
  public async Task SetAsync_ThenGetAsync_RoundTripsSnapshotAsync() {
    var store = new InMemoryEffectivePermissionsStore<Guid>();
    var userId = Guid.NewGuid();
    var snap = _snap("abc123", 1, "nav.home", "nav.job");

    await store.SetAsync(userId, snap);
    var got = await store.GetAsync(userId);

    await Assert.That(got).IsNotNull();
    var hash = got!.Hash;
    var permsCount = got.Permissions.Count;
    await Assert.That(hash).IsEqualTo("abc123");
    await Assert.That(permsCount).IsEqualTo(2);
  }

  [Test]
  public async Task SetAsync_SameHash_IsIdempotentAsync() {
    var store = new InMemoryEffectivePermissionsStore<Guid>();
    var userId = Guid.NewGuid();
    var first = _snap("h", 1, "p1");
    var secondSameHash = _snap("h", 99, "p1", "p2");   // different version, same hash

    await store.SetAsync(userId, first);
    await store.SetAsync(userId, secondSameHash);

    var got = await store.GetAsync(userId);
    var version = got!.Version;
    var permsCount = got.Permissions.Count;
    await Assert.That(version).IsEqualTo(1);
    await Assert.That(permsCount).IsEqualTo(1);
  }

  [Test]
  public async Task SetAsync_DifferentHash_OverwritesAsync() {
    var store = new InMemoryEffectivePermissionsStore<Guid>();
    var userId = Guid.NewGuid();
    await store.SetAsync(userId, _snap("h1", 1, "p1"));
    await store.SetAsync(userId, _snap("h2", 2, "p1", "p2"));

    var got = await store.GetAsync(userId);
    var hash = got!.Hash;
    var version = got.Version;
    var permsCount = got.Permissions.Count;
    await Assert.That(hash).IsEqualTo("h2");
    await Assert.That(version).IsEqualTo(2);
    await Assert.That(permsCount).IsEqualTo(2);
  }

  [Test]
  public async Task DifferentUsers_HaveIsolatedSnapshotsAsync() {
    var store = new InMemoryEffectivePermissionsStore<string>();
    await store.SetAsync("alice", _snap("ha", 1, "p1"));
    await store.SetAsync("bob", _snap("hb", 1, "p2"));

    var aliceSnap = await store.GetAsync("alice");
    var bobSnap = await store.GetAsync("bob");
    var aliceHash = aliceSnap!.Hash;
    var bobHash = bobSnap!.Hash;
    await Assert.That(aliceHash).IsEqualTo("ha");
    await Assert.That(bobHash).IsEqualTo("hb");
  }

  [Test]
  public async Task EffectivePermissionsSnapshot_RecordEquality_OnSameValuesAsync() {
    var ts = DateTimeOffset.UtcNow;
    var a = new EffectivePermissionsSnapshot(["p"], ["g"], ["r"], "h", 1, ts);
    var b = new EffectivePermissionsSnapshot(["p"], ["g"], ["r"], "h", 1, ts);
    var hashEqual = a.Hash == b.Hash;
    var sameRef = ReferenceEquals(a, b);
    await Assert.That(hashEqual).IsTrue();
    await Assert.That(sameRef).IsFalse();
  }
}
