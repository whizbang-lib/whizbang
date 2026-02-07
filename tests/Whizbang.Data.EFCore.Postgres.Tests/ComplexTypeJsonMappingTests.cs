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
/// Tests verifying EF Core 10 compatibility with Whizbang's JSONB mapping approach.
/// </summary>
/// <remarks>
/// <para>
/// <strong>EF Core 10 Migration Decision:</strong>
/// EF Core 10 introduces <c>ComplexProperty().ToJson()</c> as the recommended pattern for JSON mapping.
/// However, this approach requires all nested types to be explicitly configured as complex types,
/// which doesn't work well with our <c>PerspectiveScope</c> and <c>PerspectiveMetadata</c> types
/// that contain complex nested structures like <c>IReadOnlyList&lt;SecurityPrincipalId&gt;</c>.
/// </para>
/// <para>
/// <strong>Our Approach:</strong>
/// We continue using <c>Property().HasColumnType("jsonb")</c> which:
/// - Works with Npgsql's POCO serialization for any object structure
/// - Doesn't require explicit configuration of nested types
/// - Combined with our <c>PhysicalFieldExpressionVisitor</c>, provides unified query syntax
/// - Is still fully supported in Npgsql 10
/// </para>
/// <para>
/// These tests verify that our current approach works correctly with EF Core 10.
/// </para>
/// </remarks>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class ComplexTypeJsonMappingTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static ComplexTypeJsonMappingTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private EFCore10CompatDbContext? _context;
  private string _connectionString = null!;

  /// <summary>
  /// Test model with both physical and JSONB-only fields.
  /// </summary>
  public class ProductModel {
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
  }

  /// <summary>
  /// DbContext using current JSONB mapping pattern (compatible with EF Core 10).
  /// Uses Property().HasColumnType("jsonb") - the proven pattern for complex types.
  /// </summary>
  private sealed class EFCore10CompatDbContext : DbContext {
    public EFCore10CompatDbContext(DbContextOptions<EFCore10CompatDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<ProductModel>>(entity => {
        entity.ToTable("wh_per_efcore10_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // Current pattern: Property().HasColumnType("jsonb")
        // This works with any object structure via Npgsql POCO serialization
        // Still supported and recommended for complex types in Npgsql 10
        entity.Property(e => e.Data)
            .HasColumnName("data")
            .HasColumnType("jsonb")
            .IsRequired();

        entity.Property(e => e.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .IsRequired();

        entity.Property(e => e.Scope)
            .HasColumnName("scope")
            .HasColumnType("jsonb")
            .IsRequired();

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

    _testDatabaseName = $"efcore10_test_{Guid.NewGuid():N}";

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

    // Register physical fields
    PhysicalFieldRegistry.Clear();
    PhysicalFieldRegistry.Register<ProductModel>("Name", "name");
    PhysicalFieldRegistry.Register<ProductModel>("Price", "price");

    var optionsBuilder = new DbContextOptionsBuilder<EFCore10CompatDbContext>();
    optionsBuilder
        .UseNpgsql(_dataSource)
        .UseWhizbangPhysicalFields()
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _context = new EFCore10CompatDbContext(optionsBuilder.Options);

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
      CREATE TABLE IF NOT EXISTS wh_per_efcore10_test (
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

      CREATE INDEX IF NOT EXISTS idx_efcore10_test_name ON wh_per_efcore10_test(name);
      CREATE INDEX IF NOT EXISTS idx_efcore10_test_price ON wh_per_efcore10_test(price);
      """);
  }

  private async Task _seedTestDataAsync() {
    var strategy = new PostgresUpsertStrategy();

    var testProducts = new[] {
      new ProductModel { Name = "Widget A", Price = 15.00m, Description = "A basic widget", Tags = ["widget", "basic"] },
      new ProductModel { Name = "Widget B", Price = 25.00m, Description = "A better widget", Tags = ["widget", "premium"] },
      new ProductModel { Name = "Gadget X", Price = 50.00m, Description = "A useful gadget", Tags = ["gadget"] },
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
        { "price", product.Price }
      };

      await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
          _context!,
          "wh_per_efcore10_test",
          id,
          product,
          metadata,
          scope,
          physicalFields);
    }
  }

  // ==================== EF CORE 10 COMPATIBILITY TESTS ====================

  /// <summary>
  /// Verifies current JSONB pattern works in EF Core 10.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EFCore10_CurrentJsonbPattern_WorksCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange & Act - read data using current pattern
    var results = await _context!.Set<PerspectiveRow<ProductModel>>()
        .ToListAsync(cancellationToken);

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);

    var widgetA = results.FirstOrDefault(r => r.Data.Name == "Widget A");
    await Assert.That(widgetA).IsNotNull();
    await Assert.That(widgetA!.Data.Price).IsEqualTo(15.00m);
    await Assert.That(widgetA.Data.Description).IsEqualTo("A basic widget");
    await Assert.That(widgetA.Data.Tags).Contains("widget");
  }

  /// <summary>
  /// Verifies physical field translator works with EF Core 10.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EFCore10_PhysicalFieldTranslator_InterceptsCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange - query using unified syntax
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Price >= 25.00m);

    // Capture SQL
    var sql = query.ToQueryString();

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - SQL should use physical column, not JSONB extraction
    await Assert.That(sql).DoesNotContain("data ->> 'Price'");
    await Assert.That(sql.ToLowerInvariant()).Contains(".price >= ");

    await Assert.That(results).Count().IsEqualTo(2); // Widget B and Gadget X
  }

  /// <summary>
  /// Verifies non-physical fields query via JSONB in EF Core 10.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EFCore10_NonPhysicalField_UsesJsonbExtractionAsync(CancellationToken cancellationToken) {
    // Arrange - query non-physical field (Description)
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Description!.Contains("basic"));

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - should find Widget A via JSONB query
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Widget A");
  }

  /// <summary>
  /// Verifies mixed physical and JSONB queries work in EF Core 10.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EFCore10_MixedQuery_BothTranslateCorrectlyAsync(CancellationToken cancellationToken) {
    // Arrange - combine physical (Price) and JSONB (Description)
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .Where(r => r.Data.Price >= 25.00m)
        .Where(r => r.Data.Description!.Contains("gadget"));

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - only Gadget X matches both criteria
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Gadget X");
  }

  /// <summary>
  /// Verifies Metadata and Scope complex types work in EF Core 10.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EFCore10_MetadataAndScope_PreserveStructureAsync(CancellationToken cancellationToken) {
    // Arrange & Act
    var results = await _context!.Set<PerspectiveRow<ProductModel>>()
        .ToListAsync(cancellationToken);

    // Assert - verify Metadata structure preserved
    var first = results.First();
    await Assert.That(first.Metadata).IsNotNull();
    await Assert.That(first.Metadata.EventType).IsEqualTo("ProductCreated");
    await Assert.That(first.Metadata.EventId).IsNotNull();

    // Assert - verify Scope structure preserved
    await Assert.That(first.Scope).IsNotNull();
  }

  /// <summary>
  /// Verifies OrderBy on physical fields works in EF Core 10.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task EFCore10_OrderByPhysicalField_UsesColumnAsync(CancellationToken cancellationToken) {
    // Arrange - order by physical field
    var query = _context!.Set<PerspectiveRow<ProductModel>>()
        .OrderBy(r => r.Data.Price);

    // Act
    var results = await query.ToListAsync(cancellationToken);

    // Assert - correct order
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Data.Price).IsEqualTo(15.00m);
    await Assert.That(results[1].Data.Price).IsEqualTo(25.00m);
    await Assert.That(results[2].Data.Price).IsEqualTo(50.00m);
  }
}
