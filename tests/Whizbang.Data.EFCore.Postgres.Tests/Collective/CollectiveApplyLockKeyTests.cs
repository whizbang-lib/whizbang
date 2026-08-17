using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
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

  [Test]
  public async Task Compute_WithNullTable_ThrowsAsync() {
    await Assert.That(() => CollectiveApplyLockKey.Compute(null!, "")).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Compute_WithNullScopeKey_ThrowsAsync() {
    await Assert.That(() => CollectiveApplyLockKey.Compute("t", null!)).Throws<ArgumentNullException>();
  }
}
