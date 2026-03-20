using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for VectorSearchExtensions providing pgvector similarity search operations.
/// These tests cover argument validation, distance calculators, and VectorSearchResult.
/// </summary>
/// <remarks>
/// Integration tests with real PostgreSQL and pgvector test the actual query translation.
/// See VectorSearchIntegrationTests for PostgreSQL integration tests.
/// These unit tests verify the API contracts and helper methods.
/// </remarks>
[Category("VectorSearch")]
public class VectorSearchExtensionsTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // ========================================
  // Test Model and Infrastructure
  // ========================================

  /// <summary>
  /// Test model representing a document with embedding for similarity search.
  /// </summary>
  public class EmbeddingTestModel {
    public string Name { get; init; } = string.Empty;
    public float[]? ContentEmbedding { get; init; }
    public float[]? TitleEmbedding { get; init; }
  }

  private sealed class VectorTestDbContext(DbContextOptions<VectorSearchExtensionsTests.VectorTestDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<EmbeddingTestModel>>(entity => {
        entity.ToTable("wh_per_embedding_test_model");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.OwnsOne(e => e.Data, data => { data.ToJson("data"); });
        entity.ComplexProperty(e => e.Metadata).ToJson("metadata");
        entity.ComplexProperty(e => e.Scope).ToJson("scope");
      });
    }
  }

  private VectorTestDbContext _createInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<VectorTestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;
    return new VectorTestDbContext(options);
  }

  // ========================================
  // Category 1: VectorSearchResult Tests
  // ========================================

  /// <summary>
  /// Test 1: VectorSearchResult can be created with row, distance, and similarity.
  /// </summary>
  [Test]
  public async Task VectorSearchResult_Construction_SetsAllPropertiesAsync() {
    // Arrange
    _ = _createInMemoryDbContext();
    var testId = _idProvider.NewGuid();
    var model = new EmbeddingTestModel { Name = "Test" };
    var row = new PerspectiveRow<EmbeddingTestModel> {
      Id = testId,
      Data = model,
      Metadata = new PerspectiveMetadata { EventType = "Test", EventId = "1", Timestamp = DateTime.UtcNow },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    // Act
    var result = new VectorSearchResult<EmbeddingTestModel>(
        Row: row,
        Distance: 0.25,
        Similarity: 0.75
    );

    // Assert
    await Assert.That(result.Row).IsEqualTo(row);
    await Assert.That(result.Distance).IsEqualTo(0.25);
    await Assert.That(result.Similarity).IsEqualTo(0.75);
  }

  /// <summary>
  /// Test 2: VectorSearchResult uses value equality.
  /// </summary>
  [Test]
  public async Task VectorSearchResult_Equality_ComparesAllFieldsAsync() {
    // Arrange
    var row = new PerspectiveRow<EmbeddingTestModel> {
      Id = _idProvider.NewGuid(),
      Data = new EmbeddingTestModel { Name = "Test" },
      Metadata = new PerspectiveMetadata { EventType = "Test", EventId = "1", Timestamp = DateTime.UtcNow },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    // Act
    var result1 = new VectorSearchResult<EmbeddingTestModel>(row, 0.25, 0.75);
    var result2 = new VectorSearchResult<EmbeddingTestModel>(row, 0.25, 0.75);
    var result3 = new VectorSearchResult<EmbeddingTestModel>(row, 0.30, 0.70);

    // Assert
    await Assert.That(result1).IsEqualTo(result2);
    await Assert.That(result1).IsNotEqualTo(result3);
  }

  // ========================================
  // Category 2: Distance Calculator Tests
  // ========================================

  /// <summary>
  /// Test 3: CalculateCosineDistance returns 0 for identical vectors.
  /// </summary>
  [Test]
  public async Task CalculateCosineDistance_IdenticalVectors_ReturnsZeroAsync() {
    // Arrange
    var a = new float[] { 1.0f, 0.0f, 0.0f };
    var b = new float[] { 1.0f, 0.0f, 0.0f };

    // Act
    var distance = VectorSearchExtensions.CalculateCosineDistance(a, b);

    // Assert
    await Assert.That(distance).IsEqualTo(0.0).Within(0.0001);
  }

  /// <summary>
  /// Test 4: CalculateCosineDistance returns 1 for orthogonal vectors.
  /// </summary>
  [Test]
  public async Task CalculateCosineDistance_OrthogonalVectors_ReturnsOneAsync() {
    // Arrange
    var a = new float[] { 1.0f, 0.0f, 0.0f }; // X direction
    var b = new float[] { 0.0f, 1.0f, 0.0f }; // Y direction (orthogonal)

    // Act
    var distance = VectorSearchExtensions.CalculateCosineDistance(a, b);

    // Assert
    await Assert.That(distance).IsEqualTo(1.0).Within(0.0001);
  }

  /// <summary>
  /// Test 5: CalculateCosineDistance returns 2 for opposite vectors.
  /// </summary>
  [Test]
  public async Task CalculateCosineDistance_OppositeVectors_ReturnsTwoAsync() {
    // Arrange
    var a = new float[] { 1.0f, 0.0f, 0.0f };
    var b = new float[] { -1.0f, 0.0f, 0.0f }; // Opposite direction

    // Act
    var distance = VectorSearchExtensions.CalculateCosineDistance(a, b);

    // Assert
    await Assert.That(distance).IsEqualTo(2.0).Within(0.0001);
  }

  /// <summary>
  /// Test 6: CalculateL2Distance returns 0 for identical vectors.
  /// </summary>
  [Test]
  public async Task CalculateL2Distance_IdenticalVectors_ReturnsZeroAsync() {
    // Arrange
    var a = new float[] { 1.0f, 2.0f, 3.0f };
    var b = new float[] { 1.0f, 2.0f, 3.0f };

    // Act
    var distance = VectorSearchExtensions.CalculateL2Distance(a, b);

    // Assert
    await Assert.That(distance).IsEqualTo(0.0).Within(0.0001);
  }

  /// <summary>
  /// Test 7: CalculateL2Distance calculates correct Euclidean distance.
  /// </summary>
  [Test]
  public async Task CalculateL2Distance_DifferentVectors_CalculatesCorrectDistanceAsync() {
    // Arrange
    var a = new float[] { 0.0f, 0.0f, 0.0f }; // Origin
    var b = new float[] { 3.0f, 4.0f, 0.0f }; // 3-4-5 triangle

    // Act
    var distance = VectorSearchExtensions.CalculateL2Distance(a, b);

    // Assert - Distance should be 5 (3-4-5 right triangle)
    await Assert.That(distance).IsEqualTo(5.0).Within(0.0001);
  }

  /// <summary>
  /// Test 8: CalculateInnerProductDistance returns negative dot product.
  /// </summary>
  [Test]
  public async Task CalculateInnerProductDistance_CalculatesNegativeDotProductAsync() {
    // Arrange
    var a = new float[] { 1.0f, 2.0f, 3.0f };
    var b = new float[] { 4.0f, 5.0f, 6.0f };
    // Dot product = 1*4 + 2*5 + 3*6 = 4 + 10 + 18 = 32

    // Act
    var distance = VectorSearchExtensions.CalculateInnerProductDistance(a, b);

    // Assert - Should be negative dot product
    await Assert.That(distance).IsEqualTo(-32.0).Within(0.0001);
  }

  /// <summary>
  /// Test 9: Distance calculators return MaxValue for dimension mismatch.
  /// </summary>
  [Test]
  public async Task DistanceCalculators_DimensionMismatch_ReturnsMaxValueAsync() {
    // Arrange
    var a = new float[] { 1.0f, 2.0f };
    var b = new float[] { 1.0f, 2.0f, 3.0f };

    // Act & Assert
    await Assert.That(VectorSearchExtensions.CalculateCosineDistance(a, b)).IsEqualTo(double.MaxValue);
    await Assert.That(VectorSearchExtensions.CalculateL2Distance(a, b)).IsEqualTo(double.MaxValue);
    await Assert.That(VectorSearchExtensions.CalculateInnerProductDistance(a, b)).IsEqualTo(double.MaxValue);
  }

  /// <summary>
  /// Test 10: Distance calculators return MaxValue for empty vectors.
  /// </summary>
  [Test]
  public async Task DistanceCalculators_EmptyVectors_ReturnsMaxValueAsync() {
    // Arrange
    var a = Array.Empty<float>();
    var b = new float[] { 1.0f, 2.0f };

    // Act & Assert
    await Assert.That(VectorSearchExtensions.CalculateCosineDistance(a, b)).IsEqualTo(double.MaxValue);
    await Assert.That(VectorSearchExtensions.CalculateL2Distance(a, b)).IsEqualTo(double.MaxValue);
    await Assert.That(VectorSearchExtensions.CalculateInnerProductDistance(a, b)).IsEqualTo(double.MaxValue);
  }

  /// <summary>
  /// Test 11: Distance calculators throw on null arguments.
  /// </summary>
  [Test]
  public async Task DistanceCalculators_NullArguments_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var validVector = new float[] { 1.0f, 2.0f };

    // Act & Assert
    await Assert.That(() => VectorSearchExtensions.CalculateCosineDistance(null!, validVector))
        .Throws<ArgumentNullException>();
    await Assert.That(() => VectorSearchExtensions.CalculateCosineDistance(validVector, null!))
        .Throws<ArgumentNullException>();
    await Assert.That(() => VectorSearchExtensions.CalculateL2Distance(null!, validVector))
        .Throws<ArgumentNullException>();
    await Assert.That(() => VectorSearchExtensions.CalculateInnerProductDistance(null!, validVector))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 11b: CalculateCosineDistance returns MaxValue when magnitude is zero.
  /// </summary>
  [Test]
  public async Task CalculateCosineDistance_ZeroMagnitude_ReturnsMaxValueAsync() {
    // Arrange - Zero vector has zero magnitude
    var a = new float[] { 0.0f, 0.0f, 0.0f };
    var b = new float[] { 1.0f, 2.0f, 3.0f };

    // Act
    var distance = VectorSearchExtensions.CalculateCosineDistance(a, b);

    // Assert
    await Assert.That(distance).IsEqualTo(double.MaxValue);
  }

  // ========================================
  // Category 3: Extension Method Argument Validation
  // ========================================

  /// <summary>
  /// Test 12: OrderByCosineDistance throws on null vector selector.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_NullVectorSelector_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.OrderByCosineDistance((Expression<Func<EmbeddingTestModel, float[]?>>)null!, searchVector))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 13: OrderByCosineDistance throws on null search vector.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_NullSearchVector_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.OrderByCosineDistance(m => m.ContentEmbedding, (float[])null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 14: OrderByL2Distance throws on null arguments.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_NullArguments_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.OrderByL2Distance((Expression<Func<EmbeddingTestModel, float[]?>>)null!, searchVector))
        .Throws<ArgumentNullException>();
    await Assert.That(() => query.OrderByL2Distance(m => m.ContentEmbedding, (float[])null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 15: OrderByInnerProductDistance throws on null arguments.
  /// </summary>
  [Test]
  public async Task OrderByInnerProductDistance_NullArguments_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.OrderByInnerProductDistance(null!, searchVector))
        .Throws<ArgumentNullException>();
    await Assert.That(() => query.OrderByInnerProductDistance(m => m.ContentEmbedding, null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 16: WithinCosineDistance throws on negative threshold.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithinCosineDistance(m => m.ContentEmbedding, searchVector, threshold: -1.0))
        .Throws<ArgumentOutOfRangeException>();
  }

  /// <summary>
  /// Test 17: WithinL2Distance throws on negative threshold.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithinL2Distance(m => m.ContentEmbedding, searchVector, threshold: -0.1))
        .Throws<ArgumentOutOfRangeException>();
  }

  /// <summary>
  /// Test 18: WithCosineDistance throws on null arguments.
  /// </summary>
  [Test]
  public async Task WithCosineDistance_NullArguments_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithCosineDistance(null!, searchVector))
        .Throws<ArgumentNullException>();
    await Assert.That(() => query.WithCosineDistance(m => m.ContentEmbedding, null!))
        .Throws<ArgumentNullException>();
  }

  // ========================================
  // Category 4: Vector Selector Validation
  // ========================================

  /// <summary>
  /// Test 19: OrderByCosineDistance throws on invalid selector expression.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_InvalidSelector_ThrowsArgumentExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert - Lambda that isn't a property access should throw
    await Assert.That(() => query.OrderByCosineDistance(m => new float[] { 1, 0, 0 }, searchVector))
        .Throws<ArgumentException>();
  }

  /// <summary>
  /// Test 20: OrderByCosineDistance column-comparison throws on invalid selector.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_ColumnComparison_InvalidSelector_ThrowsArgumentExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert - Non-property access should throw
    await Assert.That(() => query.OrderByCosineDistance(
        m => m.ContentEmbedding,
        m => new float[] { 1, 0, 0 })) // Invalid!
        .Throws<ArgumentException>();
  }

  // ========================================
  // Category 5: Column-Comparison Argument Validation
  // ========================================

  /// <summary>
  /// Test 21: OrderByCosineDistance column-comparison throws on null selectors.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_ColumnComparison_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.OrderByCosineDistance(
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!,
        m => m.TitleEmbedding))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.OrderByCosineDistance(
        m => m.ContentEmbedding,
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 22: OrderByL2Distance column-comparison throws on null selectors.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_ColumnComparison_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.OrderByL2Distance(
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!,
        m => m.TitleEmbedding))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.OrderByL2Distance(
        m => m.ContentEmbedding,
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 23: WithinCosineDistance column-comparison throws on null selectors.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_ColumnComparison_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithinCosineDistance(
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!,
        m => m.TitleEmbedding,
        threshold: 0.5))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.WithinCosineDistance(
        m => m.ContentEmbedding,
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!,
        threshold: 0.5))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 24: WithinCosineDistance column-comparison throws on negative threshold.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_ColumnComparison_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithinCosineDistance(
        m => m.ContentEmbedding,
        m => m.TitleEmbedding,
        threshold: -0.5))
        .Throws<ArgumentOutOfRangeException>();
  }

  /// <summary>
  /// Test 25: WithinL2Distance column-comparison throws on null selectors.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_ColumnComparison_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithinL2Distance(
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!,
        m => m.TitleEmbedding,
        threshold: 0.5))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.WithinL2Distance(
        m => m.ContentEmbedding,
        (Expression<Func<EmbeddingTestModel, float[]?>>)null!,
        threshold: 0.5))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 26: WithinL2Distance column-comparison throws on negative threshold.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_ColumnComparison_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act & Assert
    await Assert.That(() => query.WithinL2Distance(
        m => m.ContentEmbedding,
        m => m.TitleEmbedding,
        threshold: -1.0))
        .Throws<ArgumentOutOfRangeException>();
  }

  // ========================================
  // Category 6: Generic Cross-Table Argument Validation
  // ========================================

  /// <summary>
  /// Test 27: Generic OrderByCosineDistance throws on null selectors.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_Generic_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>()
        .Select(r => new { Row = r, Other = r });

    // Act & Assert
    await Assert.That(() => query.OrderByCosineDistance(
        (Expression<Func<object, float[]?>>)null!,
        x => new float[] { 1, 0, 0 }))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.OrderByCosineDistance(
        x => new float[] { 1, 0, 0 },
        (Expression<Func<object, float[]?>>)null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 28: Generic OrderByL2Distance throws on null selectors.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_Generic_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>()
        .Select(r => new { Row = r, Other = r });

    // Act & Assert
    await Assert.That(() => query.OrderByL2Distance(
        (Expression<Func<object, float[]?>>)null!,
        x => new float[] { 1, 0, 0 }))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.OrderByL2Distance(
        x => new float[] { 1, 0, 0 },
        (Expression<Func<object, float[]?>>)null!))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 29: Generic WithinCosineDistance throws on null selectors.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_Generic_NullSelectors_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>()
        .Select(r => new { Row = r, Other = r });

    // Act & Assert
    await Assert.That(() => query.WithinCosineDistance(
        (Expression<Func<object, float[]?>>)null!,
        x => new float[] { 1, 0, 0 },
        threshold: 0.5))
        .Throws<ArgumentNullException>();

    await Assert.That(() => query.WithinCosineDistance(
        x => new float[] { 1, 0, 0 },
        (Expression<Func<object, float[]?>>)null!,
        threshold: 0.5))
        .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Test 30: Generic WithinCosineDistance throws on negative threshold.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_Generic_NegativeThreshold_ThrowsArgumentOutOfRangeExceptionAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>()
        .Select(r => new { Row = r, Other = r });

    // Act & Assert
    await Assert.That(() => query.WithinCosineDistance(
        x => new float[] { 1, 0, 0 },
        x => new float[] { 0, 1, 0 },
        threshold: -0.1))
        .Throws<ArgumentOutOfRangeException>();
  }

  // ========================================
  // Category 7: Expression Tree Building Verification
  // ========================================

  /// <summary>
  /// Test 31: OrderByCosineDistance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act - Should not throw (expression tree building works)
    var orderedQuery = query.OrderByCosineDistance(m => m.ContentEmbedding, searchVector);

    // Assert - Query was built successfully (IOrderedQueryable returned)
    await Assert.That(orderedQuery).IsNotNull();
    await Assert.That(orderedQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 32: OrderByL2Distance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var orderedQuery = query.OrderByL2Distance(m => m.ContentEmbedding, searchVector);

    // Assert
    await Assert.That(orderedQuery).IsNotNull();
    await Assert.That(orderedQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 33: OrderByInnerProductDistance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task OrderByInnerProductDistance_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var orderedQuery = query.OrderByInnerProductDistance(m => m.ContentEmbedding, searchVector);

    // Assert
    await Assert.That(orderedQuery).IsNotNull();
    await Assert.That(orderedQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 34: WithinCosineDistance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var filteredQuery = query.WithinCosineDistance(m => m.ContentEmbedding, searchVector, 0.5);

    // Assert
    await Assert.That(filteredQuery).IsNotNull();
    await Assert.That(filteredQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 35: WithinL2Distance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var filteredQuery = query.WithinL2Distance(m => m.ContentEmbedding, searchVector, 5.0);

    // Assert
    await Assert.That(filteredQuery).IsNotNull();
    await Assert.That(filteredQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 36: WithCosineDistance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task WithCosineDistance_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var searchVector = new float[] { 1.0f, 0.0f, 0.0f };
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var projectedQuery = query.WithCosineDistance(m => m.ContentEmbedding, searchVector);

    // Assert
    await Assert.That(projectedQuery).IsNotNull();
    await Assert.That(projectedQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 37: Column-comparison OrderByCosineDistance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task OrderByCosineDistance_ColumnComparison_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var orderedQuery = query.OrderByCosineDistance(m => m.ContentEmbedding, m => m.TitleEmbedding);

    // Assert
    await Assert.That(orderedQuery).IsNotNull();
    await Assert.That(orderedQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 38: Column-comparison OrderByL2Distance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task OrderByL2Distance_ColumnComparison_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var orderedQuery = query.OrderByL2Distance(m => m.ContentEmbedding, m => m.TitleEmbedding);

    // Assert
    await Assert.That(orderedQuery).IsNotNull();
    await Assert.That(orderedQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 39: Column-comparison WithinCosineDistance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task WithinCosineDistance_ColumnComparison_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var filteredQuery = query.WithinCosineDistance(m => m.ContentEmbedding, m => m.TitleEmbedding, 0.5);

    // Assert
    await Assert.That(filteredQuery).IsNotNull();
    await Assert.That(filteredQuery.Expression).IsNotNull();
  }

  /// <summary>
  /// Test 40: Column-comparison WithinL2Distance builds valid expression tree.
  /// </summary>
  [Test]
  public async Task WithinL2Distance_ColumnComparison_BuildsValidExpressionTreeAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var query = context.Set<PerspectiveRow<EmbeddingTestModel>>().AsQueryable();

    // Act
    var filteredQuery = query.WithinL2Distance(m => m.ContentEmbedding, m => m.TitleEmbedding, 5.0);

    // Assert
    await Assert.That(filteredQuery).IsNotNull();
    await Assert.That(filteredQuery.Expression).IsNotNull();
  }
}
