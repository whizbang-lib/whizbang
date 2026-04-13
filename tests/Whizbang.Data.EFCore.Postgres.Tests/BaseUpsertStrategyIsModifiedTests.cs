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
/// Integration tests for the IsModified fix in <see cref="BaseUpsertStrategy"/>.
/// Uses a real PostgreSQL database to validate that the Data property is always
/// marked as modified, ensuring JSONB changes are persisted even when using
/// polymorphic model configurations.
/// </summary>
/// <remarks>
/// These tests use simple scalar models to avoid TrackedGuid mapping issues.
/// The key validation is that version increments and data changes persist correctly.
/// </remarks>
[NotInParallel("EFCorePostgresTests")]
public class BaseUpsertStrategyIsModifiedTests : EFCoreTestBase {
  /// <summary>
  /// Tests that version is incremented on each upsert, confirming the update path is taken.
  /// This validates that the IsModified fix doesn't break the basic upsert flow.
  /// </summary>
  [Test]
  public async Task Upsert_WhenExistingRowUpdated_IncrementsVersionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    var initialOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 100.00m,
      Status = "Created"
    };

    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Create initial record
    await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        initialOrder,
        metadata,
        scope);

    // Act - Update the same row
    await using var context2 = CreateDbContext();
    var updatedOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 200.00m,
      Status = "Updated"
    };

    await strategy.UpsertPerspectiveRowAsync(
        context2,
        "wh_per_order",
        testId,
        updatedOrder,
        metadata,
        scope);

    // Assert - Use raw SQL to verify the data was persisted correctly
    // This avoids any EF Core materialization issues with TrackedGuid
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var (version, amount, status) = await conn.QuerySingleAsync<(int version, decimal amount, string status)>(
        "SELECT version, (data->>'Amount')::decimal as amount, data->>'Status' as status FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(version).IsEqualTo(2);
    await Assert.That(amount).IsEqualTo(200.00m);
    await Assert.That(status).IsEqualTo("Updated");
  }

  /// <summary>
  /// Tests that multiple sequential updates all persist their changes to the JSONB column.
  /// This is the key scenario that the IsModified fix addresses - ensuring each update
  /// writes the Data column even if the object reference might be the same.
  /// </summary>
  [Test]
  public async Task Upsert_WhenMultipleSequentialUpdates_PersistsAllChangesAsync() {
    // Arrange
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderUpdated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Create initial record
    await using (var context = CreateDbContext()) {
      var strategy = new PostgresUpsertStrategy();
      var initialOrder = new Order {
        OrderId = new TestOrderId(testId),
        Amount = 0m,
        Status = "New"
      };

      await strategy.UpsertPerspectiveRowAsync(
          context,
          "wh_per_order",
          testId,
          initialOrder,
          metadata,
          scope);
    }

    // Act - Perform 5 sequential updates
    for (var i = 1; i <= 5; i++) {
      await using var context = CreateDbContext();
      var strategy = new PostgresUpsertStrategy();

      var updatedOrder = new Order {
        OrderId = new TestOrderId(testId),
        Amount = i * 10m,
        Status = $"Update{i}"
      };

      await strategy.UpsertPerspectiveRowAsync(
          context,
          "wh_per_order",
          testId,
          updatedOrder,
          metadata,
          scope);
    }

    // Assert - Use raw SQL to verify final state
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var (version, amount, status) = await conn.QuerySingleAsync<(int version, decimal amount, string status)>(
        "SELECT version, (data->>'Amount')::decimal as amount, data->>'Status' as status FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(version).IsEqualTo(6);  // Initial + 5 updates
    await Assert.That(amount).IsEqualTo(50m);  // 5 * 10
    await Assert.That(status).IsEqualTo("Update5");
  }

  /// <summary>
  /// Tests that the InMemoryUpsertStrategy (which doesn't clear change tracker)
  /// also correctly persists data changes.
  /// </summary>
  [Test]
  public async Task InMemoryStrategy_WhenDataUpdated_PersistsChangesAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var testId = Guid.CreateVersion7();

    var initialOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 50.00m,
      Status = "Initial"
    };

    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        initialOrder,
        metadata,
        scope);

    // Act - Update using InMemoryUpsertStrategy (same context, no change tracker clear)
    var updatedOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 150.00m,
      Status = "UpdatedViaInMemory"
    };

    await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        updatedOrder,
        metadata,
        scope);

    // Assert - Use raw SQL to verify
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var (version, amount, status) = await conn.QuerySingleAsync<(int version, decimal amount, string status)>(
        "SELECT version, (data->>'Amount')::decimal as amount, data->>'Status' as status FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(version).IsEqualTo(2);
    await Assert.That(amount).IsEqualTo(150.00m);
    await Assert.That(status).IsEqualTo("UpdatedViaInMemory");
  }
}
