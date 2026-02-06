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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for physical field support with real PostgreSQL.
/// Tests roundtrip persistence, querying by physical columns, and mixed JSONB + physical queries.
/// </summary>
/// <remarks>
/// These tests validate that:
/// 1. Physical field values are correctly persisted to PostgreSQL columns
/// 2. Physical columns can be queried directly (WHERE clause optimization)
/// 3. Mixed queries combining JSONB and physical fields work correctly
/// 4. Update operations correctly update both JSONB and physical columns
/// </remarks>
/// <tests>No tests found</tests>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class PhysicalFieldIntegrationTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static PhysicalFieldIntegrationTests() {
    // Configure Npgsql for UTC timestamps
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private PhysicalFieldIntegrationDbContext? _context;
  private string _connectionString = null!;

  /// <summary>
  /// Test model with physical fields for integration testing.
  /// </summary>
  public class ProductSearchModel {
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string? Category { get; init; }
    public bool IsActive { get; init; }
    public string? Description { get; init; }  // JSONB only field
    public List<string> Tags { get; init; } = [];  // JSONB only field
  }

  /// <summary>
  /// DbContext that configures physical fields as shadow properties for PostgreSQL.
  /// </summary>
  private sealed class PhysicalFieldIntegrationDbContext : DbContext {
    public PhysicalFieldIntegrationDbContext(DbContextOptions<PhysicalFieldIntegrationDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<ProductSearchModel>>(entity => {
        entity.ToTable("wh_per_product_search");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // Data stored as JSONB (full model in Extracted mode)
        entity.Property(e => e.Data)
            .HasColumnName("data")
            .HasColumnType("jsonb");

        // Metadata as JSONB
        entity.Property(e => e.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb");

        // Scope as JSONB
        entity.Property(e => e.Scope)
            .HasColumnName("scope")
            .HasColumnType("jsonb");

        // Physical fields as shadow properties
        // These are indexed copies of model properties for query optimization
        entity.Property<string?>("name").HasColumnName("name").HasMaxLength(200);
        entity.Property<decimal>("price").HasColumnName("price");
        entity.Property<string?>("category").HasColumnName("category").HasMaxLength(100);
        entity.Property<bool>("is_active").HasColumnName("is_active");

        // Indexes on physical columns for optimized queries
        entity.HasIndex("name");
        entity.HasIndex("price");
        entity.HasIndex("category");
        entity.HasIndex("is_active");
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    // Initialize shared container
    await SharedPostgresContainer.InitializeAsync();

    // Create unique database for THIS test
    _testDatabaseName = $"test_{Guid.NewGuid():N}";

    // Connect to main database to create the test database
    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    // Build connection string for the test database
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;

    // Configure Npgsql data source with JSON support
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    // Configure DbContext
    var optionsBuilder = new DbContextOptionsBuilder<PhysicalFieldIntegrationDbContext>();
    optionsBuilder.UseNpgsql(_dataSource);
    _context = new PhysicalFieldIntegrationDbContext(optionsBuilder.Options);

    // Initialize database schema
    await _initializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_context != null) {
      await _context.DisposeAsync();
      _context = null;
    }

    if (_dataSource != null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }

    // Drop the test database
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

        // Terminate connections
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");

        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName}");
      } catch {
        // Ignore cleanup errors
      }

      _testDatabaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private async Task _initializeSchemaAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    // Create the table with physical columns
    await connection.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_product_search (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL,
        -- Physical columns (indexed copies for query optimization)
        name VARCHAR(200),
        price DECIMAL NOT NULL DEFAULT 0,
        category VARCHAR(100),
        is_active BOOLEAN NOT NULL DEFAULT FALSE
      );

      CREATE INDEX IF NOT EXISTS idx_wh_per_product_search_name ON wh_per_product_search(name);
      CREATE INDEX IF NOT EXISTS idx_wh_per_product_search_price ON wh_per_product_search(price);
      CREATE INDEX IF NOT EXISTS idx_wh_per_product_search_category ON wh_per_product_search(category);
      CREATE INDEX IF NOT EXISTS idx_wh_per_product_search_is_active ON wh_per_product_search(is_active);
      """);
  }

  // ==================== Physical Field Persistence Tests ====================

  [Test]
  [Timeout(60000)]
  public async Task UpsertWithPhysicalFields_WhenRecordDoesNotExist_PersistsToPostgresAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var model = new ProductSearchModel {
      Name = "Widget Pro",
      Price = 29.99m,
      Category = "Electronics",
      IsActive = true,
      Description = "A premium widget for professionals",
      Tags = ["premium", "electronics", "pro"]
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope { TenantId = "tenant-1" };
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "price", model.Price },
      { "category", model.Category },
      { "is_active", model.IsActive }
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!,
        "wh_per_product_search",
        testId,
        model,
        metadata,
        scope,
        physicalFieldValues,
        cancellationToken);

    // Assert - verify via raw SQL that both JSONB and physical columns have correct values
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = await connection.QuerySingleAsync<dynamic>(
        "SELECT data, name, price, category, is_active FROM wh_per_product_search WHERE id = @Id",
        new { Id = testId });

    // Verify physical columns
    await Assert.That((string?)result.name).IsEqualTo("Widget Pro");
    await Assert.That((decimal)result.price).IsEqualTo(29.99m);
    await Assert.That((string?)result.category).IsEqualTo("Electronics");
    await Assert.That((bool)result.is_active).IsTrue();

    // Verify JSONB contains full model (Extracted mode)
    var jsonData = JsonDocument.Parse((string)result.data);
    await Assert.That(jsonData.RootElement.GetProperty("Name").GetString()).IsEqualTo("Widget Pro");
    await Assert.That(jsonData.RootElement.GetProperty("Price").GetDecimal()).IsEqualTo(29.99m);
    await Assert.That(jsonData.RootElement.GetProperty("Description").GetString())
        .IsEqualTo("A premium widget for professionals");
    await Assert.That(jsonData.RootElement.GetProperty("Tags").GetArrayLength()).IsEqualTo(3);
  }

  [Test]
  [Timeout(60000)]
  public async Task UpsertWithPhysicalFields_WhenRecordExists_UpdatesBothJsonbAndPhysicalColumnsAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope { TenantId = "tenant-1" };

    // Create initial record
    var initialModel = new ProductSearchModel {
      Name = "Basic Widget",
      Price = 9.99m,
      Category = "Accessories",
      IsActive = true,
      Description = "A basic widget"
    };
    var initialPhysicalValues = new Dictionary<string, object?> {
      { "name", initialModel.Name },
      { "price", initialModel.Price },
      { "category", initialModel.Category },
      { "is_active", initialModel.IsActive }
    };
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!,
        "wh_per_product_search",
        testId,
        initialModel,
        metadata,
        scope,
        initialPhysicalValues,
        cancellationToken);

    // Act - update the record
    var updatedModel = new ProductSearchModel {
      Name = "Premium Widget",
      Price = 49.99m,
      Category = "Premium",
      IsActive = false,  // Changed to inactive
      Description = "Upgraded to premium",
      Tags = ["premium"]
    };
    var updatedPhysicalValues = new Dictionary<string, object?> {
      { "name", updatedModel.Name },
      { "price", updatedModel.Price },
      { "category", updatedModel.Category },
      { "is_active", updatedModel.IsActive }
    };
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!,
        "wh_per_product_search",
        testId,
        updatedModel,
        metadata,
        scope,
        updatedPhysicalValues,
        cancellationToken);

    // Assert
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = await connection.QuerySingleAsync<dynamic>(
        "SELECT data, name, price, category, is_active, version FROM wh_per_product_search WHERE id = @Id",
        new { Id = testId });

    // Verify physical columns updated
    await Assert.That((string?)result.name).IsEqualTo("Premium Widget");
    await Assert.That((decimal)result.price).IsEqualTo(49.99m);
    await Assert.That((string?)result.category).IsEqualTo("Premium");
    await Assert.That((bool)result.is_active).IsFalse();
    await Assert.That((int)result.version).IsEqualTo(2);

    // Verify JSONB updated
    var jsonData = JsonDocument.Parse((string)result.data);
    await Assert.That(jsonData.RootElement.GetProperty("Name").GetString()).IsEqualTo("Premium Widget");
    await Assert.That(jsonData.RootElement.GetProperty("Description").GetString())
        .IsEqualTo("Upgraded to premium");
  }

  // ==================== Physical Column Query Tests ====================

  [Test]
  [Timeout(60000)]
  public async Task QueryByPhysicalColumn_WhereClauseOnName_UsesIndexAsync(CancellationToken cancellationToken) {
    // Arrange - seed multiple products
    await _seedProductsAsync(cancellationToken);

    // Act - query by physical column (name)
    var results = await _context!.Set<PerspectiveRow<ProductSearchModel>>()
        .Where(r => EF.Property<string?>(r, "name") == "Widget Alpha")
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Widget Alpha");
    await Assert.That(results[0].Data.Price).IsEqualTo(10.00m);
  }

  [Test]
  [Timeout(60000)]
  public async Task QueryByPhysicalColumn_WhereClauseOnPrice_ReturnsFilteredResultsAsync(CancellationToken cancellationToken) {
    // Arrange
    await _seedProductsAsync(cancellationToken);

    // Act - query by physical column (price range)
    var results = await _context!.Set<PerspectiveRow<ProductSearchModel>>()
        .Where(r => EF.Property<decimal>(r, "price") >= 20.00m)
        .OrderBy(r => EF.Property<decimal>(r, "price"))
        .ToListAsync(cancellationToken);

    // Assert - should return Widget Beta (25.00) and Widget Gamma (50.00)
    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results[0].Data.Name).IsEqualTo("Widget Beta");
    await Assert.That(results[1].Data.Name).IsEqualTo("Widget Gamma");
  }

  [Test]
  [Timeout(60000)]
  public async Task QueryByPhysicalColumn_WhereClauseOnCategory_ReturnsMatchingRecordsAsync(CancellationToken cancellationToken) {
    // Arrange
    await _seedProductsAsync(cancellationToken);

    // Act - query by physical column (category)
    var results = await _context!.Set<PerspectiveRow<ProductSearchModel>>()
        .Where(r => EF.Property<string?>(r, "category") == "Electronics")
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results.Count).IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).OrderBy(n => n).ToList();
    await Assert.That(names).IsEquivalentTo(["Widget Alpha", "Widget Beta"]);
  }

  [Test]
  [Timeout(60000)]
  public async Task QueryByPhysicalColumn_WhereClauseOnBool_ReturnsActiveOnlyAsync(CancellationToken cancellationToken) {
    // Arrange
    await _seedProductsAsync(cancellationToken);

    // Act - query by physical boolean column
    var results = await _context!.Set<PerspectiveRow<ProductSearchModel>>()
        .Where(r => EF.Property<bool>(r, "is_active") == true)
        .ToListAsync(cancellationToken);

    // Assert - Alpha and Beta are active, Gamma is not
    await Assert.That(results.Count).IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).OrderBy(n => n).ToList();
    await Assert.That(names).IsEquivalentTo(["Widget Alpha", "Widget Beta"]);
  }

  // ==================== Mixed JSONB + Physical Column Tests ====================

  [Test]
  [Timeout(60000)]
  public async Task MixedQuery_CombinePhysicalAndJsonbFilters_WorksCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange
    await _seedProductsAsync(cancellationToken);

    // Act - combine physical column filter with JSONB path query
    // Physical: price >= 20
    // JSONB: Description contains "Professional" (case-sensitive in PostgreSQL)
    var results = await _context!.Set<PerspectiveRow<ProductSearchModel>>()
        .Where(r => EF.Property<decimal>(r, "price") >= 20.00m)
        .Where(r => r.Data.Description != null && r.Data.Description.Contains("Professional"))
        .ToListAsync(cancellationToken);

    // Assert - only Widget Beta has price >= 20 AND description containing "Professional"
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Widget Beta");
  }

  [Test]
  [Timeout(60000)]
  public async Task MixedQuery_OrderByPhysicalSelectFullModel_ReturnsCompleteDataAsync(CancellationToken cancellationToken) {
    // Arrange
    await _seedProductsAsync(cancellationToken);

    // Act - order by physical column, select full Data model
    var results = await _context!.Set<PerspectiveRow<ProductSearchModel>>()
        .Where(r => EF.Property<string?>(r, "category") == "Electronics")
        .OrderByDescending(r => EF.Property<decimal>(r, "price"))
        .Select(r => r.Data)
        .ToListAsync(cancellationToken);

    // Assert - ordered by price descending, with all JSONB-only fields populated
    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results[0].Name).IsEqualTo("Widget Beta");  // 25.00
    await Assert.That(results[1].Name).IsEqualTo("Widget Alpha");  // 10.00

    // JSONB-only fields should be populated
    await Assert.That(results[0].Description).IsNotNull();
    await Assert.That(results[0].Tags.Count).IsGreaterThan(0);
  }

  // ==================== Store Integration Tests ====================

  [Test]
  [Timeout(60000)]
  public async Task Store_UpsertWithPhysicalFieldsAsync_RoundtripPersistsCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<ProductSearchModel>(
        _context!,
        "wh_per_product_search",
        strategy);

    var model = new ProductSearchModel {
      Name = "Store Test Product",
      Price = 99.99m,
      Category = "Test",
      IsActive = true,
      Description = "Testing store integration with PostgreSQL",
      Tags = ["test", "integration", "postgres"]
    };
    var testId = _idProvider.NewGuid();
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "price", model.Price },
      { "category", model.Category },
      { "is_active", model.IsActive }
    };

    // Act
    await store.UpsertWithPhysicalFieldsAsync(testId, model, physicalFieldValues, cancellationToken);

    // Assert - query back via store
    var retrieved = await store.GetByStreamIdAsync(testId, cancellationToken);

    await Assert.That(retrieved).IsNotNull();
    await Assert.That(retrieved!.Name).IsEqualTo("Store Test Product");
    await Assert.That(retrieved.Price).IsEqualTo(99.99m);
    await Assert.That(retrieved.Category).IsEqualTo("Test");
    await Assert.That(retrieved.IsActive).IsTrue();
    await Assert.That(retrieved.Description).IsEqualTo("Testing store integration with PostgreSQL");
    await Assert.That(retrieved.Tags.Count).IsEqualTo(3);
  }

  [Test]
  [Timeout(60000)]
  public async Task Store_MultipleUpserts_MaintainsDataIntegrityAsync(CancellationToken cancellationToken) {
    // Arrange
    var strategy = new PostgresUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<ProductSearchModel>(
        _context!,
        "wh_per_product_search",
        strategy);
    var testId = _idProvider.NewGuid();

    // Act - perform multiple upserts
    for (var i = 1; i <= 5; i++) {
      var model = new ProductSearchModel {
        Name = $"Product v{i}",
        Price = i * 10.00m,
        Category = i % 2 == 0 ? "Even" : "Odd",
        IsActive = i < 5,  // Last version is inactive
        Description = $"Version {i}"
      };
      var physicalFieldValues = new Dictionary<string, object?> {
        { "name", model.Name },
        { "price", model.Price },
        { "category", model.Category },
        { "is_active", model.IsActive }
      };
      await store.UpsertWithPhysicalFieldsAsync(testId, model, physicalFieldValues, cancellationToken);
    }

    // Assert - final version should be persisted
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = await connection.QuerySingleAsync<dynamic>(
        "SELECT name, price, category, is_active, version FROM wh_per_product_search WHERE id = @Id",
        new { Id = testId });

    await Assert.That((string?)result.name).IsEqualTo("Product v5");
    await Assert.That((decimal)result.price).IsEqualTo(50.00m);
    await Assert.That((string?)result.category).IsEqualTo("Odd");
    await Assert.That((bool)result.is_active).IsFalse();
    await Assert.That((int)result.version).IsEqualTo(5);
  }

  // ==================== Helper Methods ====================

  private async Task _seedProductsAsync(CancellationToken cancellationToken) {
    var strategy = new PostgresUpsertStrategy();
    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope { TenantId = "tenant-1" };

    var products = new[] {
      new {
        Id = _idProvider.NewGuid(),
        Model = new ProductSearchModel {
          Name = "Widget Alpha",
          Price = 10.00m,
          Category = "Electronics",
          IsActive = true,
          Description = "Entry-level widget for beginners",
          Tags = new List<string> { "basic", "entry" }
        }
      },
      new {
        Id = _idProvider.NewGuid(),
        Model = new ProductSearchModel {
          Name = "Widget Beta",
          Price = 25.00m,
          Category = "Electronics",
          IsActive = true,
          Description = "Professional widget for advanced users",
          Tags = new List<string> { "professional", "advanced" }
        }
      },
      new {
        Id = _idProvider.NewGuid(),
        Model = new ProductSearchModel {
          Name = "Widget Gamma",
          Price = 50.00m,
          Category = "Premium",
          IsActive = false,  // Discontinued
          Description = "Premium discontinued widget",
          Tags = new List<string> { "premium", "discontinued" }
        }
      }
    };

    foreach (var product in products) {
      var physicalFieldValues = new Dictionary<string, object?> {
        { "name", product.Model.Name },
        { "price", product.Model.Price },
        { "category", product.Model.Category },
        { "is_active", product.Model.IsActive }
      };

      await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
          _context!,
          "wh_per_product_search",
          product.Id,
          product.Model,
          metadata,
          scope,
          physicalFieldValues,
          cancellationToken);
    }
  }
}
