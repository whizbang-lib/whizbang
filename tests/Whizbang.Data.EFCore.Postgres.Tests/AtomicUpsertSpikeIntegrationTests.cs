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
/// Spike test for the Path 1 atomic-UPSERT SQL shape. Issues the literal
/// <c>INSERT INTO wh_per_order … ON CONFLICT (id) DO UPDATE SET …</c> statement
/// via raw <see cref="NpgsqlCommand"/> using JSON produced by
/// <see cref="PerspectivePersistenceJsonContext"/>, then reads the row back through
/// EF Core to prove the byte format round-trips and the version-increment-on-conflict
/// semantics work end-to-end.
/// </summary>
/// <remarks>
/// This is the gating regression for slice 22b.3b. Once GREEN, the SQL + parameter
/// binding can be lifted straight into <c>BaseUpsertStrategy._tryAtomicUpsertAsync</c>.
/// </remarks>
public class AtomicUpsertSpikeIntegrationTests : EFCoreTestBase {
  [Test]
  public async Task AtomicInsert_NewRow_PersistedAndReadableByEFAsync() {
    // Arrange — first call EF once so the schema is created.
    await using var setupContext = CreateDbContext();
    await setupContext.Database.EnsureCreatedAsync();

    var testId = Guid.CreateVersion7();
    var order = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 250.00m,
      Status = "AtomicallyInserted"
    };
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    var pathOneOptions = PerspectivePersistenceJsonContext.CreateOptions(
      MessageJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
    var dataJson = JsonSerializer.Serialize(order, pathOneOptions.GetTypeInfo(typeof(Order)));
    var metadataJson = JsonSerializer.Serialize(metadata, pathOneOptions.GetTypeInfo(typeof(PerspectiveMetadata)));
    var scopeJson = JsonSerializer.Serialize(scope, pathOneOptions.GetTypeInfo(typeof(PerspectiveScope)));

    // Act — issue the atomic UPSERT directly.
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"
        INSERT INTO wh_per_order (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@id, @data::jsonb, @metadata::jsonb, @scope::jsonb, @now, @now, 1)
        ON CONFLICT (id) DO UPDATE SET
          data = EXCLUDED.data,
          metadata = EXCLUDED.metadata,
          updated_at = EXCLUDED.updated_at,
          version = wh_per_order.version + 1";
      cmd.Parameters.AddWithValue("id", testId);
      cmd.Parameters.AddWithValue("data", dataJson);
      cmd.Parameters.AddWithValue("metadata", metadataJson);
      cmd.Parameters.AddWithValue("scope", scopeJson);
      cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
      await cmd.ExecuteNonQueryAsync();
    }

    // Assert — EF can read back the atomically-inserted row without "Invalid token type".
    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.OrderId.Value).IsEqualTo(testId);
    await Assert.That(row.Data.Amount).IsEqualTo(250.00m);
    await Assert.That(row.Data.Status).IsEqualTo("AtomicallyInserted");
    await Assert.That(row.Version).IsEqualTo(1);
  }

  [Test]
  public async Task AtomicUpsert_ExistingRow_IncrementsVersionAndUpdatesDataAsync() {
    // Arrange — seed the row via EF first so we can confirm UPDATE branch fires on the second call.
    await using var setupContext = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    var testId = Guid.CreateVersion7();

    var initialOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 100.00m,
      Status = "Initial"
    };
    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();
    await strategy.UpsertPerspectiveRowAsync(setupContext, "wh_per_order", testId, initialOrder, metadata, scope);

    // Act — issue the atomic UPSERT with NEW data for the same id.
    var updatedOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 500.00m,
      Status = "AtomicallyUpdated"
    };
    var pathOneOptions = PerspectivePersistenceJsonContext.CreateOptions(
      MessageJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
    var dataJson = JsonSerializer.Serialize(updatedOrder, pathOneOptions.GetTypeInfo(typeof(Order)));
    var metadataJson = JsonSerializer.Serialize(metadata, pathOneOptions.GetTypeInfo(typeof(PerspectiveMetadata)));
    var scopeJson = JsonSerializer.Serialize(scope, pathOneOptions.GetTypeInfo(typeof(PerspectiveScope)));

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = @"
        INSERT INTO wh_per_order (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@id, @data::jsonb, @metadata::jsonb, @scope::jsonb, @now, @now, 1)
        ON CONFLICT (id) DO UPDATE SET
          data = EXCLUDED.data,
          metadata = EXCLUDED.metadata,
          updated_at = EXCLUDED.updated_at,
          version = wh_per_order.version + 1";
      cmd.Parameters.AddWithValue("id", testId);
      cmd.Parameters.AddWithValue("data", dataJson);
      cmd.Parameters.AddWithValue("metadata", metadataJson);
      cmd.Parameters.AddWithValue("scope", scopeJson);
      cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
      await cmd.ExecuteNonQueryAsync();
    }

    // Assert — EF reads the UPDATED row with version=2.
    await using var readContext = CreateDbContext();
    var row = await readContext.Set<PerspectiveRow<Order>>()
      .AsNoTracking()
      .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(2);
    await Assert.That(row.Data.Amount).IsEqualTo(500.00m);
    await Assert.That(row.Data.Status).IsEqualTo("AtomicallyUpdated");
  }
}
