#pragma warning disable CA1707

using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pgvector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for Split-mode physical fields using the PRODUCTION EF Core mapping
/// (ComplexProperty().ToJson()) — NOT the fallback Property().HasColumnType("jsonb").
/// These tests reproduce the exact bugs seen in JDNext production.
/// </summary>
[Category("Integration")]
[NotInParallel("PostgreSQL")]
public class SplitModeProductionTests : IAsyncDisposable {
  private static readonly Uuid7IdProvider _idProvider = new();

  static SplitModeProductionTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
  }

  private string? _testDatabaseName;
  private NpgsqlDataSource? _dataSource;
  private ProductionSplitDbContext? _context;
  private string _connectionString = null!;

  // ========================================================================
  // Test Model — mirrors JDNext's JobArchitectureEmbeddingModel exactly
  // ========================================================================

  /// <summary>
  /// Split-mode model with physical fields, vector field, AND nullable collection of structs.
  /// This is the exact production pattern that was crashing.
  /// </summary>
  public class SplitProductionModel {
    public Guid TenantId { get; set; }
    public string Category { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public Guid? ParentId { get; set; }
    public float[]? Embeddings { get; set; }

    // JSONB-only fields
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Guid>? TagIds { get; set; }
  }

  // ========================================================================
  // DbContext — uses ComplexProperty().ToJson() (PRODUCTION pattern)
  // ========================================================================

  private sealed class ProductionSplitDbContext(DbContextOptions<SplitModeProductionTests.ProductionSplitDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<SplitProductionModel>>(entity => {
        entity.ToTable("wh_per_split_production_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // PRODUCTION PATTERN: ComplexProperty().ToJson()
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));

        // Physical fields as shadow properties
        entity.Property<Guid>("tenant_id").HasColumnName("tenant_id");
        entity.Property<string?>("category").HasColumnName("category").HasMaxLength(200);
        entity.Property<decimal>("price").HasColumnName("price");
        entity.Property<Guid?>("parent_id").HasColumnName("parent_id");
        entity.Property<Vector?>("embeddings").HasColumnName("embeddings").HasColumnType("vector(3)");
      });
    }
  }

  // ========================================================================
  // Setup / Teardown
  // ========================================================================

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
    dataSourceBuilder.UseVector();
    _dataSource = dataSourceBuilder.Build();

    var optionsBuilder = new DbContextOptionsBuilder<ProductionSplitDbContext>();
    optionsBuilder.UseNpgsql(_dataSource, o => o.UseVector())
        .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
        .AddInterceptors(new PhysicalFieldQueryInterceptor(), new PhysicalFieldMaterializationInterceptor());
    _context = new ProductionSplitDbContext(optionsBuilder.Options);

    await _initializeSchemaAsync();

    // Register physical field mappings
    PhysicalFieldRegistry.Register<SplitProductionModel>("TenantId", "tenant_id");
    PhysicalFieldRegistry.Register<SplitProductionModel>("Category", "category");
    PhysicalFieldRegistry.Register<SplitProductionModel>("Price", "price");
    PhysicalFieldRegistry.Register<SplitProductionModel>("ParentId", "parent_id");
    PhysicalFieldRegistry.Register<SplitProductionModel>("Embeddings", "embeddings", isVector: true);

    // Register hydrator
    PhysicalFieldHydratorRegistry.Register<SplitProductionModel>((data, entity) => {
      var row = (PerspectiveRow<SplitProductionModel>)entity;
      if (row.Data is null) { return; }
      row.Data.TenantId = data.GetPropertyValue<Guid>("tenant_id");
      var category = data.GetPropertyValue<string?>("category");
      if (category is not null) { row.Data.Category = category; }
      row.Data.Price = data.GetPropertyValue<decimal>("price");
      var parentId = data.GetPropertyValue<Guid?>("parent_id");
      if (parentId is not null) { row.Data.ParentId = parentId; }
      var embeddings = data.GetPropertyValue<Vector?>("embeddings");
      if (embeddings is not null) { row.Data.Embeddings = embeddings.ToArray(); }
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
      CREATE EXTENSION IF NOT EXISTS vector;
      CREATE TABLE IF NOT EXISTS wh_per_split_production_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL,
        tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
        category VARCHAR(200),
        price DECIMAL NOT NULL DEFAULT 0,
        parent_id UUID,
        embeddings vector(3)
      );
      """);
  }

  // ========================================================================
  // Write Path Tests
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Write_SplitModel_PhysicalColumnsHaveCorrectValuesAsync(CancellationToken ct) {
    // Arrange & Act
    var tenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenantId, "electronics", 99.99m, "Widget", "A widget", [0.1f, 0.2f, 0.3f], [Guid.CreateVersion7()]);

    // Assert — physical columns have real values
    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync(ct);
    var row = await conn.QueryFirstAsync("SELECT tenant_id, category, price, embeddings IS NOT NULL as has_vec FROM wh_per_split_production_test LIMIT 1");
    await Assert.That((Guid)row.tenant_id).IsEqualTo(tenantId);
    await Assert.That((string)row.category).IsEqualTo("electronics");
    await Assert.That((decimal)row.price).IsEqualTo(99.99m);
    await Assert.That((bool)row.has_vec).IsTrue();
  }

  // ========================================================================
  // Read Path — Full Entity Materialization
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Read_FullEntity_PhysicalFieldsPopulatedFromColumnsAsync(CancellationToken ct) {
    var tenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenantId, "electronics", 42.00m, "Gadget", "A gadget", [0.4f, 0.5f, 0.6f], null);

    var row = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.TenantId).IsEqualTo(tenantId)
      .Because("Physical field TenantId must be hydrated from column");
    await Assert.That(row.Data.Category).IsEqualTo("electronics")
      .Because("Physical field Category must be hydrated from column");
    await Assert.That(row.Data.Price).IsEqualTo(42.00m)
      .Because("Physical field Price must be hydrated from column");
  }

  [Test]
  [Timeout(60000)]
  public async Task Read_FullEntity_VectorFieldPopulatedFromColumnAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "vec", 10.00m, "VecItem", "vec desc", [0.1f, 0.2f, 0.3f], null);

    var row = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Embeddings).IsNotNull()
      .Because("Vector field Embeddings must be hydrated from physical column");
    await Assert.That(row.Data.Embeddings!.Length).IsEqualTo(3);
    await Assert.That(row.Data.Embeddings[0]).IsEqualTo(0.1f).Within(0.001f);
  }

  [Test]
  [Timeout(60000)]
  public async Task Read_FullEntity_NonPhysicalFieldsFromJsonbAsync(CancellationToken ct) {
    var tagId = Guid.CreateVersion7();
    await _insertSplitRowAsync(Guid.CreateVersion7(), "cat", 10.00m, "NameFromJsonb", "DescFromJsonb", [0.1f, 0.2f, 0.3f], [tagId]);

    var row = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("NameFromJsonb");
    await Assert.That(row.Data.Description).IsEqualTo("DescFromJsonb");
    await Assert.That(row.Data.TagIds).IsNotNull();
    await Assert.That(row.Data.TagIds!.Count).IsEqualTo(1);
    await Assert.That(row.Data.TagIds[0]).IsEqualTo(tagId);
  }

  [Test]
  [Timeout(60000)]
  public async Task Read_FullEntity_NullableCollectionInJsonb_NoExceptionAsync(CancellationToken ct) {
    // TagIds is null — this is what crashed with ComplexProperty().ToJson()
    await _insertSplitRowAsync(Guid.CreateVersion7(), "cat", 10.00m, "NullTags", "desc", [0.1f, 0.2f, 0.3f], null);

    // Act — should NOT throw NullReferenceException from JsonCollectionOfStructsReaderWriter
    var row = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.TagIds).IsNull();
  }

  // ========================================================================
  // Read Path — Select Projections
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Select_VectorField_ReturnsRealValuesAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "cat", 10.00m, "Item", "desc", [0.7f, 0.8f, 0.9f], null);

    // This should read from the physical column, not JSONB (which has [] or null)
    var embeddings = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Select(r => r.Data.Embeddings)
        .ToListAsync(ct);

    await Assert.That(embeddings).Count().IsEqualTo(1);
    await Assert.That(embeddings[0]).IsNotNull()
      .Because("Select projection on vector field must return real values from physical column");
    await Assert.That(embeddings[0]!.Length).IsEqualTo(3);
    await Assert.That(embeddings[0]![0]).IsEqualTo(0.7f).Within(0.001f);
  }

  [Test]
  [Timeout(60000)]
  public async Task Select_PhysicalGuidField_ReturnsRealValueAsync(CancellationToken ct) {
    var tenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenantId, "cat", 10.00m, "Item", "desc", [0.1f, 0.2f, 0.3f], null);

    var tenantIds = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Select(r => r.Data.TenantId)
        .ToListAsync(ct);

    await Assert.That(tenantIds).Count().IsEqualTo(1);
    await Assert.That(tenantIds[0]).IsEqualTo(tenantId)
      .Because("Select projection on physical Guid field must return real value from column");
  }

  [Test]
  [Timeout(60000)]
  public async Task Select_PhysicalStringField_ReturnsRealValueAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 10.00m, "Item", "desc", [0.1f, 0.2f, 0.3f], null);

    var categories = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Select(r => r.Data.Category)
        .ToListAsync(ct);

    await Assert.That(categories).Count().IsEqualTo(1);
    await Assert.That(categories[0]).IsEqualTo("electronics")
      .Because("Select projection on physical string field must return real value from column");
  }

  // ========================================================================
  // Read Path — WHERE on Physical Fields
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Where_PhysicalGuidField_FiltersCorrectlyAsync(CancellationToken ct) {
    var targetTenantId = Guid.CreateVersion7();
    var otherTenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(targetTenantId, "a", 10.00m, "Target", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(otherTenantId, "b", 20.00m, "Other", "desc", [0.4f, 0.5f, 0.6f], null);

    var rows = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Where(r => r.Data.TenantId == targetTenantId)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Target");
    await Assert.That(rows[0].Data.TenantId).IsEqualTo(targetTenantId);
  }

  [Test]
  [Timeout(60000)]
  public async Task Where_PhysicalStringField_FiltersCorrectlyAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 10.00m, "Gadget", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "clothing", 20.00m, "Shirt", "desc", [0.4f, 0.5f, 0.6f], null);

    var rows = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Where(r => r.Data.Category == "electronics")
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Gadget");
    await Assert.That(rows[0].Data.Category).IsEqualTo("electronics");
  }

  [Test]
  [Timeout(60000)]
  public async Task Where_PhysicalDecimalField_ComparisonAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "a", 10.00m, "Cheap", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "b", 100.00m, "Expensive", "desc", [0.4f, 0.5f, 0.6f], null);

    var rows = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Where(r => r.Data.Price >= 50.00m)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Expensive");
    await Assert.That(rows[0].Data.Price).IsEqualTo(100.00m);
  }

  // ========================================================================
  // Read Path — ORDER BY
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task OrderBy_PhysicalField_OrdersCorrectlyAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "a", 30.00m, "Mid", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "b", 10.00m, "Low", "desc", [0.4f, 0.5f, 0.6f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "c", 50.00m, "High", "desc", [0.7f, 0.8f, 0.9f], null);

    var rows = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Data.Price)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(rows[0].Data.Price).IsEqualTo(10.00m);
    await Assert.That(rows[1].Data.Price).IsEqualTo(30.00m);
    await Assert.That(rows[2].Data.Price).IsEqualTo(50.00m);
  }

  // ========================================================================
  // Read Path — Vector Operations
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task OrderByCosineDistance_VectorField_ReturnsResultsAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "a", 10.00m, "Similar", "desc", [1.0f, 0.0f, 0.0f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "b", 20.00m, "Different", "desc", [0.0f, 1.0f, 0.0f], null);

    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };

    var results = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .OrderByCosineDistance(m => m.Embeddings, searchVector)
        .WithCosineDistance(m => m.Embeddings, searchVector)
        .ToListAsync(ct);

    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Row.Data.Name).IsEqualTo("Similar")
      .Because("Closest vector should be first");
  }

  // ========================================================================
  // SQL Verification
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Where_PhysicalField_SqlUsesColumn_NotJsonbAsync(CancellationToken ct) {
    var query = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Where(r => r.Data.Price >= 50.00m);

    var sql = query.ToQueryString();

    await Assert.That(sql).DoesNotContain("data")
      .Because("Physical field Price should use column, not JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains(".price")
      .Because("Should use physical column name 'price'");
  }

  // ========================================================================
  // Helpers
  // ========================================================================

  /// <summary>
  /// Simulates what the generated PerspectiveRunner does in Split mode:
  /// 1. Strip physical fields from model (set to default)
  /// 2. Store physical field values via shadow properties
  /// The JSONB will contain stripped model (physical fields = default/null)
  /// </summary>
  private async Task _insertSplitRowAsync(
      Guid tenantId, string category, decimal price, string name, string description,
      float[]? embeddings, List<Guid>? tagIds) {

    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();

    // Model as it would be AFTER Apply() but BEFORE stripping
    var strippedModel = new SplitProductionModel {
      // Physical fields set to default (simulating runner stripping)
      TenantId = default,
      Category = default!,
      Price = default,
      ParentId = default,
      Embeddings = embeddings != null ? Array.Empty<float>() : null,
      // JSONB-only fields keep their values
      Name = name,
      Description = description,
      TagIds = tagIds
    };

    var physicalFieldValues = new Dictionary<string, object?> {
      ["tenant_id"] = tenantId,
      ["category"] = category,
      ["price"] = price,
      ["parent_id"] = (Guid?)null,
      ["embeddings"] = embeddings != null ? new Vector(embeddings) : null
    };

    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context!, "wh_per_split_production_test", testId, strippedModel, metadata, new PerspectiveScope(),
        physicalFieldValues, default);
    await _context!.SaveChangesAsync();
  }
}
