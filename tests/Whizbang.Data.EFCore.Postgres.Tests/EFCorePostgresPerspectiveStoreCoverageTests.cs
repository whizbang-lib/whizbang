using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="EFCorePostgresPerspectiveStore{TModel}.PurgeByPartitionKeyAsync{TPartitionKey}"/>'s
/// relational branch (<c>ExecuteDeleteAsync</c>). <see cref="EFCorePostgresPerspectiveStoreTests"/>
/// exercises the same method but only against the EF Core InMemory provider, whose
/// <c>Database.IsRelational()</c> is always false — that suite's assertions all land on the
/// non-relational fallback (a tracked-entity <c>Remove</c> + <c>SaveChangesAsync</c>), never the
/// real-Postgres path this file drives via <see cref="EFCoreTestBase"/>.
/// <para>
/// A perspective store's writes must read back identically, and a purge is the write path's
/// mirror image: if the relational branch silently no-oped (or the guard picked the wrong
/// branch), a caller that believes a partition was purged would keep resolving stale data from
/// it — a purge that looks like it succeeded but did not is worse than one that throws.
/// </para>
/// </summary>
[Category("Shard1")]
public class EFCorePostgresPerspectiveStoreCoverageTests : EFCoreTestBase {

  private readonly IWhizbangIdProvider<TestOrderId> _orderIdProvider = TestOrderId.CreateProvider(new Uuid7IdProvider());

  [Test]
  public async Task PurgeByPartitionKeyAsync_OnARelationalProvider_ActuallyDeletesTheRowAsync() {
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "wh_per_order");
    var partitionKey = Guid.CreateVersion7();
    var order = new Order { OrderId = _orderIdProvider.NewId(), Amount = 42m, Status = "Created" };

    await store.UpsertByPartitionKeyAsync(partitionKey, order);

    var beforePurge = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(beforePurge).IsNotNull()
      .Because("seeding must actually land the row before the purge below proves anything");

    await store.PurgeByPartitionKeyAsync(partitionKey);

    var afterPurge = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(afterPurge).IsNull()
      .Because("on a REAL relational provider, PurgeByPartitionKeyAsync must issue an actual "
             + "ExecuteDeleteAsync — the InMemory-only fallback branch this suite's sibling covers "
             + "must not be silently taking over here instead");
  }
}
