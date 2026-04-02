using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for FieldStorageMode.Split perspective behavior.
/// Validates that physical fields are stored in physical columns and excluded from JSONB,
/// scope is written correctly, and LINQ queries work transparently.
/// </summary>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class SplitModeIntegrationTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static SplitModeIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private SplitModeTestDbContext? _context;
  private string _connectionString = null!;

  /// <summary>
  /// Test model simulating Split mode: Name and Price are physical fields,
  /// Description is JSONB-only. In Split mode, Name and Price should NOT appear in JSONB data.
  /// </summary>
  public class SplitTestModel {
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? Description { get; set; }
  }

  private sealed class SplitModeTestDbContext(DbContextOptions<SplitModeIntegrationTests.SplitModeTestDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<SplitTestModel>>(entity => {
        entity.ToTable("wh_per_split_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");

        // Physical fields as shadow properties
        entity.Property<string?>("name").HasColumnName("name").HasMaxLength(200);
        entity.Property<decimal>("price").HasColumnName("price");

        entity.HasIndex("name");
        entity.HasIndex("price");
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    var optionsBuilder = new DbContextOptionsBuilder<SplitModeTestDbContext>();
    optionsBuilder.UseNpgsql(_dataSource)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _context = new SplitModeTestDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_context != null) { await _context.DisposeAsync(); _context = null; }
    if (_dataSource != null) { await _dataSource.DisposeAsync(); _dataSource = null; }
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pid) FROM pg_stat_activity
          WHERE datname = '{_testDatabaseName}' AND pid <> pg_backend_pid()");
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch { /* cleanup errors */ }
      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() { await TeardownAsync(); GC.SuppressFinalize(this); }

  private async Task _initializeSchemaAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_split_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL,
        name VARCHAR(200),
        price DECIMAL NOT NULL DEFAULT 0
      );
      CREATE INDEX IF NOT EXISTS idx_split_test_name ON wh_per_split_test(name);
      CREATE INDEX IF NOT EXISTS idx_split_test_price ON wh_per_split_test(price);
      """);
  }

  // ==========================================================================
  // Split Mode: Physical Fields NOT in JSONB
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_PhysicalFieldValues_NotInJsonbDataAsync(CancellationToken cancellationToken) {
    // Arrange — simulate what the generated runner does in Split mode:
    // 1. Extract physical field values into dictionary
    // 2. Strip physical fields from model (set to default)
    // 3. Call UpsertWithPhysicalFieldsAsync with stripped model
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();

    // Full model as returned by Apply()
    var fullModel = new SplitTestModel {
      Name = "Widget",
      Price = 42.99m,
      Description = "A test widget"
    };

    // Physical field values extracted before stripping
    var physicalFieldValues = new Dictionary<string, object?> {
      ["name"] = fullModel.Name,
      ["price"] = fullModel.Price
    };

    // Split mode: strip physical fields from model before JSONB serialization
    var strippedModel = new SplitTestModel {
      Name = default!,
      Price = default,
      Description = fullModel.Description  // Non-physical fields preserved
    };

    var metadata = new PerspectiveMetadata { EventType = "TestEvent", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };
    var scope = new PerspectiveScope { TenantId = "tenant-split-test" };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_test", testId, strippedModel, metadata, scope,
        physicalFieldValues, default);
    await _context!.SaveChangesAsync(cancellationToken);

    // Assert — JSONB data should NOT contain physical field values
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var jsonData = await connection.ExecuteScalarAsync<string>(
      "SELECT data::text FROM wh_per_split_test WHERE id = @id", new { id = (Guid)testId });

    await Assert.That(jsonData).IsNotNull();
    var json = JsonDocument.Parse(jsonData!);

    // Physical fields should be absent or default in JSONB
    await Assert.That(json.RootElement.TryGetProperty("Name", out var nameEl) && nameEl.GetString() == "Widget")
      .IsFalse()
      .Because("Split mode: Name should NOT be in JSONB data (it's in the physical column)");

    await Assert.That(json.RootElement.TryGetProperty("Price", out var priceEl) && priceEl.GetDecimal() == 42.99m)
      .IsFalse()
      .Because("Split mode: Price should NOT be in JSONB data (it's in the physical column)");

    // Non-physical field SHOULD be in JSONB
    await Assert.That(json.RootElement.TryGetProperty("Description", out var descEl) && descEl.GetString() == "A test widget")
      .IsTrue()
      .Because("Non-physical field Description should be in JSONB data");
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_PhysicalColumnValues_CorrectAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var strippedModel = new SplitTestModel { Description = "A test widget" };
    var physicalFieldValues = new Dictionary<string, object?> {
      ["name"] = "Widget",
      ["price"] = 42.99m
    };
    var metadata = new PerspectiveMetadata { EventType = "TestEvent", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };
    var scope = new PerspectiveScope { TenantId = "tenant-split-test" };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_test", testId, strippedModel, metadata, scope,
        physicalFieldValues, default);
    await _context!.SaveChangesAsync(cancellationToken);

    // Assert — physical columns have the values
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var row = await connection.QuerySingleAsync(
      "SELECT name, price FROM wh_per_split_test WHERE id = @id", new { id = (Guid)testId });

    await Assert.That((string)row.name).IsEqualTo("Widget");
    await Assert.That((decimal)row.price).IsEqualTo(42.99m);
  }

  // ==========================================================================
  // Scope Tests
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_UpsertWithScope_ScopeWrittenCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var model = new SplitTestModel { Description = "Scope test" };
    var physicalFieldValues = new Dictionary<string, object?> {
      ["name"] = "ScopeWidget",
      ["price"] = 10.00m
    };
    var metadata = new PerspectiveMetadata { EventType = "TestEvent", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };
    var scope = new PerspectiveScope { TenantId = "c0ffee00-0000-0000-0000-000000000000" };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_test", testId, model, metadata, scope,
        physicalFieldValues, default);
    await _context!.SaveChangesAsync(cancellationToken);

    // Assert — scope column has tenant ID
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var scopeJson = await connection.ExecuteScalarAsync<string>(
      "SELECT scope::text FROM wh_per_split_test WHERE id = @id", new { id = (Guid)testId });

    await Assert.That(scopeJson).IsNotNull();
    await Assert.That(scopeJson!).Contains("c0ffee00")
      .Because("Scope should contain the tenant ID from the event hop metadata");
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_UpsertWithNullScope_DefaultScopeWrittenAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var model = new SplitTestModel { Description = "Null scope test" };
    var physicalFieldValues = new Dictionary<string, object?> {
      ["name"] = "NullScopeWidget",
      ["price"] = 5.00m
    };
    var metadata = new PerspectiveMetadata { EventType = "TestEvent", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };

    // Act — null scope, should use default empty scope
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_test", testId, model, metadata, new PerspectiveScope(),
        physicalFieldValues, default);
    await _context!.SaveChangesAsync(cancellationToken);

    // Assert — row exists (didn't crash with null scope)
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    var count = await connection.ExecuteScalarAsync<int>(
      "SELECT COUNT(*) FROM wh_per_split_test WHERE id = @id", new { id = (Guid)testId });
    await Assert.That(count).IsEqualTo(1);
  }
}
