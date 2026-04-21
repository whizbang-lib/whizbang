using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for VectorSearchExtensions with real PostgreSQL and pgvector.
/// Tests verify that extension methods correctly translate to SQL and return expected results.
/// </summary>
[Category("Integration")]
[Category("VectorSearch")]
public class VectorSearchIntegrationTests : IAsyncDisposable {
  private string? _testDatabaseName;
  private string _connectionString = null!;
  private DbContextOptions<VectorTestDbContext> _dbContextOptions = null!;
  private NpgsqlDataSource? _dataSource;

  // ========================================
  // Test Model and DbContext
  // ========================================

  /// <summary>
  /// Test model with two vector columns for column-comparison tests.
  /// </summary>
  public class VectorTestModel {
    [PhysicalField]
    public Guid Id { get; set; }

    [VectorField(3)]
    public float[]? Embedding { get; set; }

    [VectorField(3)]
    public float[]? ReferenceEmbedding { get; set; }

    public string Name { get; set; } = "";
  }

  /// <summary>
  /// Second test model for cross-table comparison tests.
  /// </summary>
  public class SecondVectorTestModel {
    [PhysicalField]
    public Guid Id { get; set; }

    [VectorField(3)]
    public float[]? TargetEmbedding { get; set; }

    public string Label { get; set; } = "";
  }

  /// <summary>
  /// DbContext for vector search tests with explicit configuration.
  /// Uses manual configuration instead of source generation for test isolation.
  /// </summary>
  private sealed class VectorTestDbContext(DbContextOptions<VectorSearchIntegrationTests.VectorTestDbContext> options) : DbContext(options) {
    public DbSet<PerspectiveRow<VectorTestModel>> VectorTestRows => Set<PerspectiveRow<VectorTestModel>>();
    public DbSet<PerspectiveRow<SecondVectorTestModel>> SecondVectorTestRows => Set<PerspectiveRow<SecondVectorTestModel>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      // Configure VectorTestModel perspective row
      modelBuilder.Entity<PerspectiveRow<VectorTestModel>>(entity => {
        entity.ToTable("wh_per_vector_test_model", "public");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.OwnsOne(e => e.Data, data => data.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata).ToJson("metadata");
        entity.ComplexProperty(e => e.Scope).ToJson("scope");

        // Shadow properties for vector columns
        // IMPORTANT: Shadow property names use snake_case to match the generator convention
        // This is what VectorSearchExtensions expects when converting property selectors
        entity.Property<Vector?>("embedding")
            .HasColumnName("embedding")
            .HasColumnType("vector(3)");

        entity.Property<Vector?>("reference_embedding")
            .HasColumnName("reference_embedding")
            .HasColumnType("vector(3)");
      });

