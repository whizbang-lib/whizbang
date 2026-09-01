using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Two guards on the atomic UPSERT path that decide whether it runs at all. Both bail to the
/// SELECT-then-INSERT path, and both must leave the row written either way — the atomic path is
/// an optimization, and a guard that turned into a dropped write would be silent.
/// </summary>
/// <remarks>
/// Mutates the process-wide <see cref="BaseUpsertStrategy.PathOnePersistenceOptionsProvider"/>,
/// so it joins the "EFCorePostgresTests" group with every other suite that flips it.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[NotInParallel("EFCorePostgresTests")]
[Category("Integration")]
[Category("Shard4")]
public class BaseUpsertStrategyAtomicPathGuardTests : EFCoreTestBase {

  [After(Test)]
  public Task ClearPathOneProviderAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    return Task.CompletedTask;
  }

  [Test]
  public async Task Upsert_WithATableNameThatIsNotAPlainIdentifier_StillPersistsViaTheFallbackAsync() {
    // The atomic path interpolates the table name into raw SQL, so it refuses anything that is
    // not a plain unquoted PostgreSQL identifier. These names come from generated perspective
    // infrastructure today, which is exactly why the guard has to hold if that ever changes —
    // and why bailing must cost the write nothing.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      JsonContextRegistry.CreateCombinedOptions(SerializationProfile.Persistence);

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

  [Test]
  public async Task Upsert_WhenTheSuppliedOptionsCarryNoResolver_FallsBackToTheUnionAndPersistsAsync() {
    // A provider is registered but its options have no TypeInfoResolver — nothing it can
    // serialize with. The path takes the framework's persistence union instead of trusting the
    // empty options, which would otherwise fail on the first model it tried to write.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = static () => new JsonSerializerOptions();

    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      context,
      "wh_per_order",
      testId,
      new Order { OrderId = new TestOrderId(testId), Amount = 33.75m, Status = "UnionFallback" },
      new PerspectiveMetadata { EventType = "OrderCreated", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      new PerspectiveScope());

    await using var read = CreateDbContext();
    var row = await read.Set<PerspectiveRow<Order>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull()
      .Because("options with no resolver are not a reason to fail the write — the union already "
             + "knows how to serialize every registered perspective model");
    await Assert.That(row!.Data.Amount).IsEqualTo(33.75m);
  }
}
