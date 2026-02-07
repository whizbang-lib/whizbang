using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="IDbUpsertStrategy.UpsertPerspectiveRowWithPhysicalFieldsAsync{TModel}"/>.
/// Validates that physical field values are correctly persisted to shadow properties.
/// </summary>
public class PhysicalFieldUpsertStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  /// <summary>
  /// Test model with physical fields for validation.
  /// </summary>
  public class PhysicalFieldTestModel {
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string? Description { get; init; }
  }

  /// <summary>
  /// Test DbContext that configures shadow properties for physical fields.
  /// </summary>
  private sealed class PhysicalFieldTestDbContext : DbContext {
    public PhysicalFieldTestDbContext(DbContextOptions<PhysicalFieldTestDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      // Configure PerspectiveRow<PhysicalFieldTestModel> with full entity configuration
      modelBuilder.Entity<PerspectiveRow<PhysicalFieldTestModel>>(entity => {
        entity.ToTable("wh_per_physical_field_test_model");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        // Use owned types for InMemory provider
        entity.OwnsOne(e => e.Data, data => {
          data.WithOwner();
        });

        entity.OwnsOne(e => e.Metadata, metadata => {
          metadata.WithOwner();
          metadata.Property(m => m.EventType).IsRequired();
          metadata.Property(m => m.EventId).IsRequired();
          metadata.Property(m => m.Timestamp).IsRequired();
        });

        // Use JSON conversion for Scope
        entity.Property(e => e.Scope)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);

        // Shadow properties for physical fields
        entity.Property<string?>("name").HasColumnName("name").HasMaxLength(200);
        entity.Property<decimal>("price").HasColumnName("price");

        // Indexes on shadow properties
        entity.HasIndex("name");
        entity.HasIndex("price");
      });
    }
  }

  private PhysicalFieldTestDbContext _createInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<PhysicalFieldTestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;

    return new PhysicalFieldTestDbContext(options);
  }

  [Test]
  public async Task UpsertWithPhysicalFields_WhenRecordDoesNotExist_CreatesShadowPropertiesAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var model = new PhysicalFieldTestModel {
      Name = "Widget",
      Price = 19.99m,
      Description = "A test widget"
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "price", model.Price }
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_physical_field_test_model",
        testId,
        model,
        metadata,
        scope,
        physicalFieldValues);

    // Assert - verify shadow property values
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Widget");
    await Assert.That(row.Data.Price).IsEqualTo(19.99m);

    // Verify shadow properties were set
    var entry = context.Entry(row);
    await Assert.That(entry.Property("name").CurrentValue).IsEqualTo("Widget");
    await Assert.That(entry.Property("price").CurrentValue).IsEqualTo(19.99m);
  }

  [Test]
  public async Task UpsertWithPhysicalFields_WhenRecordExists_UpdatesShadowPropertiesAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Create initial record
    var initialModel = new PhysicalFieldTestModel {
      Name = "Widget",
      Price = 19.99m,
      Description = "Original description"
    };
    var initialPhysicalValues = new Dictionary<string, object?> {
      { "name", initialModel.Name },
      { "price", initialModel.Price }
    };
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_physical_field_test_model",
        testId,
        initialModel,
        metadata,
        scope,
        initialPhysicalValues);

    // Act - update the record with new values
    var updatedModel = new PhysicalFieldTestModel {
      Name = "Super Widget",
      Price = 29.99m,
      Description = "Updated description"
    };
    var updatedPhysicalValues = new Dictionary<string, object?> {
      { "name", updatedModel.Name },
      { "price", updatedModel.Price }
    };
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_physical_field_test_model",
        testId,
        updatedModel,
        metadata,
        scope,
        updatedPhysicalValues);

    // Assert - verify shadow properties were updated
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Super Widget");
    await Assert.That(row.Data.Price).IsEqualTo(29.99m);

    var entry = context.Entry(row);
    await Assert.That(entry.Property("name").CurrentValue).IsEqualTo("Super Widget");
    await Assert.That(entry.Property("price").CurrentValue).IsEqualTo(29.99m);
  }

  [Test]
  public async Task UpsertWithPhysicalFields_WithNullValues_SetsShadowPropertiesToNullAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var model = new PhysicalFieldTestModel {
      Name = "Widget",
      Price = 19.99m,
      Description = null
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // Physical field with null value
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", null },
      { "price", model.Price }
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_physical_field_test_model",
        testId,
        model,
        metadata,
        scope,
        physicalFieldValues);

    // Assert - null value should be set
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();

    var entry = context.Entry(row!);
    await Assert.That(entry.Property("name").CurrentValue).IsNull();
    await Assert.That(entry.Property("price").CurrentValue).IsEqualTo(19.99m);
  }

  [Test]
  public async Task UpsertWithPhysicalFields_WithEmptyDictionary_DoesNotFailAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var model = new PhysicalFieldTestModel {
      Name = "Widget",
      Price = 19.99m,
      Description = "A test widget"
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();
    var emptyPhysicalFieldValues = new Dictionary<string, object?>();

    // Act - should not throw with empty dictionary
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_physical_field_test_model",
        testId,
        model,
        metadata,
        scope,
        emptyPhysicalFieldValues);

    // Assert - record should be created
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Widget");
  }

  [Test]
  public async Task UpsertWithPhysicalFields_PostgresStrategy_SetsShadowPropertiesAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new PostgresUpsertStrategy();
    var model = new PhysicalFieldTestModel {
      Name = "Premium Widget",
      Price = 49.99m,
      Description = "High-end widget"
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "price", model.Price }
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_physical_field_test_model",
        testId,
        model,
        metadata,
        scope,
        physicalFieldValues);

    // Assert - need to re-query to get shadow property values (change tracker was cleared)
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Premium Widget");
    await Assert.That(row.Data.Price).IsEqualTo(49.99m);

    // Shadow properties should be queryable
    var entry = context.Entry(row);
    await Assert.That(entry.Property("name").CurrentValue).IsEqualTo("Premium Widget");
    await Assert.That(entry.Property("price").CurrentValue).IsEqualTo(49.99m);
  }

  // ==================== Vector Field Tests ====================

  /// <summary>
  /// Test model with vector field for validation.
  /// </summary>
  public class VectorFieldTestModel {
    public string Name { get; init; } = string.Empty;
    public float[]? Embedding { get; init; }
  }

  /// <summary>
  /// Test DbContext that configures shadow properties for vector fields.
  /// </summary>
  private sealed class VectorFieldTestDbContext : DbContext {
    public VectorFieldTestDbContext(DbContextOptions<VectorFieldTestDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);

      modelBuilder.Entity<PerspectiveRow<VectorFieldTestModel>>(entity => {
        entity.ToTable("wh_per_vector_field_test_model");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        entity.OwnsOne(e => e.Data, data => {
          data.WithOwner();
        });

        entity.OwnsOne(e => e.Metadata, metadata => {
          metadata.WithOwner();
          metadata.Property(m => m.EventType).IsRequired();
          metadata.Property(m => m.EventId).IsRequired();
          metadata.Property(m => m.Timestamp).IsRequired();
        });

        entity.Property(e => e.Scope)
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);

        // Shadow property for vector field (stored as string in InMemory, actual vector in Postgres)
        entity.Property<string?>("name").HasColumnName("name").HasMaxLength(200);
        entity.Property<string?>("embedding").HasColumnName("embedding");
      });
    }
  }

  private VectorFieldTestDbContext _createVectorInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<VectorFieldTestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;

    return new VectorFieldTestDbContext(options);
  }

  [Test]
  public async Task UpsertWithPhysicalFields_WithVectorField_StoresArrayValueAsync() {
    // Arrange
    var context = _createVectorInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var embedding = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
    var model = new VectorFieldTestModel {
      Name = "Document",
      Embedding = embedding
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();

    // For InMemory testing, serialize the vector as JSON string
    // In real PostgreSQL, this would be a native vector type
    var embeddingJson = JsonSerializer.Serialize(embedding);
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "embedding", embeddingJson }
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_vector_field_test_model",
        testId,
        model,
        metadata,
        scope,
        physicalFieldValues);

    // Assert
    var row = await context.Set<PerspectiveRow<VectorFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Document");
    await Assert.That(row.Data.Embedding).IsNotNull();
    await Assert.That(row.Data.Embedding).IsEquivalentTo(embedding);

    // Verify shadow property was set
    var entry = context.Entry(row);
    await Assert.That(entry.Property("embedding").CurrentValue).IsEqualTo(embeddingJson);
  }

  [Test]
  public async Task UpsertWithPhysicalFields_WithNullVector_StoresNullAsync() {
    // Arrange
    var context = _createVectorInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var model = new VectorFieldTestModel {
      Name = "NoEmbedding",
      Embedding = null
    };
    var testId = _idProvider.NewGuid();
    var metadata = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope();
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "embedding", null }
    };

    // Act
    await strategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        context,
        "wh_per_vector_field_test_model",
        testId,
        model,
        metadata,
        scope,
        physicalFieldValues);

    // Assert
    var row = await context.Set<PerspectiveRow<VectorFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Embedding).IsNull();

    var entry = context.Entry(row);
    await Assert.That(entry.Property("embedding").CurrentValue).IsNull();
  }

  // ==================== Store Integration Tests ====================

  [Test]
  public async Task Store_UpsertWithPhysicalFieldsAsync_PersistsShadowPropertiesAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<PhysicalFieldTestModel>(
        context,
        "wh_per_physical_field_test_model",
        strategy);

    var model = new PhysicalFieldTestModel {
      Name = "StoreTest",
      Price = 99.99m,
      Description = "Testing store integration"
    };
    var testId = _idProvider.NewGuid();
    var physicalFieldValues = new Dictionary<string, object?> {
      { "name", model.Name },
      { "price", model.Price }
    };

    // Act
    await store.UpsertWithPhysicalFieldsAsync(testId, model, physicalFieldValues);

    // Assert - verify data was persisted
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("StoreTest");
    await Assert.That(row.Data.Price).IsEqualTo(99.99m);

    // Verify shadow properties
    var entry = context.Entry(row);
    await Assert.That(entry.Property("name").CurrentValue).IsEqualTo("StoreTest");
    await Assert.That(entry.Property("price").CurrentValue).IsEqualTo(99.99m);
  }

  [Test]
  public async Task Store_UpsertWithPhysicalFieldsAsync_UpdatesExistingRecordAsync() {
    // Arrange
    var context = _createInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<PhysicalFieldTestModel>(
        context,
        "wh_per_physical_field_test_model",
        strategy);
    var testId = _idProvider.NewGuid();

    // Create initial record
    var initialModel = new PhysicalFieldTestModel {
      Name = "Initial",
      Price = 10.00m,
      Description = "First version"
    };
    var initialPhysicalValues = new Dictionary<string, object?> {
      { "name", initialModel.Name },
      { "price", initialModel.Price }
    };
    await store.UpsertWithPhysicalFieldsAsync(testId, initialModel, initialPhysicalValues);

    // Act - update the record
    var updatedModel = new PhysicalFieldTestModel {
      Name = "Updated",
      Price = 20.00m,
      Description = "Second version"
    };
    var updatedPhysicalValues = new Dictionary<string, object?> {
      { "name", updatedModel.Name },
      { "price", updatedModel.Price }
    };
    await store.UpsertWithPhysicalFieldsAsync(testId, updatedModel, updatedPhysicalValues);

    // Assert
    var row = await context.Set<PerspectiveRow<PhysicalFieldTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Updated");
    await Assert.That(row.Data.Price).IsEqualTo(20.00m);
    await Assert.That(row.Version).IsEqualTo(2);

    var entry = context.Entry(row);
    await Assert.That(entry.Property("name").CurrentValue).IsEqualTo("Updated");
    await Assert.That(entry.Property("price").CurrentValue).IsEqualTo(20.00m);
  }
}