      // Configure SecondVectorTestModel perspective row
      modelBuilder.Entity<PerspectiveRow<SecondVectorTestModel>>(entity => {
        entity.ToTable("wh_per_second_vector_test_model", "public");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.OwnsOne(e => e.Data, data => data.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata).ToJson("metadata");
        entity.ComplexProperty(e => e.Scope).ToJson("scope");

        // Shadow property for vector column
        // IMPORTANT: Shadow property names use snake_case to match the generator convention
        entity.Property<Vector?>("target_embedding")
            .HasColumnName("target_embedding")
            .HasColumnType("vector(3)");
      });
    }
  }

  // ========================================
  // Test Setup and Teardown
  // ========================================

  [Before(Test)]
  public async Task SetupAsync() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);

    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_vector_{Guid.NewGuid():N}";

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
    dataSourceBuilder.UseVector();
    _dataSource = dataSourceBuilder.Build();

    var optionsBuilder = new DbContextOptionsBuilder<VectorTestDbContext>();
    optionsBuilder.UseNpgsql(_dataSource, o => o.UseVector())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _dbContextOptions = optionsBuilder.Options;

    await _initializeDatabaseAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
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

  private async Task _initializeDatabaseAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    // Create pgvector extension
    await connection.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");

    // Create VectorTestModel table with vector columns
    await connection.ExecuteAsync(@"
      CREATE TABLE wh_per_vector_test_model (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        version INTEGER NOT NULL DEFAULT 1,
        embedding VECTOR(3),
        reference_embedding VECTOR(3)
      )");

    // Create SecondVectorTestModel table
    await connection.ExecuteAsync(@"
      CREATE TABLE wh_per_second_vector_test_model (
        id UUID PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
        version INTEGER NOT NULL DEFAULT 1,
        target_embedding VECTOR(3)
      )");
  }

  private VectorTestDbContext _createDbContext() {
    return new VectorTestDbContext(_dbContextOptions);
  }

  private async Task _seedTestDataAsync() {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    // Seed VectorTestModel rows with known vectors for predictable distance calculations
    // Using 3D vectors for simplicity:
    // - Item1: [1,0,0] - X direction
    // - Item2: [0,1,0] - Y direction (orthogonal to X)
    // - Item3: [-1,0,0] - Opposite X (cosine distance = 2 to Item1)
    // - Item4: [0.707,0.707,0] - 45 degrees between X and Y

    var items = new[] {
      (Id: Guid.NewGuid(), Name: "Item1", Embedding: "[1,0,0]", Reference: "[1,0,0]"),
      (Id: Guid.NewGuid(), Name: "Item2", Embedding: "[0,1,0]", Reference: "[0,1,0]"),
      (Id: Guid.NewGuid(), Name: "Item3", Embedding: "[-1,0,0]", Reference: "[-1,0,0]"),
      (Id: Guid.NewGuid(), Name: "Item4", Embedding: "[0.707,0.707,0]", Reference: "[0.707,0.707,0]")
    };

    foreach (var (Id, Name, Embedding, Reference) in items) {
      await _insertVectorTestModelAsync(connection, Id, Name, Embedding, Reference);
    }
  }

  /// <summary>
  /// Helper to insert a VectorTestModel row with proper JSON escaping.
  /// </summary>
  private static async Task _insertVectorTestModelAsync(
      NpgsqlConnection connection,
      Guid id,
      string name,
      string embedding,
      string reference) {
    var dataJson = System.Text.Json.JsonSerializer.Serialize(new {
      Id = id.ToString(),
      Name = name,
      Embedding = (float[]?)null,
      ReferenceEmbedding = (float[]?)null
    });
    const string metadataJson = """{"EventType":"Test","EventId":"1","Timestamp":"2024-01-01T00:00:00Z"}""";
    const string scopeJson = "{}";

    await connection.ExecuteAsync(@"
      INSERT INTO wh_per_vector_test_model (id, data, metadata, scope, embedding, reference_embedding)
      VALUES (@Id, @Data::jsonb, @Metadata::jsonb, @Scope::jsonb, @Embedding::vector, @Reference::vector)",
        new { Id = id, Data = dataJson, Metadata = metadataJson, Scope = scopeJson, Embedding = embedding, Reference = reference });
  }

  /// <summary>
  /// Helper to insert a SecondVectorTestModel row with proper JSON escaping.
  /// </summary>
  private static async Task _insertSecondVectorTestModelAsync(
      NpgsqlConnection connection,
      Guid id,
      string label,
      string targetEmbedding) {
    var dataJson = System.Text.Json.JsonSerializer.Serialize(new {
      Id = id.ToString(),
      Label = label
    });
    const string metadataJson = """{"EventType":"Test","EventId":"1","Timestamp":"2024-01-01T00:00:00Z"}""";
    const string scopeJson = "{}";

    await connection.ExecuteAsync(@"
      INSERT INTO wh_per_second_vector_test_model (id, data, metadata, scope, target_embedding)
      VALUES (@Id, @Data::jsonb, @Metadata::jsonb, @Scope::jsonb, @TargetEmbedding::vector)",
        new { Id = id, Data = dataJson, Metadata = metadataJson, Scope = scopeJson, TargetEmbedding = targetEmbedding });
  }

  // ========================================
  // Category 1: Constant Search Vector Tests
  // ========================================

  /// <summary>
  /// Test 1: OrderByCosineDistance with constant search vector orders results correctly.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_WithConstant_ReturnsResultsInCorrectOrderAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 }; // Search for X direction

    // Act
    var results = await context.VectorTestRows
        .OrderByCosineDistance(m => m.Embedding, searchVector)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(4);
    // Item1 [1,0,0] should be first (distance 0)
    await Assert.That(results[0].Data.Name).IsEqualTo("Item1");
    // Item4 [0.707,0.707,0] should be second (distance ~0.29)
    await Assert.That(results[1].Data.Name).IsEqualTo("Item4");
    // Item2 [0,1,0] should be third (distance 1 - orthogonal)
    await Assert.That(results[2].Data.Name).IsEqualTo("Item2");
    // Item3 [-1,0,0] should be last (distance 2 - opposite)
    await Assert.That(results[3].Data.Name).IsEqualTo("Item3");
  }

  /// <summary>
  /// Test 2: OrderByL2Distance with constant search vector orders results correctly.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_WithConstant_ReturnsResultsInCorrectOrderAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act
    var results = await context.VectorTestRows
        .OrderByL2Distance(m => m.Embedding, searchVector)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(4);
    // Item1 [1,0,0] should be first (L2 distance 0)
    await Assert.That(results[0].Data.Name).IsEqualTo("Item1");
    // Item3 [-1,0,0] should be last (L2 distance 2)
    await Assert.That(results[3].Data.Name).IsEqualTo("Item3");
  }

  /// <summary>
  /// Test 3: OrderByInnerProductDistance with constant search vector orders results correctly.
  /// </summary>
  [Test]
  public async Task OrderByInnerProductDistance_WithConstant_ReturnsResultsInCorrectOrderAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act
    var results = await context.VectorTestRows
        .OrderByInnerProductDistance(m => m.Embedding, searchVector)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(4);
    // Inner product distance is -dot_product, so higher dot product = lower distance
    // Item1 [1,0,0] has dot product 1, distance -1 (lowest)
    // Item3 [-1,0,0] has dot product -1, distance 1 (highest)
    await Assert.That(results[0].Data.Name).IsEqualTo("Item1");
    await Assert.That(results[3].Data.Name).IsEqualTo("Item3");
  }

  /// <summary>
  /// Test 4: WithinCosineDistance with constant filters correctly.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_WithConstant_FiltersCorrectlyAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };
    // Threshold 0.5: should include Item1 (0) and Item4 (~0.29), exclude Item2 (1) and Item3 (2)

    // Act
    var results = await context.VectorTestRows
        .WithinCosineDistance(m => m.Embedding, searchVector, threshold: 0.5)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).ToList();
    await Assert.That(names).Contains("Item1");
    await Assert.That(names).Contains("Item4");
    await Assert.That(names).DoesNotContain("Item2");
    await Assert.That(names).DoesNotContain("Item3");
  }

  /// <summary>
  /// Test 5: WithinL2Distance with constant filters correctly.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_WithConstant_FiltersCorrectlyAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };
    // L2 distances: Item1=0, Item4=~0.59, Item2=sqrt(2)=~1.41, Item3=2
    // Threshold 1.0: should include Item1 and Item4

    // Act
    var results = await context.VectorTestRows
        .WithinL2Distance(m => m.Embedding, searchVector, threshold: 1.0)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).ToList();
    await Assert.That(names).Contains("Item1");
    await Assert.That(names).Contains("Item4");
  }

  /// <summary>
  /// Test 6: WithCosineDistance returns distance and similarity values.
  /// </summary>
  [Test]
  public async Task WithCosineDistance_WithConstant_ReturnsDistanceAndSimilarityAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act - Use OrderByCosineDistance for SQL-side ordering, then WithCosineDistance for projection
    // Note: WithCosineDistance should be used as the final projection, not for intermediate SQL operations
    var results = await context.VectorTestRows
        .OrderByCosineDistance(m => m.Embedding, searchVector)
        .WithCosineDistance(m => m.Embedding, searchVector)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(4);

    // Item1 should have distance ~0 and similarity ~1 (first due to OrderByCosineDistance)
    var item1Result = results.First(r => r.Row.Data.Name == "Item1");
    await Assert.That(item1Result.Distance).IsLessThanOrEqualTo(0.001);
    await Assert.That(item1Result.Similarity).IsGreaterThanOrEqualTo(0.999);

    // Item3 should have distance ~2 and similarity ~-1
    var item3Result = results.First(r => r.Row.Data.Name == "Item3");
    await Assert.That(item3Result.Distance).IsGreaterThanOrEqualTo(1.999);
    await Assert.That(item3Result.Similarity).IsLessThanOrEqualTo(-0.999);
  }

  // ========================================
  // Category 2: Column-Based Search Vector Tests
  // ========================================

  /// <summary>
  /// Test 7: OrderByCosineDistance with column selector compares columns in SQL.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_WithColumnSelector_ComparesColumnsInSqlAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    // Insert items with different reference embeddings for cross-column comparison
    var items = new[] {
      // Embedding matches Reference exactly
      (Id: Guid.NewGuid(), Name: "Match", Embedding: "[1,0,0]", Reference: "[1,0,0]"),
      // Embedding differs from Reference
      (Id: Guid.NewGuid(), Name: "Differ", Embedding: "[1,0,0]", Reference: "[0,1,0]")
    };

    foreach (var (Id, Name, Embedding, Reference) in items) {
      await _insertVectorTestModelAsync(connection, Id, Name, Embedding, Reference);
    }

    await using var context = _createDbContext();

    // Act - Compare Embedding to ReferenceEmbedding (column to column)
    var results = await context.VectorTestRows
        .OrderByCosineDistance(m => m.Embedding, m => m.ReferenceEmbedding)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    // Match should come first (distance 0 - same vectors)
    await Assert.That(results[0].Data.Name).IsEqualTo("Match");
    // Differ should be second (distance 1 - orthogonal)
    await Assert.That(results[1].Data.Name).IsEqualTo("Differ");
  }

  /// <summary>
  /// Test 8: OrderByL2Distance with column selector compares columns in SQL.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_WithColumnSelector_ComparesColumnsInSqlAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var items = new[] {
      (Id: Guid.NewGuid(), Name: "Match", Embedding: "[1,0,0]", Reference: "[1,0,0]"),
      (Id: Guid.NewGuid(), Name: "Differ", Embedding: "[1,0,0]", Reference: "[-1,0,0]")
    };

    foreach (var (Id, Name, Embedding, Reference) in items) {
      await _insertVectorTestModelAsync(connection, Id, Name, Embedding, Reference);
    }

    await using var context = _createDbContext();

    // Act
    var results = await context.VectorTestRows
        .OrderByL2Distance(m => m.Embedding, m => m.ReferenceEmbedding)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Data.Name).IsEqualTo("Match");
    await Assert.That(results[1].Data.Name).IsEqualTo("Differ");
  }

  /// <summary>
  /// Test 9: WithinCosineDistance with column selector filters in SQL.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_WithColumnSelector_FiltersInSqlAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var items = new[] {
      (Id: Guid.NewGuid(), Name: "Match", Embedding: "[1,0,0]", Reference: "[1,0,0]"),    // Distance 0
      (Id: Guid.NewGuid(), Name: "Close", Embedding: "[1,0,0]", Reference: "[0.9,0.44,0]"), // Distance ~0.1
      (Id: Guid.NewGuid(), Name: "Far", Embedding: "[1,0,0]", Reference: "[0,1,0]")        // Distance 1
    };

    foreach (var (Id, Name, Embedding, Reference) in items) {
      await _insertVectorTestModelAsync(connection, Id, Name, Embedding, Reference);
    }

    await using var context = _createDbContext();

    // Act - Threshold 0.5 should include Match and Close, exclude Far
    var results = await context.VectorTestRows
        .WithinCosineDistance(m => m.Embedding, m => m.ReferenceEmbedding, threshold: 0.5)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).ToList();
    await Assert.That(names).Contains("Match");
    await Assert.That(names).Contains("Close");
    await Assert.That(names).DoesNotContain("Far");
  }

  /// <summary>
  /// Test 10: WithinL2Distance with column selector filters in SQL.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_WithColumnSelector_FiltersInSqlAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var items = new[] {
      (Id: Guid.NewGuid(), Name: "Match", Embedding: "[1,0,0]", Reference: "[1,0,0]"),
      (Id: Guid.NewGuid(), Name: "Close", Embedding: "[1,0,0]", Reference: "[0.9,0,0]"),
      (Id: Guid.NewGuid(), Name: "Far", Embedding: "[1,0,0]", Reference: "[-1,0,0]")
    };

    foreach (var (Id, Name, Embedding, Reference) in items) {
      await _insertVectorTestModelAsync(connection, Id, Name, Embedding, Reference);
    }

    await using var context = _createDbContext();

    // Act - L2 distances: Match=0, Close=0.1, Far=2. Threshold 0.5 should exclude Far.
    var results = await context.VectorTestRows
        .WithinL2Distance(m => m.Embedding, m => m.ReferenceEmbedding, threshold: 0.5)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).ToList();
    await Assert.That(names).Contains("Match");
    await Assert.That(names).Contains("Close");
    await Assert.That(names).DoesNotContain("Far");
  }

  // ========================================
  // Category 3: Cross-Table/Join Tests
  // ========================================

  /// <summary>
  /// Test 11: OrderByCosineDistance can compare vectors from different tables via join.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_CrossTable_ComparesVectorsFromDifferentTablesAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    // Seed VectorTestModel rows
    var vectorItems = new[] {
      (Id: Guid.NewGuid(), Name: "V1", Embedding: "[1,0,0]"),
      (Id: Guid.NewGuid(), Name: "V2", Embedding: "[0,1,0]"),
      (Id: Guid.NewGuid(), Name: "V3", Embedding: "[-1,0,0]")
    };

    foreach (var (Id, Name, Embedding) in vectorItems) {
      await _insertVectorTestModelAsync(connection, Id, Name, Embedding, Embedding);
    }

    // Seed SecondVectorTestModel with target vector [1,0,0]
    var targetId = Guid.NewGuid();
    await _insertSecondVectorTestModelAsync(connection, targetId, "Target", "[1,0,0]");

    await using var context = _createDbContext();

    // Act - Join and compare: find VectorTestModel rows closest to SecondVectorTestModel's TargetEmbedding
    var results = await context.VectorTestRows
        .Join(
            context.SecondVectorTestRows,
            v => true,
            s => true,
            (v, s) => new { Vector = v, Second = s })
        .OrderByCosineDistance(
            x => x.Vector.Data.Embedding,
            x => x.Second.Data.TargetEmbedding)
        .Select(x => x.Vector)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(3);
    // V1 [1,0,0] should be first (matches target [1,0,0])
    await Assert.That(results[0].Data.Name).IsEqualTo("V1");
    // V3 [-1,0,0] should be last (opposite to target)
    await Assert.That(results[2].Data.Name).IsEqualTo("V3");
  }

  /// <summary>
  /// Test 12: OrderByCosineDistance works with anonymous types from joins.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_JoinedAnonymousType_TranslatesToSqlAsync() {
    // Arrange
    await _seedTestDataAsync();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var targetId = Guid.NewGuid();
    await _insertSecondVectorTestModelAsync(connection, targetId, "SearchTarget", "[1,0,0]");

    await using var context = _createDbContext();

    // Act - Anonymous type projection with vector comparison
    var results = await context.VectorTestRows
        .SelectMany(
            v => context.SecondVectorTestRows.Where(s => s.Data.Label == "SearchTarget"),
            (v, s) => new { VectorRow = v, Target = s })
        .OrderByCosineDistance(
            x => x.VectorRow.Data.Embedding,
            x => x.Target.Data.TargetEmbedding)
        .Take(2)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    // Item1 should be first (closest to [1,0,0])
    await Assert.That(results[0].VectorRow.Data.Name).IsEqualTo("Item1");
  }

  /// <summary>
  /// Test 13: WithinCosineDistance works across tables via joins.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_CrossTable_FiltersInSqlAsync() {
    // Arrange
    await _seedTestDataAsync();

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();

    var targetId = Guid.NewGuid();
    await _insertSecondVectorTestModelAsync(connection, targetId, "FilterTarget", "[1,0,0]");

    await using var context = _createDbContext();

    // Act - Filter: cosine distance < 0.5 should include Item1 and Item4
    var results = await context.VectorTestRows
        .SelectMany(
            v => context.SecondVectorTestRows.Where(s => s.Data.Label == "FilterTarget"),
            (v, s) => new { VectorRow = v, Target = s })
        .WithinCosineDistance(
            x => x.VectorRow.Data.Embedding,
            x => x.Target.Data.TargetEmbedding,
            threshold: 0.5)
        .Select(x => x.VectorRow)
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var names = results.Select(r => r.Data.Name).ToList();
    await Assert.That(names).Contains("Item1");
    await Assert.That(names).Contains("Item4");
  }

  // ========================================
  // Category 4: Validation Tests
  // ========================================

  /// <summary>
  /// Test 14: Vector selector with invalid expression throws ArgumentException.
  /// </summary>
  [Test]
  public async Task VectorSelector_InvalidExpression_ThrowsArgumentExceptionAsync() {
    // Arrange
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act & Assert - Lambda that isn't a property access should throw immediately during expression building
    await Assert.That(() => context.VectorTestRows
        .OrderByCosineDistance(m => new float[] { 1, 0, 0 }, searchVector)) // Not a property!
        .Throws<ArgumentException>();
  }

  /// <summary>
  /// Test 15: Search vector selector with invalid expression throws ArgumentException.
  /// </summary>
  [Test]
  public async Task SearchVectorSelector_InvalidExpression_ThrowsArgumentExceptionAsync() {
    // Arrange
    await using var context = _createDbContext();

    // Act & Assert - Column comparison with non-property lambda should throw immediately
    await Assert.That(() => context.VectorTestRows
        .OrderByCosineDistance(
            m => m.Embedding,
            m => new float[] { 1, 0, 0 })) // Not a property!
        .Throws<ArgumentException>();
  }

  /// <summary>
  /// Test 16: Null vector selector throws ArgumentNullException.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_NullVectorSelector_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act & Assert
    await Assert.That(() => context.VectorTestRows
        .OrderByCosineDistance(null!, searchVector))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 17: Null search vector throws ArgumentNullException.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_NullSearchVector_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = _createDbContext();

    // Act & Assert
    await Assert.That(() => context.VectorTestRows
        .OrderByCosineDistance(m => m.Embedding, (float[])null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 18: WithinCosineDistance with negative threshold throws ArgumentOutOfRangeException.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act & Assert
    await Assert.That(() => context.VectorTestRows
        .WithinCosineDistance(m => m.Embedding, searchVector, threshold: -1.0))
        .Throws<ArgumentOutOfRangeException>();
  }

  /// <summary>
  /// Test 19: WithinL2Distance with negative threshold throws ArgumentOutOfRangeException.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act & Assert
    await Assert.That(() => context.VectorTestRows
        .WithinL2Distance(m => m.Embedding, searchVector, threshold: -0.1))
        .Throws<ArgumentOutOfRangeException>();
  }

  // ========================================
  // Category 5: Chaining Tests
  // ========================================

  /// <summary>
  /// Test 20: Multiple vector operations can be chained.
  /// </summary>
  [Test]
  public async Task VectorOperations_CanBeChainedAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act - Chain WithinCosineDistance with OrderByCosineDistance
    var results = await context.VectorTestRows
        .WithinCosineDistance(m => m.Embedding, searchVector, threshold: 1.5)
        .OrderByCosineDistance(m => m.Embedding, searchVector)
        .Take(2)
        .ToListAsync();

    // Assert - Should have Item1 (distance 0), Item4 (~0.29), but not Item3 (2 > 1.5)
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].Data.Name).IsEqualTo("Item1");
    await Assert.That(results[1].Data.Name).IsEqualTo("Item4");
  }

  /// <summary>
  /// Test 21: WithCosineDistance can be used with additional LINQ operations.
  /// </summary>
  [Test]
  public async Task WithCosineDistance_WithLinqOperations_WorksCorrectlyAsync() {
    // Arrange
    await _seedTestDataAsync();
    await using var context = _createDbContext();
    var searchVector = new float[] { 1, 0, 0 };

    // Act - Use WithinCosineDistance for SQL-side filtering, OrderByCosineDistance for sorting,
    // then WithCosineDistance for the final projection with distance/similarity values
    // Note: WithCosineDistance should be used as the final projection, not for intermediate SQL operations
    // Threshold 0.5 includes Item1 (distance=0) and Item4 (distance≈0.01), excludes Item2 (distance=1) and Item3 (distance=2)
    var results = await context.VectorTestRows
        .WithinCosineDistance(m => m.Embedding, searchVector, 0.5)
        .OrderByCosineDistance(m => m.Embedding, searchVector)
        .WithCosineDistance(m => m.Embedding, searchVector)
        .Select(r => new { r.Row.Data.Name, r.Distance, r.Similarity })
        .ToListAsync();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2); // Item1, Item4 (not Item2, Item3)
    await Assert.That(results[0].Name).IsEqualTo("Item1");
    await Assert.That(results[0].Similarity).IsGreaterThanOrEqualTo(0.999);
  }
}
