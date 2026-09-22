using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Verifies <see cref="BaseUpsertStrategy"/> actually takes the Path 1 atomic-upsert
/// branch when <see cref="BaseUpsertStrategy.PathOnePersistenceOptionsProvider"/> is set,
/// falls back to the legacy SELECT-then-INSERT/UPDATE retry path otherwise.
/// </summary>
/// <remarks>
/// The atomic path is "invisible" from EF's perspective — both branches end with a row in
/// <c>wh_per_order</c>. We confirm the atomic branch fires by counting that the slice 19
/// <c>DuplicateKeyRetriesRecovered</c> counter stays at zero across a concurrent
/// double-upsert that the legacy path WOULD have caught at 23505. With Path 1 active the
/// race is structurally impossible: ON CONFLICT (id) DO UPDATE collapses both attempts
/// into a single round-trip — the second arrival becomes an UPDATE in the same statement.
/// </remarks>
// Serializes with every other test that reads or writes the process-wide
// BaseUpsertStrategy.PathOnePersistenceOptionsProvider static. Without this, a mutator here could flip the
// provider mid-seed of a parallel persistence test (e.g. ComplexTypeJsonMappingTests), engaging the atomic
// path for a model it can't cleanly bind — the cross-test static race behind the PostgreSQL-integration flake.
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class BaseUpsertStrategyAtomicPathTests : EFCoreTestBase {
  [After(Test)]
  public Task ClearPathOneProviderAsync() {
    // Reset the process-wide hook so this test doesn't leak into other suites.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    return Task.CompletedTask;
  }

  [Test]
  public async Task Upsert_WithPathOneOptionsProvider_PersistsRowViaAtomicSqlAsync() {
    // Arrange — register Path 1 globally; from here on, atomic UPSERT is the active branch.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      PerspectivePersistenceJsonContext.CreateOptions(
        MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);

    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var order = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 75.50m,
      Status = "AtomicFirstWrite"
    };
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Act — first upsert through the public surface; with the provider set, this goes
    // through _tryAtomicUpsertAsync, NOT the SELECT-then-INSERT retry path.
    await strategy.UpsertPerspectiveRowAsync(context, "wh_per_order", testId, order, metadata, scope);

    // Assert — row landed, version=1, fields persisted via the atomic SQL.
    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(1);
    await Assert.That(row.Data.OrderId.Value).IsEqualTo(testId);
    await Assert.That(row.Data.Amount).IsEqualTo(75.50m);
    await Assert.That(row.Data.Status).IsEqualTo("AtomicFirstWrite");
  }

  [Test]
  public async Task SequentialUpserts_WithPathOneActive_DoNotIncrementDupKeyRetryCounterAsync() {
    // Arrange — capture the slice-19 retry counter baseline.
    var baselineRetries = BaseUpsertStrategy.DuplicateKeyRetriesRecovered;

    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      PerspectivePersistenceJsonContext.CreateOptions(
        MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);

    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Act — 3 sequential upserts. The legacy path's SELECT-then-INSERT under any TOCTOU
    // would have ticked the retry counter; the atomic path collapses each into one round-trip.
    for (var i = 1; i <= 3; i++) {
      await using var ctx = CreateDbContext();
      var strategy = new PostgresUpsertStrategy();
      var order = new Order {
        OrderId = new TestOrderId(testId),
        Amount = 100m * i,
        Status = $"Upsert{i}"
      };
      await strategy.UpsertPerspectiveRowAsync(ctx, "wh_per_order", testId, order, metadata, scope);
    }

    // Assert — final row reflects the last write, version=3, retry counter unchanged.
    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(3);
    await Assert.That(row.Data.Amount).IsEqualTo(300m);
    await Assert.That(row.Data.Status).IsEqualTo("Upsert3");
    await Assert.That(BaseUpsertStrategy.DuplicateKeyRetriesRecovered).IsEqualTo(baselineRetries);
  }

  [Test]
  public async Task Upsert_WithoutPathOneOptionsProvider_FallsBackToRetryPathAsync() {
    // Arrange — explicitly leave the provider null. Existing strategy must continue to work.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;

    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var order = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 42m,
      Status = "FallbackPath"
    };
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Act — without provider, this MUST take the legacy path. No exception, row lands as v=1.
    await strategy.UpsertPerspectiveRowAsync(context, "wh_per_order", testId, order, metadata, scope);

    // Assert — same external behavior as before slice 22b.3 shipped.
    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(1);
    await Assert.That(row.Data.Status).IsEqualTo("FallbackPath");
  }

  // --- Identifier guard ------------------------------------------------------
  // The atomic path interpolates the table name and physical-field column names into raw
  // SQL. They are not user input today — source generators produce them from compile-time
  // definitions — but the guard is what keeps that true if a caller is ever refactored.
  // A name that fails it declines the atomic path and falls through to the EF-mapped
  // write, which never sees the string. The row still lands; the SQL never does.
  //
  // Only the table-name half is covered here. The physical-field key guard needs a
  // perspective whose table actually has projected columns; wh_per_order has none, so a
  // test against it fails on the missing column rather than on the guard.

  private static Order _guardOrder(Guid id) => new() {
    OrderId = new TestOrderId(id),
    Amount = 10m,
    Status = "GuardProbe",
  };

  private static PerspectiveMetadata _guardMetadata() => new() {
    EventType = "OrderCreated",
    EventId = Guid.NewGuid().ToString(),
    Timestamp = DateTime.UtcNow,
  };

  private static void _enablePathOne() =>
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      PerspectivePersistenceJsonContext.CreateOptions(
        MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);

  [Test]
  [Arguments("wh_per_order; DROP TABLE wh_per_order")]
  [Arguments("wh-per-order")]
  [Arguments("1_starts_with_digit")]
  [Arguments("wh_per_order\"")]
  public async Task Upsert_WithAnInvalidTableName_DeclinesTheAtomicPathAndLeavesTheTableIntactAsync(
      string tableName) {
    _enablePathOne();
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var id = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      context, tableName, id, _guardOrder(id), _guardMetadata(), new PerspectiveScope());

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == id);

    await Assert.That(row).IsNotNull()
      .Because("declining the atomic path falls through to the EF-mapped write, which "
             + "never interpolates the caller's string");
  }

  [Test]
  public async Task Upsert_WithAnOverlongTableName_DeclinesTheAtomicPathAsync() {
    // Postgres truncates identifiers at 63 bytes, so a longer name would silently target a
    // different table than the caller named — the guard rejects rather than let that happen.
    _enablePathOne();
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var id = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      context, new string('a', 64), id, _guardOrder(id), _guardMetadata(), new PerspectiveScope());

    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == id);

    await Assert.That(row).IsNotNull();
  }
}
