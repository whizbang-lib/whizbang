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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// TDD tests for unified query syntax - verifies that r.Data.PropertyName
/// queries translate to physical column access for [PhysicalField] properties.
/// </summary>
/// <remarks>
/// These tests follow TDD RED-GREEN-REFACTOR:
/// - RED: Tests initially fail because r.Data.PropertyName goes through JSONB
/// - GREEN: After implementing PhysicalFieldMemberTranslator, tests pass
/// - REFACTOR: Clean up and optimize implementation
///
/// The unified syntax means users write:
///   .Where(r => r.Data.Price >= 20.00m)  // Looks like JSONB access
/// But the translator redirects physical fields to column access:
///   WHERE price >= 20.00  // Uses indexed physical column
/// </remarks>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class UnifiedQuerySyntaxTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static UnifiedQuerySyntaxTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private UnifiedQueryDbContext? _context;
  private string _connectionString = null!;

  /// <summary>
  /// Test model representing a product with physical fields.
  /// Physical fields: Name, Price, Category, IsActive
  /// JSONB-only fields: Description, Tags
  /// </summary>
  public class ProductModel {
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string? Category { get; init; }
    public bool IsActive { get; init; }
    public string? Description { get; init; }  // JSONB only
    public List<string> Tags { get; init; } = [];  // JSONB only
  }

  /// <summary>
  /// DbContext configured with physical fields as shadow properties.
  /// </summary>
  private sealed class UnifiedQueryDbContext : DbContext {
    public UnifiedQueryDbContext(DbContextOptions<UnifiedQueryDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<ProductModel>>(entity => {
        entity.ToTable("wh_per_unified_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        entity.Property(e => e.Data)
            .HasColumnName("data")
            .HasColumnType("jsonb");

        entity.Property(e => e.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb");

        entity.Property(e => e.Scope)
            .HasColumnName("scope")
            .HasColumnType("jsonb");

        // Physical fields as shadow properties
        entity.Property<string?>("name").HasColumnName("name").HasMaxLength(200);
        entity.Property<decimal>("price").HasColumnName("price");
        entity.Property<string?>("category").HasColumnName("category").HasMaxLength(100);
        entity.Property<bool>("is_active").HasColumnName("is_active");

        entity.HasIndex("name");
        entity.HasIndex("price");
        entity.HasIndex("category");
        entity.HasIndex("is_active");
      });
    }
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"unified_test_{Guid.NewGuid():N}";

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

    // Register physical fields for ProductModel
    PhysicalFieldRegistry.Clear(); // Clear any previous registrations
    PhysicalFieldRegistry.Register<ProductModel>("Name", "name");
    PhysicalFieldRegistry.Register<ProductModel>("Price", "price");
    PhysicalFieldRegistry.Register<ProductModel>("Category", "category");
    PhysicalFieldRegistry.Register<ProductModel>("IsActive", "is_active");

    var optionsBuilder = new DbContextOptionsBuilder<UnifiedQueryDbContext>();
    optionsBuilder
        .UseNpgsql(_dataSource)
        .UseWhizbangPhysicalFields()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _context = new UnifiedQueryDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();
    await _seedTestDataAsync();
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

    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();

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

    await connection.ExecuteAsync("""
      CREATE TABLE IF NOT EXISTS wh_per_unified_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL,
        name VARCHAR(200),
        price DECIMAL NOT NULL DEFAULT 0,
        category VARCHAR(100),
        is_active BOOLEAN NOT NULL DEFAULT FALSE
      );

      CREATE INDEX IF NOT EXISTS idx_unified_test_name ON wh_per_unified_test(name);
      CREATE INDEX IF NOT EXISTS idx_unified_test_price ON wh_per_unified_test(price);
      CREATE INDEX IF NOT EXISTS idx_unified_test_category ON wh_per_unified_test(category);
      CREATE INDEX IF NOT EXISTS idx_unified_test_is_active ON wh_per_unified_test(is_active);
      """);
  }

  private async Task _seedTestDataAsync() {
    var strategy = new PostgresUpsertStrategy();

    var testProducts = new[] {
      new ProductModel { Name = "Widget A", Price = 15.00m, Category = "Widgets", IsActive = true, Description = "A basic widget" },
      new ProductModel { Name = "Widget B", Price = 25.00m, Category = "Widgets", IsActive = true, Description = "A better widget" },
      new ProductModel { Name = "Gadget X", Price = 50.00m, Category = "Gadgets", IsActive = true, Description = "A useful gadget" },
      new ProductModel { Name = "Gadget Y", Price = 75.00m, Category = "Gadgets", IsActive = false, Description = "Discontinued gadget" },
      new ProductModel { Name = "Tool Z", Price = 100.00m, Category = "Tools", IsActive = true, Description = "Professional tool" }
    };

    var metadata = new PerspectiveMetadata {
      EventType = "ProductCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    foreach (var product in testProducts) {
      var id = _idProvider.NewGuid();
      var physicalFields = new Dictionary<string, object?> {
        { "name", product.Name },
        { "price", product.Price },
        { "category", product.Category },
        { "is_active", product.IsActive }
      };

      await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
          _context!,
          "wh_per_unified_test",
          id,
          product,
          metadata,
          scope,
          physicalFields);
    }
  }

  // ==================== UNIFIED QUERY SYNTAX TESTS ====================
  // These tests verify r.Data.PropertyName translates to physical column access

  /// <summary>
  /// Test 1: Basic WHERE clause on physical field should use column, not JSONB.
  /// Verifies the SQL contains "price" column reference, not JSONB path like data->>'Price'.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_WhereOnPhysicalField_UsesColumnNotJsonbAsync(CancellationToken cancellationToken) {
    // Arrange - query using unified syntax r.Data.Price
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Price >= 50.00m);

    // Capture the generated SQL
    var sql = query.ToQueryString();

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - SQL must use physical column "w.price", NOT JSONB extraction "data ->> 'Price'"
    // Current behavior (before fix): CAST(w.data ->> 'Price' AS numeric) >= 50.0
    // Expected behavior (after fix): w.price >= 50.0
    await Assert.That(sql).DoesNotContain("data ->> 'Price'");
    await Assert.That(sql.ToLowerInvariant()).Contains(".price >= ");

    // Assert - should find Gadget X ($50), Gadget Y ($75), Tool Z ($100)
    await Assert.That(results).Count().IsEqualTo(3);

    // Verify data is correctly loaded
    var prices = results.Select(r => r.Data.Price).OrderBy(p => p).ToList();
    await Assert.That(prices).Contains(50.00m);
    await Assert.That(prices).Contains(75.00m);
    await Assert.That(prices).Contains(100.00m);
  }

  /// <summary>
  /// Test 2: Comparison operators on physical field should use column.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_PhysicalFieldGreaterThan_TranslatesToColumnComparisonAsync(CancellationToken cancellationToken) {
    // Arrange - price > 25 should find items priced at 50, 75, 100
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Price > 25.00m);

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    var allPricesOver25 = results.All(r => r.Data.Price > 25.00m);
    await Assert.That(allPricesOver25).IsTrue();
  }

  /// <summary>
  /// Test 3: String Contains on physical field should use column LIKE.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_PhysicalFieldStringContains_TranslatesToColumnLikeAsync(CancellationToken cancellationToken) {
    // Arrange - find products with "Widget" in name
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Name.Contains("Widget"));

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - should find Widget A and Widget B
    await Assert.That(results).Count().IsEqualTo(2);
    var allContainWidget = results.All(r => r.Data.Name.Contains("Widget"));
    await Assert.That(allContainWidget).IsTrue();
  }

  /// <summary>
  /// Test 4: Mixed physical and JSONB fields in same WHERE clause.
  /// Physical: r.Data.Price (uses column)
  /// JSONB: r.Data.Description (uses data->>'Description')
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_MixedPhysicalAndJsonb_BothTranslateCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange - Price (physical) and Description (JSONB)
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Price >= 50.00m)
        .Where(r => r.Data.Description!.Contains("Professional"));

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - only Tool Z matches both criteria
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Tool Z");
  }

  /// <summary>
  /// Test 5: ORDER BY on physical field should use column.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_OrderByPhysicalField_UsesColumnNotJsonbAsync(CancellationToken cancellationToken) {
    // Arrange - order by price ascending
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .OrderBy(r => r.Data.Price);

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - verify correct order
    await Assert.That(results).Count().IsEqualTo(5);
    await Assert.That(results[0].Data.Price).IsEqualTo(15.00m);  // Widget A
    await Assert.That(results[1].Data.Price).IsEqualTo(25.00m);  // Widget B
    await Assert.That(results[2].Data.Price).IsEqualTo(50.00m);  // Gadget X
    await Assert.That(results[3].Data.Price).IsEqualTo(75.00m);  // Gadget Y
    await Assert.That(results[4].Data.Price).IsEqualTo(100.00m); // Tool Z
  }

  /// <summary>
  /// Test 6: SELECT projection including physical field.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_SelectPhysicalField_ReturnsFromColumnAsync(CancellationToken cancellationToken) {
    // Arrange - select just Name and Price
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Category == "Widgets")
        .Select(r => new { r.Data.Name, r.Data.Price });

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var hasWidgetA = results.Any(r => r.Name == "Widget A" && r.Price == 15.00m);
    var hasWidgetB = results.Any(r => r.Name == "Widget B" && r.Price == 25.00m);
    await Assert.That(hasWidgetA).IsTrue();
    await Assert.That(hasWidgetB).IsTrue();
  }

  /// <summary>
  /// Test 7: Multiple physical fields in same query.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_MultiplePhysicalFields_AllUseColumnsAsync(CancellationToken cancellationToken) {
    // Arrange - filter on Price, Category, and IsActive
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Price < 100.00m)
        .Where(r => r.Data.Category == "Gadgets")
        .Where(r => r.Data.IsActive);

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - only Gadget X matches (Gadget Y is inactive)
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Gadget X");
  }

  /// <summary>
  /// Test 8: Non-physical field (Description) should still use JSONB.
  /// This verifies we don't break JSONB queries for non-physical fields.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Query_NonPhysicalField_StillUsesJsonbAsync(CancellationToken cancellationToken) {
    // Arrange - Description is NOT a physical field, should use JSONB
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Description!.Contains("basic"));

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - should find Widget A
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Widget A");
  }
}
