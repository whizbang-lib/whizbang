using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks <see cref="EffectivePermissionsSnapshot"/>'s record-generated
/// surface: positional ctor, value-equality, hash consistency, `with`
/// non-destructive mutation, and a `ToString` that surfaces the
/// load-bearing fields (Hash + Version) so logs aren't lying.
/// The store tests cover the storage paths; this file pins the
/// data-shape behavior independently.
/// </summary>
/// <docs>fundamentals/security/effective-permissions</docs>
public class EffectivePermissionsSnapshotTests {

  private static EffectivePermissionsSnapshot _Snap(
      string hash = "h1",
      long version = 1,
      params string[] perms) {
    return new EffectivePermissionsSnapshot(
      Permissions: perms.Length == 0 ? ["read"] : perms,
      GroupIds: ["group-a"],
      RoleNames: ["admin"],
      Hash: hash,
      Version: version,
      ComputedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
  }

  [Test]
  public async Task PositionalConstructor_AssignsAllPropertiesAsync() {
    var snap = new EffectivePermissionsSnapshot(
      Permissions: ["read", "write"],
      GroupIds: ["g1", "g2"],
      RoleNames: ["admin"],
      Hash: "abc123",
      Version: 42,
      ComputedAt: new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero));

    await Assert.That(snap.Permissions).IsEquivalentTo(["read", "write"]);
    await Assert.That(snap.GroupIds).IsEquivalentTo(["g1", "g2"]);
    await Assert.That(snap.RoleNames).IsEquivalentTo(["admin"]);
    await Assert.That(snap.Hash).IsEqualTo("abc123");
    await Assert.That(snap.Version).IsEqualTo(42L);
    await Assert.That(snap.ComputedAt).IsEqualTo(new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero));
  }

  [Test]
  public async Task ValueEquality_SameInstancesAreEqualAsync() {
    // Records synthesize Equals/GetHashCode by member, but collection-typed
    // members use reference equality — so two snapshots that share their
    // list instances are equal, two with independently-allocated lists are
    // not. Lock both shapes so a future refactor that switches to deep
    // collection equality (or breaks the synthesized impl) surfaces here.
    IReadOnlyList<string> perms = ["read"];
    IReadOnlyList<string> groups = ["group-a"];
    IReadOnlyList<string> roles = ["admin"];
    var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var a = new EffectivePermissionsSnapshot(perms, groups, roles, "h", 1, when);
    var b = new EffectivePermissionsSnapshot(perms, groups, roles, "h", 1, when);
    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task ValueEquality_DistinctListInstances_AreNotEqualAsync() {
    // Two snapshots with the SAME contents but DIFFERENT list instances are
    // NOT equal — record equality on IReadOnlyList<string> is reference-based.
    // Locks the actual semantics so callers don't accidentally assume deep
    // equality and skip an audit-trigger comparison.
    var a = _Snap(perms: "read");
    var b = _Snap(perms: "read");
    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task ValueEquality_DifferentHashesAreNotEqualAsync() {
    // Same shared collections (so collection identity matches), differing
    // only on scalar fields.
    IReadOnlyList<string> perms = ["read"];
    IReadOnlyList<string> groups = ["group-a"];
    IReadOnlyList<string> roles = ["admin"];
    var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var a = new EffectivePermissionsSnapshot(perms, groups, roles, "h1", 1, when);
    var b = new EffectivePermissionsSnapshot(perms, groups, roles, "h2", 1, when);
    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task ValueEquality_DifferentVersionsAreNotEqualAsync() {
    IReadOnlyList<string> perms = ["read"];
    IReadOnlyList<string> groups = ["group-a"];
    IReadOnlyList<string> roles = ["admin"];
    var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var a = new EffectivePermissionsSnapshot(perms, groups, roles, "h", 1, when);
    var b = new EffectivePermissionsSnapshot(perms, groups, roles, "h", 2, when);
    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task With_NonDestructiveMutation_PreservesUnchangedFieldsAsync() {
    var original = _Snap(hash: "h1", version: 1);
    var rotated = original with { Hash = "h2", Version = 2 };

    await Assert.That(rotated.Hash).IsEqualTo("h2");
    await Assert.That(rotated.Version).IsEqualTo(2L);
    // Untouched fields carry forward.
    await Assert.That(rotated.Permissions).IsEquivalentTo(original.Permissions);
    await Assert.That(rotated.GroupIds).IsEquivalentTo(original.GroupIds);
    await Assert.That(rotated.ComputedAt).IsEqualTo(original.ComputedAt);
    // Original is unchanged.
    await Assert.That(original.Hash).IsEqualTo("h1");
  }

  [Test]
  public async Task ToString_SurfacesHashAndVersionAsync() {
    var snap = _Snap(hash: "abc123", version: 42);
    var s = snap.ToString();
    // Records' synthesized ToString includes named property values — these
    // two are the load-bearing identity fields, so a regression that drops
    // them from the format would silently obscure audit logs.
    await Assert.That(s).Contains("Hash = abc123");
    await Assert.That(s).Contains("Version = 42");
  }
}
