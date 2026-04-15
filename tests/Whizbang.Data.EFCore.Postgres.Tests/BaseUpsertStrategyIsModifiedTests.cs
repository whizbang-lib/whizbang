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

  // ─── Phase 1: Scope exclusion tests ───────────────────────────────────────
  // SECURITY: Scope is a security boundary — it must only be set on INSERT.
  // These tests verify that scope is never overwritten by subsequent UPDATEs.

  /// <summary>
  /// INSERT with scope A, then UPDATE with scope B → DB must still have scope A.
  /// This is the core bug: scope was being overwritten on every event.
  /// </summary>
  /// <tests>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs:_upsertCoreAsync</tests>
  [Test]
  public async Task UpsertAsync_WhenRecordExists_DoesNotUpdateScopeColumnAsync() {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    var scopeA = new PerspectiveScope { TenantId = "tenant-A", UserId = "user-1" };
    var scopeB = new PerspectiveScope { TenantId = "tenant-B", UserId = "user-2" };

    // INSERT with scope A
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 100m, Status = "Created" },
          metadata, scopeA);
    }

    // Act - UPDATE with scope B
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 200m, Status = "Updated" },
          metadata, scopeB);
    }

    // Assert - scope must still be A
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (tenantId, userId) = await conn.QuerySingleAsync<(string tenantId, string userId)>(
        "SELECT scope->>'t' as tenantId, scope->>'u' as userId FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(tenantId).IsEqualTo("tenant-A");
    await Assert.That(userId).IsEqualTo("user-1");
  }

  /// <summary>
  /// INSERT path must still write scope from the parameter.
  /// </summary>
  [Test]
  public async Task UpsertAsync_WhenRecordDoesNotExist_SetsScopeFromParameterAsync() {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope { TenantId = "tenant-insert", CustomerId = "cust-42" };

    // Act - INSERT
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 50m, Status = "New" },
          metadata, scope);
    }

    // Assert - scope must be set
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (tenantId, customerId) = await conn.QuerySingleAsync<(string tenantId, string customerId)>(
        "SELECT scope->>'t' as tenantId, scope->>'c' as customerId FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(tenantId).IsEqualTo("tenant-insert");
    await Assert.That(customerId).IsEqualTo("cust-42");
  }

  /// <summary>
  /// Scope must survive N sequential updates with different scope values.
  /// </summary>
  [Test]
  public async Task UpsertAsync_MultipleUpdates_PreservesOriginalScopeAsync() {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderUpdated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var originalScope = new PerspectiveScope { TenantId = "tenant-original", OrganizationId = "org-1" };

    // INSERT with original scope
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 0m, Status = "New" },
          metadata, originalScope);
    }

    // Act - 5 updates, each with a different scope
    for (var i = 1; i <= 5; i++) {
      await using var context = CreateDbContext();
      var differentScope = new PerspectiveScope { TenantId = $"tenant-{i}", OrganizationId = $"org-{i}" };
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = i * 10m, Status = $"Update{i}" },
          metadata, differentScope);
    }

    // Assert - scope must still be original
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (tenantId, orgId, version) = await conn.QuerySingleAsync<(string tenantId, string orgId, int version)>(
        "SELECT scope->>'t' as tenantId, scope->>'o' as orgId, version FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(tenantId).IsEqualTo("tenant-original");
    await Assert.That(orgId).IsEqualTo("org-1");
    await Assert.That(version).IsEqualTo(6); // 1 insert + 5 updates
  }

  /// <summary>
  /// Physical fields path must also exclude scope from UPDATE.
  /// </summary>
  [Test]
  public async Task UpsertWithPhysicalFields_WhenRecordExists_DoesNotUpdateScopeColumnAsync() {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scopeA = new PerspectiveScope { TenantId = "tenant-phys-A" };
    var scopeB = new PerspectiveScope { TenantId = "tenant-phys-B" };

    // INSERT with scope A (no physical fields on initial insert)
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 100m, Status = "Created" },
          metadata, scopeA);
    }

    // Act - UPDATE with physical fields and scope B
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 200m, Status = "Updated" },
          metadata, scopeB,
          new Dictionary<string, object?>());
    }

    // Assert - scope must still be A
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var tenantId = await conn.QuerySingleAsync<string>(
        "SELECT scope->>'t' FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(tenantId).IsEqualTo("tenant-phys-A");
  }

  /// <summary>
  /// When UPDATE passes an empty/default scope, the original scope must be preserved.
  /// </summary>
  [Test]
  public async Task UpsertAsync_WithDefaultScope_WhenRecordExists_PreservesScopeAsync() {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var originalScope = new PerspectiveScope { TenantId = "tenant-keep", UserId = "user-keep" };

    // INSERT with populated scope
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 100m, Status = "Created" },
          metadata, originalScope);
    }

    // Act - UPDATE with empty/default scope
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
          context, "wh_per_order", testId,
          new Order { OrderId = new TestOrderId(testId), Amount = 200m, Status = "Updated" },
          metadata, new PerspectiveScope());
    }

    // Assert - original scope must be preserved
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (tenantId, userId) = await conn.QuerySingleAsync<(string tenantId, string userId)>(
        "SELECT scope->>'t' as tenantId, scope->>'u' as userId FROM wh_per_order WHERE id = @id",
        new { id = testId });

    await Assert.That(tenantId).IsEqualTo("tenant-keep");
    await Assert.That(userId).IsEqualTo("user-keep");
  }

  // ─── End: Scope exclusion tests ─────────────────────────────────────────

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
