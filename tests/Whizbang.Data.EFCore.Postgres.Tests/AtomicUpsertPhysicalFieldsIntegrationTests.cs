using System;
using System.Collections.Generic;
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
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the slice 22b.3e invariant: <see cref="BaseUpsertStrategy._tryAtomicUpsertAsync"/>
/// now handles perspectives with physical-field shadow properties (denormalized scalar
/// columns and vector embeddings). Before 22b.3e, the strategy fell back to the slice-19
/// retry path whenever <c>PhysicalFieldValues</c> was non-null, leaving a burst of 23505
/// errors per bulk import on a consumer service's physical-field perspectives. Now the
/// column list, VALUES bindings, and DO UPDATE SET clause are extended dynamically.
/// </summary>
/// <remarks>
/// Integration test against real Postgres: the InMemory provider lets us verify
/// shadow-property routing but doesn't exercise the raw-SQL atomic UPSERT path.
/// </remarks>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class AtomicUpsertPhysicalFieldsIntegrationTests : IAsyncDisposable {
  static AtomicUpsertPhysicalFieldsIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private string _connectionString = null!;

  private sealed class TestDbContext(DbContextOptions<TestDbContext> opts) : DbContext(opts) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<global::Whizbang.Data.EFCore.Postgres.Tests.ProductPhysicalModel>>(entity => {
        entity.ToTable("wh_per_product_physical");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
        entity.Property<string?>("name").HasColumnName("name").HasMaxLength(200);
        entity.Property<decimal>("price").HasColumnName("price");
        entity.Property<string?>("category").HasColumnName("category").HasMaxLength(100);
        entity.HasIndex("name");
        entity.HasIndex("price");
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;
    await using var ctx = _createDbContext();
    await ctx.Database.EnsureCreatedAsync();

    // Wire Path 1 manually for the test scope.
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>
      Generated.PerspectivePersistenceJsonContext.CreateOptions(
        Generated.MessageJsonContext.Default,
        global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    BaseUpsertStrategy.PathOnePersistenceOptionsProvider = null;
    if (!string.IsNullOrEmpty(_testDatabaseName)) {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private TestDbContext _createDbContext() {
    var builder = new DbContextOptionsBuilder<TestDbContext>().UseNpgsql(_connectionString);
    return new TestDbContext(builder.Options);
  }

  [Test]
  public async Task AtomicUpsert_WithPhysicalFields_PersistsBothJsonbAndShadowColumnsAsync() {
    // Arrange
    await using var ctx = _createDbContext();
    var strategy = new PostgresUpsertStrategy();
    var id = Guid.CreateVersion7();
    var model = new global::Whizbang.Data.EFCore.Postgres.Tests.ProductPhysicalModel { Id = id, Name = "Widget", Price = 19.99m, Description = "JSONB only" };
    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var physicalFields = new Dictionary<string, object?> {
      { "name", model.Name },
      { "price", model.Price },
      { "category", "tools" }
    };

    // Act — should go through atomic path now (no fallback for physical fields).
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx, "wh_per_product_physical", id, model, metadata, new PerspectiveScope(), physicalFields);

    // Assert — shadow columns AND JSONB both populated.
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    var (name, price, category, version, descFromJsonb) = await conn.QuerySingleAsync<(string name, decimal price, string category, int version, string description)>(
      "SELECT name, price, category, version, data->>'Description' as description FROM wh_per_product_physical WHERE id = @id",
      new { id });

    await Assert.That(name).IsEqualTo("Widget");
    await Assert.That(price).IsEqualTo(19.99m);
    await Assert.That(category).IsEqualTo("tools");
    await Assert.That(version).IsEqualTo(1);
    await Assert.That(descFromJsonb).IsEqualTo("JSONB only");
  }

  [Test]
  public async Task AtomicUpsert_ExistingRowWithPhysicalFields_UpdatesShadowColumnsAndIncrementsVersionAsync() {
    // Arrange — seed.
    await using var ctx = _createDbContext();
    var strategy = new PostgresUpsertStrategy();
    var id = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx, "wh_per_product_physical", id,
      new global::Whizbang.Data.EFCore.Postgres.Tests.ProductPhysicalModel { Id = id, Name = "Original", Price = 5m, Description = "first" },
      metadata, scope,
      new Dictionary<string, object?> { { "name", "Original" }, { "price", 5m }, { "category", "old" } });

    // Act — UPDATE same id.
    await using var ctx2 = _createDbContext();
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx2, "wh_per_product_physical", id,
      new global::Whizbang.Data.EFCore.Postgres.Tests.ProductPhysicalModel { Id = id, Name = "Updated", Price = 99m, Description = "second" },
      metadata, scope,
      new Dictionary<string, object?> { { "name", "Updated" }, { "price", 99m }, { "category", "new" } });

    // Assert — UPDATE branch fired, version=2, all physical columns reflect second write.
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    var (name, price, category, version) = await conn.QuerySingleAsync<(string name, decimal price, string category, int version)>(
      "SELECT name, price, category, version FROM wh_per_product_physical WHERE id = @id",
      new { id });

    await Assert.That(name).IsEqualTo("Updated");
    await Assert.That(price).IsEqualTo(99m);
    await Assert.That(category).IsEqualTo("new");
    await Assert.That(version).IsEqualTo(2);
  }

  [Test]
  public async Task AtomicUpsert_WithNullPhysicalFieldValue_BindsAsDbNullAsync() {
    // Arrange — category is nullable; pass null to confirm DBNull binding works.
    await using var ctx = _createDbContext();
    var strategy = new PostgresUpsertStrategy();
    var id = Guid.CreateVersion7();
    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx, "wh_per_product_physical", id,
      new global::Whizbang.Data.EFCore.Postgres.Tests.ProductPhysicalModel { Id = id, Name = "NoCategory", Price = 1m, Description = null },
      metadata, new PerspectiveScope(),
      new Dictionary<string, object?> { { "name", "NoCategory" }, { "price", 1m }, { "category", null } });

    // Assert — category column is NULL at the DB level. Use raw NpgsqlDataReader so we
    // see DB NULL semantics directly rather than going through Dapper's nullable coercion.
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT category FROM wh_per_product_physical WHERE id = @id";
    cmd.Parameters.Add(new NpgsqlParameter("id", id));
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    var isNull = reader.IsDBNull(0);

    await Assert.That(isNull).IsTrue();
  }
}
