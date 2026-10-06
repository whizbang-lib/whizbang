// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="EFCorePostgresPerspectiveStore{TModel}"/>'s blue-green redirect
/// and provider split: a redirect of some other table leaves this store on its mapped table; a
/// redirect of this store's table reads (inside or outside a transaction) and purges the shadow
/// table without touching the mapped one; and a non-relational provider purges through the change
/// tracker.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
[Category("Shard1")]
public class PerspectiveStoreRedirectBranchTests : EFCoreTestBase {

  private readonly IWhizbangIdProvider<TestOrderId> _orderIds = TestOrderId.CreateProvider(new Uuid7IdProvider());

  [Test]
  public async Task Redirect_OfThisTable_ReadsAndPurgesTheShadow_OfAnotherTable_UsesTheMappedTableAsync() {
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "wh_per_order");
    var table = context.Model.FindEntityType(typeof(PerspectiveRow<Order>))!.GetTableName()!;
    var shadow = table + "_bg";
    var redirected = Guid.CreateVersion7();
    var unrelated = Guid.CreateVersion7();
    await store.UpsertByPartitionKeyAsync(redirected, _order(10m));
    await store.UpsertByPartitionKeyAsync(unrelated, _order(20m));
    // A plain copy of the one row is all the redirected read and purge need from a shadow table.
    await _execAsync($"CREATE TABLE \"{shadow}\" AS SELECT * FROM \"{table}\" WHERE id = '{redirected}'");

    using (PerspectiveTableRedirect.Begin("some_other_table", "some_other_table_bg")) {
      await store.PurgeByPartitionKeyAsync(unrelated);
    }
    await Assert.That(await _countAsync(table, unrelated)).IsEqualTo(0)
      .Because("a redirect of another table leaves this store purging its own mapped table");

    using (PerspectiveTableRedirect.Begin(table, shadow)) {
      var outside = await store.GetByPartitionKeyAsync(redirected);
      Order? inside;
      await using (var tx = await context.Database.BeginTransactionAsync()) {
        inside = await store.GetByPartitionKeyAsync(redirected);
        await tx.RollbackAsync();
      }
      await store.PurgeByPartitionKeyAsync(redirected);

      await Assert.That(outside!.Amount).IsEqualTo(10m)
        .Because("the redirected read comes from the shadow table");
      await Assert.That(inside!.Amount).IsEqualTo(10m)
        .Because("inside a transaction the shadow read enlists in it and still finds the row");
    }

    await Assert.That(await _countAsync(shadow, redirected)).IsEqualTo(0)
      .Because("the redirected purge deletes from the shadow table");
    await Assert.That(await _countAsync(table, redirected)).IsEqualTo(1)
      .Because("a rebuild never touches the mapped table");
  }

  [Test]
  public async Task Purge_OnANonRelationalProvider_RemovesThroughTheChangeTrackerAsync() {
    var options = new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", new InMemoryUpsertStrategy());
    var key = Guid.CreateVersion7();
    await store.UpsertByPartitionKeyAsync(key, new StoreTestModel { Name = "ToDelete", Value = 1 });

    await store.PurgeByPartitionKeyAsync(key);

    await Assert.That(await store.GetByPartitionKeyAsync(key)).IsNull();
  }

  // ===== Helpers =====

  private Order _order(decimal amount) => new() { OrderId = _orderIds.NewId(), Amount = amount, Status = "Created" };

  private async Task _execAsync(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<long> _countAsync(string table, Guid id) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE id = @id";
    cmd.Parameters.AddWithValue("id", id);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
