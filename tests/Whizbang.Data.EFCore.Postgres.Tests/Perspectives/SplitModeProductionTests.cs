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

  /// <summary>
  /// Second model for join tests — represents a category lookup table.
  /// </summary>
  public class CategoryModel {
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
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

      // Second entity for JOIN tests
      modelBuilder.Entity<PerspectiveRow<CategoryModel>>(entity => {
        entity.ToTable("wh_per_category_test");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => s.ToJson("scope"));
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

    // Register materialization interceptor hydrator (fallback — no-ops when Data is null with ComplexProperty().ToJson())
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

    // Register ChangeTracker-based hydrator (primary path — fires after ComplexProperty().ToJson() populates Data)
    SplitModeChangeTrackerHydrator.Register(typeof(PerspectiveRow<SplitProductionModel>), entry => {
      var row = (PerspectiveRow<SplitProductionModel>)entry.Entity;
      if (row.Data is null) { return; }
      row.Data.TenantId = (Guid)entry.Property("tenant_id").CurrentValue!;
      var category = (string?)entry.Property("category").CurrentValue;
      if (category is not null) { row.Data.Category = category; }
      row.Data.Price = (decimal)entry.Property("price").CurrentValue!;
      row.Data.ParentId = (Guid?)entry.Property("parent_id").CurrentValue;
      var embeddings = (Vector?)entry.Property("embeddings").CurrentValue;
      if (embeddings is not null) { row.Data.Embeddings = embeddings.ToArray(); }
      entry.State = EntityState.Detached;
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
      CREATE TABLE IF NOT EXISTS wh_per_category_test (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        version INTEGER NOT NULL
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

    // Use TRACKED query so we can access shadow properties via context.Entry()
    var row = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();

    // Hydrate physical fields from shadow properties
    _hydratePhysicalFields(_context, row!);

    await Assert.That(row.Data.TenantId).IsEqualTo(tenantId)
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
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();
    _hydratePhysicalFields(_context!, row!);

    await Assert.That(row.Data.Embeddings).IsNotNull()
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
  public async Task Select_VectorField_ViaEFProperty_ReturnsRealValuesAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "cat", 10.00m, "Item", "desc", [0.7f, 0.8f, 0.9f], null);

    // Vector Select projections require explicit EF.Property<Vector?> because the shadow property
    // type (Vector) differs from the model property type (float[]). The expression visitor
    // cannot rewrite due to type coercion limitations in EF Core expression trees.
    // Full entity materialization uses ChangeTracker hydration instead.
    var vectors = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .Select(r => EF.Property<Vector?>(r, "embeddings"))
        .ToListAsync(ct);

    await Assert.That(vectors).Count().IsEqualTo(1);
    await Assert.That(vectors[0]).IsNotNull()
      .Because("EF.Property<Vector?> must read from physical column, not JSONB");
    var floats = vectors[0]!.ToArray();
    await Assert.That(floats.Length).IsEqualTo(3);
    await Assert.That(floats[0]).IsEqualTo(0.7f).Within(0.001f);
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
        .Where(r => r.Data.TenantId == targetTenantId)
        .ToListAsync(ct);

    _hydratePhysicalFieldsList(_context!, rows);

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
        .Where(r => r.Data.Category == "electronics")
        .ToListAsync(ct);

    _hydratePhysicalFieldsList(_context!, rows);

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
        .Where(r => r.Data.Price >= 50.00m)
        .ToListAsync(ct);

    _hydratePhysicalFieldsList(_context!, rows);

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
        .OrderBy(r => r.Data.Price)
        .ToListAsync(ct);

    _hydratePhysicalFieldsList(_context!, rows);

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

    // WHERE clause must use physical column, not JSONB extraction like data->>'Price'
    await Assert.That(sql).DoesNotContain("data ->>")
      .Because("Physical field Price should use column, not JSONB path extraction");
    await Assert.That(sql).DoesNotContain("data->>'Price'")
      .Because("Should not extract Price from JSONB");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.price >= ")
      .Because("WHERE should use physical column name 'price'");
  }

  // ========================================================================
  // LINQ Regression — SQL Translation Verification
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task GroupBy_PhysicalField_TranslatesToSqlAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 10.00m, "A", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 20.00m, "B", "desc", [0.4f, 0.5f, 0.6f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "clothing", 30.00m, "C", "desc", [0.7f, 0.8f, 0.9f], null);

    var groups = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .GroupBy(r => r.Data.Category)
        .Select(g => new { Category = g.Key, Count = g.Count(), Total = g.Sum(r => r.Data.Price) })
        .OrderBy(g => g.Category)
        .ToListAsync(ct);

    await Assert.That(groups).Count().IsEqualTo(2);
    await Assert.That(groups[0].Category).IsEqualTo("clothing");
    await Assert.That(groups[0].Count).IsEqualTo(1);
    await Assert.That(groups[1].Category).IsEqualTo("electronics");
    await Assert.That(groups[1].Count).IsEqualTo(2);
    await Assert.That(groups[1].Total).IsEqualTo(30.00m);
  }

  [Test]
  [Timeout(60000)]
  public async Task Count_WherePhysicalField_TranslatesToSqlAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "a", 10.00m, "Cheap", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "b", 50.00m, "Mid", "desc", [0.4f, 0.5f, 0.6f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "c", 100.00m, "Pricey", "desc", [0.7f, 0.8f, 0.9f], null);

    var count = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .CountAsync(r => r.Data.Price > 25.00m, ct);

    await Assert.That(count).IsEqualTo(2);
  }

  [Test]
  [Timeout(60000)]
  public async Task Any_WherePhysicalField_TranslatesToSqlAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 10.00m, "Item", "desc", [0.1f, 0.2f, 0.3f], null);

    var exists = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .AnyAsync(r => r.Data.Category == "electronics", ct);
    var notExists = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .AnyAsync(r => r.Data.Category == "nonexistent", ct);

    await Assert.That(exists).IsTrue();
    await Assert.That(notExists).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task SkipTake_OrderByPhysicalField_TranslatesToSqlAsync(CancellationToken ct) {
    for (var i = 1; i <= 5; i++) {
      await _insertSplitRowAsync(Guid.CreateVersion7(), $"cat{i}", i * 10.00m, $"Item{i}", "desc", [0.1f * i, 0.2f * i, 0.3f * i], null);
    }

    // Page 2: skip 2, take 2, ordered by price
    var rows = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Data.Price)
        .Skip(2)
        .Take(2)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(2);
    // Prices should be 30 and 40 (3rd and 4th items)
    await Assert.That(rows[0].Data.Name).IsEqualTo("Item3");
    await Assert.That(rows[1].Data.Name).IsEqualTo("Item4");
  }

  [Test]
  [Timeout(60000)]
  public async Task GroupBy_PhysicalField_SqlNotClientEvalAsync(CancellationToken ct) {
    var query = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .GroupBy(r => r.Data.Category)
        .Select(g => new { Category = g.Key, Count = g.Count() });

    var sql = query.ToQueryString();

    // GROUP BY must use physical column, not JSONB
    await Assert.That(sql.ToLowerInvariant()).Contains("group by")
      .Because("GROUP BY must be translated to SQL, not client-evaluated");
    await Assert.That(sql).DoesNotContain("data ->>")
      .Because("GROUP BY should use physical column, not JSONB extraction");
  }

  [Test]
  [Timeout(60000)]
  public async Task OrderBy_PhysicalField_SqlNotClientEvalAsync(CancellationToken ct) {
    var query = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .OrderBy(r => r.Data.Price);

    var sql = query.ToQueryString();

    await Assert.That(sql.ToLowerInvariant()).Contains("order by")
      .Because("ORDER BY must be translated to SQL");
    await Assert.That(sql).DoesNotContain("data ->>")
      .Because("ORDER BY should use physical column, not JSONB extraction");
  }

  // ========================================================================
  // LINQ Regression — Joins
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Join_OnPhysicalField_TranslatesToSqlJoinAsync(CancellationToken ct) {
    // Arrange — products with categories, join on category code
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 50.00m, "Laptop", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "clothing", 30.00m, "Shirt", "desc", [0.4f, 0.5f, 0.6f], null);
    await _insertCategoryRowAsync("electronics", "Electronics & Gadgets");
    await _insertCategoryRowAsync("clothing", "Apparel & Fashion");

    // Act — LINQ join between products and categories using physical field Category
    var results = await _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Join(
            _context.Set<PerspectiveRow<CategoryModel>>(),
            product => product.Data.Category,        // physical field on product
            category => category.Data.Code,           // JSONB field on category
            (product, category) => new {
              ProductName = product.Data.Name,
              Category = product.Data.Category,
              Price = product.Data.Price,
              CategoryDisplay = category.Data.DisplayName
            })
        .OrderBy(r => r.ProductName)
        .ToListAsync(ct);

    // Assert — join worked, physical fields populated
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].ProductName).IsEqualTo("Laptop");
    await Assert.That(results[0].CategoryDisplay).IsEqualTo("Electronics & Gadgets");
    await Assert.That(results[1].ProductName).IsEqualTo("Shirt");
    await Assert.That(results[1].CategoryDisplay).IsEqualTo("Apparel & Fashion");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_Join_OnPhysicalField_UsesColumnNotJsonbAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Join(
            _context.Set<PerspectiveRow<CategoryModel>>(),
            product => product.Data.Category,
            category => category.Data.Code,
            (product, category) => new { product, category })
        .ToQueryString();

    // JOIN condition must use physical column for Category, not JSONB
    await Assert.That(sql.ToLowerInvariant()).Contains("join")
      .Because("Must translate to SQL JOIN, not client-side join");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.category")
      .Because("Join key on physical field must use column");
  }

  // ========================================================================
  // SQL Verification — Every LINQ operator uses physical columns
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task Sql_Where_GuidField_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Where(r => r.Data.TenantId == Guid.Empty)
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("WHERE on TenantId must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.tenant_id").Because("WHERE must use physical column tenant_id");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_Where_StringField_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Where(r => r.Data.Category == "test")
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("WHERE on Category must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.category").Because("WHERE must use physical column category");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_Where_DecimalField_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Where(r => r.Data.Price >= 50.0m)
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("WHERE on Price must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.price >= ").Because("WHERE must use physical column price");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_OrderBy_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .OrderBy(r => r.Data.Price)
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("ORDER BY must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("order by w.price").Because("ORDER BY must use physical column price");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_GroupBy_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .GroupBy(r => r.Data.Category)
        .Select(g => new { Category = g.Key, Count = g.Count() })
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("GROUP BY must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("group by w.category").Because("GROUP BY must use physical column category");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_Select_VectorField_ViaEFProperty_UsesPhysicalColumnAsync(CancellationToken ct) {
    // Vector Select projections use EF.Property<Vector?> directly (not the expression visitor)
    // because shadow property type (Vector) differs from model type (float[])
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Select(r => EF.Property<Vector?>(r, "embeddings"))
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("SELECT on Embeddings must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.embeddings").Because("SELECT must use physical column embeddings");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_Select_GuidField_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Select(r => r.Data.TenantId)
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("SELECT on TenantId must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.tenant_id").Because("SELECT must use physical column tenant_id");
  }

  [Test]
  [Timeout(60000)]
  public async Task Sql_Count_Where_UsesPhysicalColumnAsync(CancellationToken ct) {
    var sql = _context!.Set<PerspectiveRow<SplitProductionModel>>()
        .Where(r => r.Data.Price > 50.0m)
        .Select(r => r.Id)  // Need to materialize something for ToQueryString
        .ToQueryString();

    await Assert.That(sql).DoesNotContain("data ->>").Because("COUNT WHERE must not use JSONB extraction");
    await Assert.That(sql.ToLowerInvariant()).Contains("w.price >").Because("WHERE must use physical column price");
  }

  // ========================================================================
  // LensQuery Integration — Full Framework Path
  // Tests through EFCorePostgresLensQuery which includes scoped access,
  // conditional tracking, and ChangeTracker hydration.
  // ========================================================================

  private EFCorePostgresLensQuery<SplitProductionModel> _createLensQuery() =>
      new(_context!, "wh_per_split_production_test");

  private EFCorePostgresLensQuery<CategoryModel> _createCategoryLensQuery() =>
      new(_context!, "wh_per_category_test");

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_GetByIdAsync_PhysicalFieldsHydratedAsync(CancellationToken ct) {
    var tenantId = Guid.CreateVersion7();
    var id = await _insertSplitRowReturningIdAsync(tenantId, "lens-cat", 55.55m, "LensItem", "desc", [0.1f, 0.2f, 0.3f], null);

    var lensQuery = _createLensQuery();
    var model = await lensQuery.GetByIdAsync(id, ct);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.TenantId).IsEqualTo(tenantId)
      .Because("Physical Guid field must be hydrated via LensQuery");
    await Assert.That(model.Category).IsEqualTo("lens-cat")
      .Because("Physical string field must be hydrated via LensQuery");
    await Assert.That(model.Price).IsEqualTo(55.55m)
      .Because("Physical decimal field must be hydrated via LensQuery");
    await Assert.That(model.Name).IsEqualTo("LensItem")
      .Because("JSONB-only field must still work via LensQuery");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_GetByIdAsync_VectorFieldHydratedAsync(CancellationToken ct) {
    var id = await _insertSplitRowReturningIdAsync(Guid.CreateVersion7(), "vec", 10.00m, "VecItem", "desc", [0.4f, 0.5f, 0.6f], null);

    var model = await _createLensQuery().GetByIdAsync(id, ct);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Embeddings).IsNotNull()
      .Because("Vector field must be hydrated via LensQuery ChangeTracker path");
    await Assert.That(model.Embeddings![0]).IsEqualTo(0.4f).Within(0.001f);
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_GetByIdAsync_NonExistent_ReturnsNullAsync(CancellationToken ct) {
    var model = await _createLensQuery().GetByIdAsync(Guid.CreateVersion7(), ct);
    await Assert.That(model).IsNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_ToListAsync_PhysicalFieldsHydratedAsync(CancellationToken ct) {
    var tenant1 = Guid.CreateVersion7();
    var tenant2 = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenant1, "list-a", 10.00m, "Item1", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(tenant2, "list-b", 20.00m, "Item2", "desc", [0.4f, 0.5f, 0.6f], null);

    var rows = await _createLensQuery().Query
        .OrderBy(r => r.Data.Price)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows[0].Data.TenantId).IsEqualTo(tenant1);
    await Assert.That(rows[0].Data.Category).IsEqualTo("list-a");
    await Assert.That(rows[0].Data.Price).IsEqualTo(10.00m);
    await Assert.That(rows[1].Data.TenantId).IsEqualTo(tenant2);
    await Assert.That(rows[1].Data.Price).IsEqualTo(20.00m);
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_Where_PhysicalField_FiltersCorrectlyAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "target", 10.00m, "Target", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "other", 20.00m, "Other", "desc", [0.4f, 0.5f, 0.6f], null);

    var rows = await _createLensQuery().Query
        .Where(r => r.Data.Category == "target")
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Target");
    await Assert.That(rows[0].Data.Category).IsEqualTo("target");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_OrderBy_PhysicalFieldAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "c", 30.00m, "High", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "a", 10.00m, "Low", "desc", [0.4f, 0.5f, 0.6f], null);

    var rows = await _createLensQuery().Query
        .OrderBy(r => r.Data.Price)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows[0].Data.Price).IsEqualTo(10.00m);
    await Assert.That(rows[1].Data.Price).IsEqualTo(30.00m);
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_Select_PhysicalFieldAsync(CancellationToken ct) {
    var tenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenantId, "sel-cat", 42.00m, "SelItem", "desc", [0.1f, 0.2f, 0.3f], null);

    var categories = await _createLensQuery().Query
        .Select(r => r.Data.Category)
        .ToListAsync(ct);

    await Assert.That(categories).Count().IsEqualTo(1);
    await Assert.That(categories[0]).IsEqualTo("sel-cat");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_GroupBy_PhysicalFieldAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 10.00m, "A", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 20.00m, "B", "desc", [0.4f, 0.5f, 0.6f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "clothing", 30.00m, "C", "desc", [0.7f, 0.8f, 0.9f], null);

    var groups = await _createLensQuery().Query
        .GroupBy(r => r.Data.Category)
        .Select(g => new { Category = g.Key, Count = g.Count(), Total = g.Sum(r => r.Data.Price) })
        .OrderBy(g => g.Category)
        .ToListAsync(ct);

    await Assert.That(groups).Count().IsEqualTo(2);
    await Assert.That(groups[0].Category).IsEqualTo("clothing");
    await Assert.That(groups[1].Category).IsEqualTo("electronics");
    await Assert.That(groups[1].Count).IsEqualTo(2);
    await Assert.That(groups[1].Total).IsEqualTo(30.00m);
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_CountAsync_PhysicalFieldPredicateAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "a", 10.00m, "Cheap", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(Guid.CreateVersion7(), "b", 100.00m, "Expensive", "desc", [0.4f, 0.5f, 0.6f], null);

    var count = await _createLensQuery().Query
        .CountAsync(r => r.Data.Price > 50.00m, ct);

    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_AnyAsync_PhysicalFieldPredicateAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "electronics", 10.00m, "Item", "desc", [0.1f, 0.2f, 0.3f], null);

    var exists = await _createLensQuery().Query.AnyAsync(r => r.Data.Category == "electronics", ct);
    var notExists = await _createLensQuery().Query.AnyAsync(r => r.Data.Category == "nonexistent", ct);

    await Assert.That(exists).IsTrue();
    await Assert.That(notExists).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_Query_SkipTake_PhysicalFieldOrderingAsync(CancellationToken ct) {
    for (var i = 1; i <= 5; i++) {
      await _insertSplitRowAsync(Guid.CreateVersion7(), $"cat{i}", i * 10.00m, $"Item{i}", "desc", [0.1f * i, 0.2f * i, 0.3f * i], null);
    }

    var rows = await _createLensQuery().Query
        .OrderBy(r => r.Data.Price)
        .Skip(2).Take(2)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(2);
    await Assert.That(rows[0].Data.Name).IsEqualTo("Item3");
    await Assert.That(rows[1].Data.Name).IsEqualTo("Item4");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_SplitModel_EntitiesDetachedAfterQueryAsync(CancellationToken ct) {
    await _insertSplitRowAsync(Guid.CreateVersion7(), "detach", 10.00m, "DetachItem", "desc", [0.1f, 0.2f, 0.3f], null);

    var rows = await _createLensQuery().Query.ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(rows[0].Data.Category).IsEqualTo("detach")
      .Because("Physical field must be hydrated");
    await Assert.That(_context!.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("All entities must be detached after ChangeTracker hydration");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_NonSplitModel_StillUsesAsNoTrackingAsync(CancellationToken ct) {
    await _insertCategoryRowAsync("notrack-code", "NoTrack Display");

    var catLens = _createCategoryLensQuery();
    var rows = await catLens.Query.ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(1);
    await Assert.That(_context!.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("Non-Split models must use AsNoTracking — zero tracking overhead");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_NullablePhysicalField_ReturnsNullAsync(CancellationToken ct) {
    // ParentId is Guid? — stored as null in physical column
    var id = await _insertSplitRowReturningIdAsync(Guid.CreateVersion7(), "nullable", 10.00m, "NullableItem", "desc", [0.1f, 0.2f, 0.3f], null);

    var model = await _createLensQuery().GetByIdAsync(id, ct);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.ParentId).IsNull()
      .Because("Nullable physical field with null value must remain null");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_LargeResultSet_AllHydratedAsync(CancellationToken ct) {
    // Insert 50 rows to verify no detach-during-iteration bugs
    for (var i = 0; i < 50; i++) {
      await _insertSplitRowAsync(Guid.CreateVersion7(), $"large-{i}", i * 1.11m, $"Large{i}", "desc", [0.1f, 0.2f, 0.3f], null);
    }

    var rows = await _createLensQuery().Query
        .OrderBy(r => r.Data.Price)
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(50);
    // Verify every row has hydrated physical fields
    foreach (var row in rows) {
      await Assert.That(row.Data.Category).IsNotNull().And.IsNotEmpty()
        .Because("Every row must have Category hydrated from physical column");
      await Assert.That(row.Data.TenantId).IsNotEqualTo(Guid.Empty)
        .Because("Every row must have TenantId hydrated from physical column");
    }
    await Assert.That(_context!.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("All 50 entities must be detached");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensQuery_EmptyResult_NoExceptionAsync(CancellationToken ct) {
    // No data inserted — query should return empty, not crash
    var rows = await _createLensQuery().Query.ToListAsync(ct);
    await Assert.That(rows).Count().IsEqualTo(0);

    var model = await _createLensQuery().GetByIdAsync(Guid.CreateVersion7(), ct);
    await Assert.That(model).IsNull();
  }

  // ========================================================================
  // ChangeTracker.Tracked Timing Validation (GATE TEST)
  // ========================================================================

  [Test]
  [Timeout(60000)]
  public async Task ChangeTrackerTracked_FiresAfterComplexPropertyPopulatedAsync(CancellationToken ct) {
    // This test validates the fundamental assumption of our hydration approach:
    // ChangeTracker.Tracked fires AFTER ComplexProperty().ToJson() populates Data.
    // If this test fails, the ChangeTracker approach won't work and we need to redesign.

    await _insertSplitRowAsync(Guid.CreateVersion7(), "gate-test", 99.99m, "GateItem", "gate desc", [0.1f, 0.2f, 0.3f], null);

    var trackedEventFired = false;
    var dataWasNonNull = false;
    var dataNameValue = (string?)null;

    _context!.ChangeTracker.Tracked += (sender, args) => {
      trackedEventFired = true;
      var entity = args.Entry.Entity;
      if (entity is PerspectiveRow<SplitProductionModel> row) {
        dataWasNonNull = row.Data is not null;
        if (row.Data is not null) {
          dataNameValue = row.Data.Name;
        }
      }
    };

    // Execute a TRACKED query (no AsNoTracking) — this should fire the Tracked event
    var row = await _context.Set<PerspectiveRow<SplitProductionModel>>()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull()
      .Because("Query should return a row");
    await Assert.That(trackedEventFired).IsTrue()
      .Because("ChangeTracker.Tracked event must fire for tracked queries");
    await Assert.That(dataWasNonNull).IsTrue()
      .Because("CRITICAL: row.Data must be non-null when Tracked fires — this validates ComplexProperty().ToJson() populates Data BEFORE the Tracked event");
    await Assert.That(dataNameValue).IsEqualTo("GateItem")
      .Because("JSONB-only field Name must be populated when Tracked fires — proves full materialization is complete");
  }

  [Test]
  [Timeout(60000)]
  public async Task ChangeTrackerTracked_CanReadShadowPropertiesAsync(CancellationToken ct) {
    // Validates that shadow properties (physical columns) are accessible via EntityEntry
    // within the Tracked event handler — needed for hydration.

    var tenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenantId, "shadow-test", 42.00m, "ShadowItem", "desc", [0.7f, 0.8f, 0.9f], null);

    Guid? readTenantId = null;
    string? readCategory = null;
    decimal? readPrice = null;
    float[]? readEmbeddings = null;

    _context!.ChangeTracker.Tracked += (sender, args) => {
      if (args.Entry.Entity is PerspectiveRow<SplitProductionModel>) {
        // Non-generic EntityEntry — use Property("name").CurrentValue with casts
        readTenantId = (Guid)args.Entry.Property("tenant_id").CurrentValue!;
        readCategory = (string?)args.Entry.Property("category").CurrentValue;
        readPrice = (decimal)args.Entry.Property("price").CurrentValue!;
        var vec = (Vector?)args.Entry.Property("embeddings").CurrentValue;
        readEmbeddings = vec?.ToArray();
      }
    };

    var row = await _context.Set<PerspectiveRow<SplitProductionModel>>()
        .FirstOrDefaultAsync(ct);

    await Assert.That(row).IsNotNull();
    await Assert.That(readTenantId).IsEqualTo(tenantId)
      .Because("Shadow property tenant_id must be readable in Tracked handler");
    await Assert.That(readCategory).IsEqualTo("shadow-test")
      .Because("Shadow property category must be readable in Tracked handler");
    await Assert.That(readPrice).IsEqualTo(42.00m)
      .Because("Shadow property price must be readable in Tracked handler");
    await Assert.That(readEmbeddings).IsNotNull()
      .Because("Shadow property embeddings must be readable in Tracked handler");
    await Assert.That(readEmbeddings![0]).IsEqualTo(0.7f).Within(0.001f);
  }

  [Test]
  [Timeout(60000)]
  public async Task ChangeTrackerTracked_CanHydrateAndDetachAsync(CancellationToken ct) {
    // Validates the full hydration + immediate detach pattern works:
    // 1. Tracked fires with Data populated
    // 2. We can read shadow properties and write to Data
    // 3. We can detach immediately
    // 4. The result list still has valid references with hydrated data

    var tenantId = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenantId, "hydrate-test", 77.77m, "HydrateItem", "desc", [0.4f, 0.5f, 0.6f], null);

    _context!.ChangeTracker.Tracked += (sender, args) => {
      if (args.Entry.Entity is PerspectiveRow<SplitProductionModel> row && row.Data is not null) {
        // Hydrate physical fields from shadow properties (non-generic EntityEntry)
        _hydrateFromEntry(row, args.Entry);
        // Immediately detach
        args.Entry.State = EntityState.Detached;
      }
    };

    var result = await _context.Set<PerspectiveRow<SplitProductionModel>>()
        .FirstOrDefaultAsync(ct);

    // Verify hydration worked
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Data.TenantId).IsEqualTo(tenantId)
      .Because("Physical field must be hydrated via Tracked handler");
    await Assert.That(result.Data.Category).IsEqualTo("hydrate-test");
    await Assert.That(result.Data.Price).IsEqualTo(77.77m);
    await Assert.That(result.Data.Embeddings).IsNotNull();
    await Assert.That(result.Data.Embeddings![0]).IsEqualTo(0.4f).Within(0.001f);

    // Verify JSONB-only fields still work
    await Assert.That(result.Data.Name).IsEqualTo("HydrateItem");

    // Verify entity was detached
    await Assert.That(_context.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("Entity must be detached after hydration — zero tracking overhead");
  }

  [Test]
  [Timeout(60000)]
  public async Task ChangeTrackerTracked_BulkResults_AllHydratedAndDetachedAsync(CancellationToken ct) {
    // Validates that immediate detach during bulk materialization doesn't break iteration.
    // Each row is hydrated and detached as it's tracked, while ToListAsync is still collecting rows.

    var tenant1 = Guid.CreateVersion7();
    var tenant2 = Guid.CreateVersion7();
    var tenant3 = Guid.CreateVersion7();
    await _insertSplitRowAsync(tenant1, "bulk-a", 10.00m, "Item1", "desc", [0.1f, 0.2f, 0.3f], null);
    await _insertSplitRowAsync(tenant2, "bulk-b", 20.00m, "Item2", "desc", [0.4f, 0.5f, 0.6f], null);
    await _insertSplitRowAsync(tenant3, "bulk-c", 30.00m, "Item3", "desc", [0.7f, 0.8f, 0.9f], null);

    var hydratedCount = 0;

    _context!.ChangeTracker.Tracked += (sender, args) => {
      if (args.Entry.Entity is PerspectiveRow<SplitProductionModel> row && row.Data is not null) {
        _hydrateFromEntry(row, args.Entry);
        args.Entry.State = EntityState.Detached;
        Interlocked.Increment(ref hydratedCount);
      }
    };

    var rows = await _context.Set<PerspectiveRow<SplitProductionModel>>()
        .OrderBy(r => r.Data.Price) // Uses PhysicalFieldExpressionVisitor
        .ToListAsync(ct);

    await Assert.That(rows).Count().IsEqualTo(3);
    await Assert.That(hydratedCount).IsEqualTo(3)
      .Because("All 3 rows must be hydrated via Tracked handler");

    // Verify all rows have correct physical field values
    await Assert.That(rows[0].Data.Price).IsEqualTo(10.00m);
    await Assert.That(rows[0].Data.Category).IsEqualTo("bulk-a");
    await Assert.That(rows[1].Data.Price).IsEqualTo(20.00m);
    await Assert.That(rows[2].Data.Price).IsEqualTo(30.00m);

    // Verify all detached
    await Assert.That(_context.ChangeTracker.Entries().Count()).IsEqualTo(0)
      .Because("All entities must be detached — zero tracking overhead");
  }

  // ========================================================================
  // Helpers
  // ========================================================================

  /// <summary>
  /// Inserts a split row and returns its ID (for GetByIdAsync tests).
  /// </summary>
  private async Task<Guid> _insertSplitRowReturningIdAsync(
      Guid tenantId, string category, decimal price, string name, string description,
      float[]? embeddings, List<Guid>? tagIds) {
    var id = _idProvider.NewGuid();
    await _insertSplitRowCoreAsync(id, tenantId, category, price, name, description, embeddings, tagIds);
    return id;
  }

  /// <summary>
  /// Simulates what the generated PerspectiveRunner does in Split mode:
  /// 1. Strip physical fields from model (set to default)
  /// 2. Store physical field values via shadow properties
  /// The JSONB will contain stripped model (physical fields = default/null)
  /// </summary>
  private Task _insertSplitRowAsync(
      Guid tenantId, string category, decimal price, string name, string description,
      float[]? embeddings, List<Guid>? tagIds) =>
      _insertSplitRowCoreAsync(_idProvider.NewGuid(), tenantId, category, price, name, description, embeddings, tagIds);

  private async Task _insertSplitRowCoreAsync(
      Guid testId, Guid tenantId, string category, decimal price, string name, string description,
      float[]? embeddings, List<Guid>? tagIds) {

    var strategy = new PostgresUpsertStrategy();

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

  /// <summary>
  /// Hydrates physical field values from shadow properties into the Data model.
  /// Requires tracked query (NOT AsNoTracking) so context.Entry() can access shadow properties.
  /// This is what the framework needs to do automatically for Split-mode models.
  /// </summary>
  private async Task _insertCategoryRowAsync(string code, string displayName) {
    var strategy = new PostgresUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var model = new CategoryModel { Code = code, DisplayName = displayName };
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    await strategy.UpsertPerspectiveRowAsync(
        _context!, "wh_per_category_test", testId, model, metadata, new PerspectiveScope(), default);
    await _context!.SaveChangesAsync();
  }

  /// <summary>
  /// Hydrates from a non-generic EntityEntry (as provided by ChangeTracker.Tracked event).
  /// This is the pattern the framework will use — zero generic type args, just casts.
  /// </summary>
  private static void _hydrateFromEntry(PerspectiveRow<SplitProductionModel> row, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) {
    row.Data.TenantId = (Guid)entry.Property("tenant_id").CurrentValue!;
    var category = (string?)entry.Property("category").CurrentValue;
    if (category is not null) { row.Data.Category = category; }
    row.Data.Price = (decimal)entry.Property("price").CurrentValue!;
    row.Data.ParentId = (Guid?)entry.Property("parent_id").CurrentValue;
    var embeddings = (Vector?)entry.Property("embeddings").CurrentValue;
    if (embeddings is not null) { row.Data.Embeddings = embeddings.ToArray(); }
  }

  private static void _hydratePhysicalFields(DbContext context, PerspectiveRow<SplitProductionModel> row) {
    var entry = context.Entry(row);
    row.Data.TenantId = entry.Property<Guid>("tenant_id").CurrentValue;
    var category = entry.Property<string?>("category").CurrentValue;
    if (category is not null) { row.Data.Category = category; }
    row.Data.Price = entry.Property<decimal>("price").CurrentValue;
    row.Data.ParentId = entry.Property<Guid?>("parent_id").CurrentValue;
    var embeddings = entry.Property<Vector?>("embeddings").CurrentValue;
    if (embeddings is not null) { row.Data.Embeddings = embeddings.ToArray(); }

    // Detach after hydration — entity was only tracked to read shadow properties
    entry.State = EntityState.Detached;
  }

  private static void _hydratePhysicalFieldsList(DbContext context, List<PerspectiveRow<SplitProductionModel>> rows) {
    foreach (var row in rows) {
      _hydratePhysicalFields(context, row);
    }
  }
}
