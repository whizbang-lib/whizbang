using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The collective-apply key is durable coordination state: the exclusive holder (collective apply)
/// and the shared holder (standard per-row apply) must derive the identical key for the same
/// table+scope, across processes and across library versions during a rolling deploy. These pinned
/// values exist so any change to the underlying hash fails here rather than silently letting two
/// instances apply the same collective concurrently.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyLockKey.cs</code-under-test>
[Category("Collective")]
[Category("Shard4")]
public class CollectiveApplyLockKeyTests {

  [Test]
  [Arguments("inventory", "", 167766346951628793L)]
  [Arguments("wh_per_orders", "tenant-a", 7042163902737380820L)]
  public async Task Compute_ForTableAndScope_ReturnsPinnedProcessStableKeyAsync(
      string table, string scopeKey, long expected) {
    await Assert.That(CollectiveApplyLockKey.Compute(table, scopeKey)).IsEqualTo(expected);
  }

  [Test]
  public async Task Compute_ForDifferentScopes_ReturnsDifferentKeysAsync() {
    await Assert.That(CollectiveApplyLockKey.Compute("wh_per_orders", "tenant-a"))
      .IsNotEqualTo(CollectiveApplyLockKey.Compute("wh_per_orders", "tenant-b"));
  }

  /// <summary>
  /// Two tenants applying to the same table take different locks.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The tests above pass the scope key as a string and so could never see the defect, which was in
  /// what the callers passed: the key was built from the scope's kind and its <c>ToString()</c>, and
  /// <c>ToString()</c> is the kind. Every tenant therefore keyed on "tenant:tenant" and every
  /// tenant's collective applies serialized against each other, one at a time, per table -- a
  /// lock with one holder and a dozen waiters, until the waiters reached a command timeout.
  /// </para>
  /// <para>
  /// Asserted from the scope rather than from a string, because that is where it went wrong. The
  /// third assertion is the trap itself: the kind is equal for both, so anything keyed on the kind
  /// collapses them.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TwoTenants_OnTheSameTable_TakeDifferentLocksAsync() {
    const string table = "wh_per_orders";
    var a = new TenantCollectiveScope("tenant-a");
    var b = new TenantCollectiveScope("tenant-b");

    await Assert.That(a.ScopeIdentity).IsNotEqualTo(b.ScopeIdentity)
      .Because("the tenant is what makes one of these narrower than its kind");
    await Assert.That(CollectiveApplyLockKey.Compute(table, a.ScopeIdentity))
      .IsNotEqualTo(CollectiveApplyLockKey.Compute(table, b.ScopeIdentity))
      .Because("two tenants applying to one table have no reason to wait for each other");
    await Assert.That(a.ScopeKind).IsEqualTo(b.ScopeKind)
      .Because("the kind is equal for both, which is why a key built from it served every tenant one lock");
    await Assert.That(a.ScopeIdentity).Contains("tenant-a", StringComparison.Ordinal)
      .Because("an identity that does not carry the tenant cannot tell two tenants apart");
  }

  /// <summary>The same tenant still shares one lock, which is what the lock is for.</summary>
  [Test]
  public async Task TheSameTenant_OnTheSameTable_TakesOneLockAsync() {
    const string table = "wh_per_orders";
    await Assert.That(CollectiveApplyLockKey.Compute(table, new TenantCollectiveScope("t").ScopeIdentity))
      .IsEqualTo(CollectiveApplyLockKey.Compute(table, new TenantCollectiveScope("t").ScopeIdentity))
      .Because("serializing a tenant's own collective applies is the point; only other tenants were collateral");
  }

  [Test]
  public async Task Compute_WithNullTable_ThrowsAsync() {
    await Assert.That(() => CollectiveApplyLockKey.Compute(null!, "")).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Compute_WithNullScopeKey_ThrowsAsync() {
    await Assert.That(() => CollectiveApplyLockKey.Compute("t", null!)).Throws<ArgumentNullException>();
  }
}
