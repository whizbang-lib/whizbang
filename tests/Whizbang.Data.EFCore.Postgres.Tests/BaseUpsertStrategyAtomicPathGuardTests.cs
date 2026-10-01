using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A guard on the atomic UPSERT path that decides whether it runs at all. It bails to the
/// SELECT-then-INSERT path, and it must leave the row written either way — the atomic path is
/// an optimization, and a guard that turned into a dropped write would be silent.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[NotInParallel("EFCorePostgresTests")]
[Category("Integration")]
[Category("Shard4")]
public class BaseUpsertStrategyAtomicPathGuardTests : EFCoreTestBase {

  [Test]
  public async Task Upsert_WithATableNameThatIsNotAPlainIdentifier_StillPersistsViaTheFallbackAsync() {
    // The atomic path interpolates the table name into raw SQL, so it refuses anything that is
    // not a plain unquoted PostgreSQL identifier. These names come from generated perspective
    // infrastructure today, which is exactly why the guard has to hold if that ever changes —
    // and why bailing must cost the write nothing.
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      context,
      "wh_per_order; DROP TABLE wh_per_order --",
      testId,
      new Order { OrderId = new TestOrderId(testId), Amount = 12.25m, Status = "GuardedName" },
      new PerspectiveMetadata { EventType = "OrderCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope());

    await using var read = CreateDbContext();
    var row = await read.Set<PerspectiveRow<Order>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull()
      .Because("the guard declines the atomic path, it does not decline the write — a rejected "
             + "identifier that also dropped the row would lose data silently");
    await Assert.That(row!.Data.Amount).IsEqualTo(12.25m);

    var tableStillThere = await read.Set<PerspectiveRow<Order>>().AsNoTracking().AnyAsync();
    await Assert.That(tableStillThere).IsTrue()
      .Because("nothing that looks like SQL in a table name may reach the database");
  }
}
