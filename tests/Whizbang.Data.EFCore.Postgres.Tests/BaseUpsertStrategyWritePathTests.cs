using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Which write path a perspective upsert takes, and what decides it (#967).
/// </summary>
/// <remarks>
/// <para>
/// The atomic statement takes its serialization options from the registry's persistence profile, which
/// every assembly's generated contexts join from their own module initializers. So on Npgsql the path is
/// available with no startup hook, and which path a strategy takes depends only on that strategy, never on
/// process-wide state a sibling test may have left behind.
/// </para>
/// <para>
/// The path is observed rather than inferred from the row: the atomic statement goes to the connection
/// directly, so Entity Framework creates no command for it, and the Entity Framework path always does.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class BaseUpsertStrategyWritePathTests : EFCoreTestBase {
  private static Order _order(Guid id, string status) => new() {
    OrderId = new TestOrderId(id),
    Amount = 12.5m,
    Status = status,
  };

  private static PerspectiveMetadata _metadata() => new() {
    EventType = "OrderCreated",
    EventId = Guid.CreateVersion7().ToString(),
    Timestamp = DateTime.UtcNow,
  };

  private WorkCoordinationDbContext _countingContext(EntityFrameworkCommandCounter counter) =>
    new(new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions).AddInterceptors(counter).Options);

  private async Task<PerspectiveRow<Order>?> _rowAsync(Guid id) {
    await using var read = CreateDbContext();
    return await read.Set<PerspectiveRow<Order>>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
  }

  [Test]
  public async Task Upsert_OnNpgsql_WithNoStartupHookRun_TakesTheAtomicPathAsync() {
    // Nothing here calls AddWhizbang or sets anything: the test assembly's generated module initializer
    // joined its contexts to the registry when it loaded, and that is all the atomic path needs.
    var counter = new EntityFrameworkCommandCounter();
    var id = Guid.CreateVersion7();

    await using (var context = _countingContext(counter)) {
      await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
        context, "wh_per_order", id, _order(id, "Atomic"), _metadata(), new PerspectiveScope());
    }

    await Assert.That(counter.Created).IsEqualTo(0)
      .Because("the atomic statement goes to the connection directly; any Entity Framework command means "
        + "the write fell back to the Entity Framework path");
    var row = await _rowAsync(id);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Status).IsEqualTo("Atomic");
    await Assert.That(row.Version).IsEqualTo(1);
  }

  [Test]
  public async Task Upsert_WithAStrategyThatChoosesTheEntityFrameworkPath_TakesItAsync() {
    var counter = new EntityFrameworkCommandCounter();
    var id = Guid.CreateVersion7();

    await using (var context = _countingContext(counter)) {
      await UpsertWritePath.Strategy(atomic: false).UpsertPerspectiveRowAsync(
        context, "wh_per_order", id, _order(id, "EntityFramework"), _metadata(), new PerspectiveScope());
    }

    await Assert.That(counter.Created).IsGreaterThan(0)
      .Because("a strategy that opts out of the atomic statement reads and saves through the context");
    var row = await _rowAsync(id);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Status).IsEqualTo("EntityFramework");
  }

  [Test]
  public async Task Upsert_TheChoiceIsPerInstance_AnOptedOutStrategyDoesNotMoveAnotherAsync() {
    // The hazard #967 removes: one test choosing a path must not choose it for the next. An opted-out
    // strategy writes first; a default strategy written after it still takes the atomic statement.
    var first = Guid.CreateVersion7();
    await using (var context = CreateDbContext()) {
      await UpsertWritePath.Strategy(atomic: false).UpsertPerspectiveRowAsync(
        context, "wh_per_order", first, _order(first, "OptedOut"), _metadata(), new PerspectiveScope());
    }

    var counter = new EntityFrameworkCommandCounter();
    var second = Guid.CreateVersion7();
    await using (var context = _countingContext(counter)) {
      await UpsertWritePath.Strategy(atomic: true).UpsertPerspectiveRowAsync(
        context, "wh_per_order", second, _order(second, "Default"), _metadata(), new PerspectiveScope());
    }

    await Assert.That(counter.Created).IsEqualTo(0)
      .Because("the path is chosen on the strategy instance, so no earlier choice can leak into this one");
    await Assert.That((await _rowAsync(second))!.Data.Status).IsEqualTo("Default");
  }

  [Test]
  public async Task Upsert_AnAtomicWrite_ReadsBackThroughTheRegistryOptionsAsync() {
    // The writer and the reader are one set of options: the registry's persistence profile.
    var id = Guid.CreateVersion7();
    await using (var context = CreateDbContext()) {
      await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
        context, "wh_per_order", id, _order(id, "RoundTrip"), _metadata(), new PerspectiveScope());
    }

    await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new Npgsql.NpgsqlCommand("SELECT data::text FROM wh_per_order WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    var stored = (string)(await cmd.ExecuteScalarAsync())!;

    var back = PerspectiveDocumentSerialization.Deserialize<Order>(stored);
    await Assert.That(back.Status).IsEqualTo("RoundTrip");
    await Assert.That(back.OrderId.Value).IsEqualTo(id);
  }
}
