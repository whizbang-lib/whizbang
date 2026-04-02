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
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .AddInterceptors(new PhysicalFieldQueryInterceptor(), new PhysicalFieldMaterializationInterceptor());
    _context = new SplitModeTestDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();

    // Register physical field mappings so PhysicalFieldExpressionVisitor rewrites queries
    PhysicalFieldRegistry.Register<SplitTestModel>("Name", "name");
    PhysicalFieldRegistry.Register<SplitTestModel>("Price", "price");

    // Register hydrator so PhysicalFieldMaterializationInterceptor populates Data after query
    // This simulates what generated code does at startup (AOT-safe, no reflection)
    PhysicalFieldHydratorRegistry.Register<SplitTestModel>((data, entity) => {
      var row = (PerspectiveRow<SplitTestModel>)entity;
      var name = data.GetPropertyValue<string?>("name");
      if (name is not null) {
        row.Data.Name = name;
      }
      var price = data.GetPropertyValue<decimal>("price");
      if (price != default) {
        row.Data.Price = price;
      }
    });
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

  // ==========================================================================
  // Split Mode: Read-Back — Physical Fields Hydrated After Materialization
  // ==========================================================================

  /// <summary>
  /// Core bug repro: write a Split model, read it back via EF Core query,
  /// verify that physical fields (stripped from JSONB) are populated from physical columns.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task SplitMode_ReadBack_PhysicalFields_PopulatedFromColumnsAsync(CancellationToken cancellationToken) {
    // Arrange — write with physical fields stripped from JSONB
    await _insertSplitRowAsync("ReadBackTest", 99.99m, "read-back description");

    // Act — read via EF Core (AsNoTracking, same as ILensQuery)
    var row = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(cancellationToken);

    // Assert — physical fields must be populated from columns, not from JSONB (where they're null)
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("ReadBackTest")
      .Because("Physical field Name must be hydrated from column after materialization");
    await Assert.That(row.Data.Price).IsEqualTo(99.99m)
      .Because("Physical field Price must be hydrated from column after materialization");
    // Non-physical field should come from JSONB normally
    await Assert.That(row.Data.Description).IsEqualTo("read-back description");
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_ReadBack_MultipleRows_AllPopulatedAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Item A", 10.00m, "desc A");
    await _insertSplitRowAsync("Item B", 20.00m, "desc B");
    await _insertSplitRowAsync("Item C", 30.00m, "desc C");

    // Act
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .OrderBy(r => EF.Property<decimal>(r, "price"))
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Item A");
    await Assert.That(rows[0].Data.Price).IsEqualTo(10.00m);
    await Assert.That(rows[1].Data.Name).IsEqualTo("Item B");
    await Assert.That(rows[1].Data.Price).IsEqualTo(20.00m);
    await Assert.That(rows[2].Data.Name).IsEqualTo("Item C");
    await Assert.That(rows[2].Data.Price).IsEqualTo(30.00m);
  }

  // ==========================================================================
  // Split Mode: WHERE Clause — Physical Column Used, Results Populated
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Where_EqualityOnPhysicalField_FiltersAndPopulatesAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Widget", 50.00m, "widget desc");
    await _insertSplitRowAsync("Gadget", 75.00m, "gadget desc");

    // Act — WHERE on physical field
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .Where(r => r.Data.Name == "Widget")
        .ToListAsync(cancellationToken);

    // Assert — filtered correctly AND physical fields populated
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Widget");
    await Assert.That(rows[0].Data.Price).IsEqualTo(50.00m);
    await Assert.That(rows[0].Data.Description).IsEqualTo("widget desc");
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Where_ComparisonOnPhysicalField_FiltersAndPopulatesAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Cheap", 10.00m, "cheap");
    await _insertSplitRowAsync("Medium", 50.00m, "medium");
    await _insertSplitRowAsync("Expensive", 100.00m, "expensive");

    // Act — range filter on physical field
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .Where(r => r.Data.Price >= 50.00m)
        .OrderBy(r => r.Data.Price)
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Medium");
    await Assert.That(rows[0].Data.Price).IsEqualTo(50.00m);
    await Assert.That(rows[1].Data.Name).IsEqualTo("Expensive");
    await Assert.That(rows[1].Data.Price).IsEqualTo(100.00m);
  }

  // ==========================================================================
  // Split Mode: ORDER BY — Physical Column Used, Results Populated
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_OrderByDescending_PhysicalField_OrdersAndPopulatesAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("A", 10.00m, "a");
    await _insertSplitRowAsync("B", 30.00m, "b");
    await _insertSplitRowAsync("C", 20.00m, "c");

    // Act
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .OrderByDescending(r => r.Data.Price)
        .ToListAsync(cancellationToken);

    // Assert — ordered by price descending, physical fields populated
    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(rows[0].Data.Price).IsEqualTo(30.00m);
    await Assert.That(rows[1].Data.Price).IsEqualTo(20.00m);
    await Assert.That(rows[2].Data.Price).IsEqualTo(10.00m);
  }

  // ==========================================================================
  // Split Mode: SQL Verification — Physical Column Used, Not JSONB
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Where_GeneratedSql_UsesPhysicalColumn_NotJsonbAsync(CancellationToken cancellationToken) {
    // Arrange — build query (don't execute)
    var query = _context!.Set<PerspectiveRow<SplitTestModel>>()
        .Where(r => r.Data.Price >= 50.00m);

    // Act
    var sql = query.ToQueryString();

    // Assert — SQL should use physical column, NOT JSONB extraction
    await Assert.That(sql).DoesNotContain("data ->> 'Price'")
      .Because("Physical field Price should be queried from column, not JSONB");
    await Assert.That(sql.ToLowerInvariant()).Contains(".price >= ")
      .Because("Should use physical column name 'price' in WHERE clause");
  }

  // ==========================================================================
  // Split Mode: Pagination
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Skip_Take_WithPhysicalFieldOrder_PaginatesAndPopulatesAsync(CancellationToken cancellationToken) {
    // Arrange — insert 5 rows
    for (var i = 1; i <= 5; i++) {
      await _insertSplitRowAsync($"Item {i}", i * 10.00m, $"desc {i}");
    }

    // Act — page 2 (skip 2, take 2) ordered by price
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Data.Price)
        .Skip(2)
        .Take(2)
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows[0].Data.Price).IsEqualTo(30.00m);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Item 3");
    await Assert.That(rows[1].Data.Price).IsEqualTo(40.00m);
    await Assert.That(rows[1].Data.Name).IsEqualTo("Item 4");
  }

  // ==========================================================================
  // Split Mode: Advanced LINQ
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Select_ProjectPhysicalField_ReturnsValueAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Projected", 42.00m, "proj desc");

    // Act — project just the physical field
    var names = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .Select(r => r.Data.Name)
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(names).Count().IsEqualTo(1);
    await Assert.That(names[0]).IsEqualTo("Projected");
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Count_Where_PhysicalField_ReturnsCorrectCountAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("A", 10.00m, "a");
    await _insertSplitRowAsync("B", 50.00m, "b");
    await _insertSplitRowAsync("C", 100.00m, "c");

    // Act
    var count = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .CountAsync(r => r.Data.Price > 25.00m, cancellationToken);

    // Assert
    await Assert.That(count).IsEqualTo(2);
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Any_PhysicalField_TranslatesToSqlAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Widget", 10.00m, "w");

    // Act
    var exists = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .AnyAsync(r => r.Data.Name == "Widget", cancellationToken);
    var notExists = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .AnyAsync(r => r.Data.Name == "NonExistent", cancellationToken);

    // Assert
    await Assert.That(exists).IsTrue();
    await Assert.That(notExists).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Where_StringContainsOnPhysicalField_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Widget Alpha", 10.00m, "a");
    await _insertSplitRowAsync("Gadget Beta", 20.00m, "b");
    await _insertSplitRowAsync("Widget Gamma", 30.00m, "c");

    // Act — string Contains on physical field
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .Where(r => r.Data.Name.Contains("Widget"))
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows.All(r => r.Data.Name.Contains("Widget"))).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_Where_CombinedPhysicalAndJsonbFields_FiltersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange
    await _insertSplitRowAsync("Widget", 50.00m, "cheap widget");
    await _insertSplitRowAsync("Widget", 100.00m, "expensive widget");
    await _insertSplitRowAsync("Gadget", 50.00m, "cheap gadget");

    // Act — WHERE on physical field (Name) AND JSONB field (Description)
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .Where(r => r.Data.Name == "Widget" && r.Data.Description!.Contains("expensive"))
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Widget");
    await Assert.That(rows[0].Data.Price).IsEqualTo(100.00m);
    await Assert.That(rows[0].Data.Description).IsEqualTo("expensive widget");
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_OrderByThenBy_MixedFields_OrdersCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange — same name, different prices and descriptions
    await _insertSplitRowAsync("Widget", 30.00m, "z-desc");
    await _insertSplitRowAsync("Widget", 10.00m, "a-desc");
    await _insertSplitRowAsync("Gadget", 20.00m, "m-desc");

    // Act — OrderBy physical field, ThenBy physical field
    var rows = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Data.Name)
        .ThenBy(r => r.Data.Price)
        .ToListAsync(cancellationToken);

    // Assert — Gadget first (alphabetically), then Widgets ordered by price
    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Gadget");
    await Assert.That(rows[1].Data.Name).IsEqualTo("Widget");
    await Assert.That(rows[1].Data.Price).IsEqualTo(10.00m);
    await Assert.That(rows[2].Data.Name).IsEqualTo("Widget");
    await Assert.That(rows[2].Data.Price).IsEqualTo(30.00m);
  }

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_OrderBy_GeneratedSql_UsesPhysicalColumn_NotJsonbAsync(CancellationToken cancellationToken) {
    // Arrange
    var query = _context!.Set<PerspectiveRow<SplitTestModel>>()
        .OrderBy(r => r.Data.Price);

    // Act
    var sql = query.ToQueryString();

    // Assert
    await Assert.That(sql).DoesNotContain("data ->> 'Price'")
      .Because("Physical field Price should be ordered by column, not JSONB");
    await Assert.That(sql.ToLowerInvariant()).Contains("order by")
      .Because("Should have ORDER BY clause");
  }

  // ==========================================================================
  // Split Mode: GroupBy
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task SplitMode_GroupBy_PhysicalField_GroupsCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange — multiple items per name
    await _insertSplitRowAsync("Widget", 10.00m, "w1");
    await _insertSplitRowAsync("Widget", 20.00m, "w2");
    await _insertSplitRowAsync("Gadget", 30.00m, "g1");

    // Act — group by physical field
    var groups = await _context!.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .GroupBy(r => r.Data.Name)
        .Select(g => new { Name = g.Key, Count = g.Count(), TotalPrice = g.Sum(r => r.Data.Price) })
        .OrderBy(g => g.Name)
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(groups).Count().IsEqualTo(2);
    await Assert.That(groups[0].Name).IsEqualTo("Gadget");
    await Assert.That(groups[0].Count).IsEqualTo(1);
    await Assert.That(groups[1].Name).IsEqualTo("Widget");
    await Assert.That(groups[1].Count).IsEqualTo(2);
    await Assert.That(groups[1].TotalPrice).IsEqualTo(30.00m);
  }

  // ==========================================================================
  // Extracted Mode: Regression
  // ==========================================================================

  [Test]
  [Timeout(60000)]
  public async Task ExtractedMode_ReadBack_PhysicalFieldsStillWorkAsync(CancellationToken cancellationToken) {
    // Arrange — in Extracted mode, physical fields are in BOTH JSONB and columns.
    // The interceptor should still work (overlay doesn't break existing values).
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();

    // In Extracted mode, the model is NOT stripped — physical fields stay in JSONB
    var fullModel = new SplitTestModel {
      Name = "ExtractedWidget",
      Price = 77.77m,
      Description = "extracted desc"
    };
    var physicalFieldValues = new Dictionary<string, object?> {
      ["name"] = fullModel.Name,
      ["price"] = fullModel.Price
    };
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    // Act — write with full model (Extracted mode: JSONB has all fields)
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_test", testId, fullModel, metadata, new PerspectiveScope(),
        physicalFieldValues, default);
    await _context!.SaveChangesAsync(cancellationToken);

    // Read back
    var row = await _context.Set<PerspectiveRow<SplitTestModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == (Guid)testId, cancellationToken);

    // Assert — physical fields should be populated (from JSONB + overlay from columns)
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("ExtractedWidget");
    await Assert.That(row.Data.Price).IsEqualTo(77.77m);
    await Assert.That(row.Data.Description).IsEqualTo("extracted desc");
  }

  // ==========================================================================
  // Helpers
  // ==========================================================================

  /// <summary>
  /// Inserts a Split-mode row with physical fields in columns and stripped from JSONB.
  /// Simulates what the generated PerspectiveRunner does.
  /// </summary>
  private async Task _insertSplitRowAsync(string name, decimal price, string description) {
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var strippedModel = new SplitTestModel { Description = description };
    var physicalFieldValues = new Dictionary<string, object?> {
      ["name"] = name,
      ["price"] = price
    };
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_test", testId, strippedModel, metadata, scope,
        physicalFieldValues, default);
    await _context!.SaveChangesAsync();
  }
}
