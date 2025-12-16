using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for EFCorePostgresLensQuery implementation of ILensQuery.
/// These tests use EF Core InMemory provider for fast, isolated testing.
/// </summary>
public class EFCorePostgresLensQueryTests {
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  private TestDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;

    return new TestDbContext(options);
  }

  private async Task SeedPerspectiveAsync(
      TestDbContext context,
      Guid id,
      TestModel data,
      PerspectiveMetadata? metadata = null,
      PerspectiveScope? scope = null) {

    // Create fresh instances to avoid EF Core owned type tracking issues
    var metadataInstance = metadata != null
        ? new PerspectiveMetadata {
          EventType = metadata.EventType,
          EventId = metadata.EventId,
          Timestamp = metadata.Timestamp,
          CorrelationId = metadata.CorrelationId,
          CausationId = metadata.CausationId
        }
        : new PerspectiveMetadata {
          EventType = "TestEvent",
          EventId = Guid.NewGuid().ToString(),
          Timestamp = DateTime.UtcNow
        };

    var scopeInstance = scope != null
        ? new PerspectiveScope {
          TenantId = scope.TenantId,
          CustomerId = scope.CustomerId,
          UserId = scope.UserId,
          OrganizationId = scope.OrganizationId
        }
        : new PerspectiveScope();

    var row = new PerspectiveRow<TestModel> {
      Id = id,
      Data = data,
      Metadata = metadataInstance,
      Scope = scopeInstance,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    context.Set<PerspectiveRow<TestModel>>().Add(row);
    await context.SaveChangesAsync();
  }

  [Test]
  public async Task GetByIdAsync_WhenModelExists_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");
    var testId = _idProvider.NewGuid();

    var testModel = new TestModel { Name = "Test", Value = 123 };
    await SeedPerspectiveAsync(context, testId, testModel);

    // Act
    var result = await lensQuery.GetByIdAsync(testId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Test");
    await Assert.That(result.Value).IsEqualTo(123);
  }

  [Test]
  public async Task GetByIdAsync_WhenModelDoesNotExist_ReturnsNullAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    // Act
    var result = await lensQuery.GetByIdAsync(_idProvider.NewGuid());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Query_ReturnsIQueryable_WithCorrectTypeAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    // Act
    var query = lensQuery.Query;

    // Assert
    await Assert.That(query).IsNotNull();
    await Assert.That(query is IQueryable<PerspectiveRow<TestModel>>).IsTrue();
  }

  [Test]
  public async Task Query_CanFilterByDataFields_ReturnsMatchingRowsAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Alice", Value = 100 });
    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Bob", Value = 200 });
    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Charlie", Value = 100 });

    // Act - Filter by data field
    var results = await lensQuery.Query
        .Where(row => row.Data.Value == 100)
        .ToListAsync();

    // Assert
    await Assert.That(results).HasCount().EqualTo(2);
    await Assert.That(results.Select(r => r.Data.Name)).Contains("Alice");
    await Assert.That(results.Select(r => r.Data.Name)).Contains("Charlie");
  }

  [Test]
  public async Task Query_CanFilterByMetadataFields_ReturnsMatchingRowsAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    var metadata1 = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = "evt-1",
      Timestamp = DateTime.UtcNow
    };

    var metadata2 = new PerspectiveMetadata {
      EventType = "OrderUpdated",
      EventId = "evt-2",
      Timestamp = DateTime.UtcNow
    };

    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();

    await SeedPerspectiveAsync(context, id1, new TestModel { Name = "Test1", Value = 1 }, metadata1);
    await SeedPerspectiveAsync(context, id2, new TestModel { Name = "Test2", Value = 2 }, metadata2);
    await SeedPerspectiveAsync(context, id3, new TestModel { Name = "Test3", Value = 3 }, metadata1);

    // Act - Filter by metadata field
    var results = await lensQuery.Query
        .Where(row => row.Metadata.EventType == "OrderCreated")
        .ToListAsync();

    // Assert
    await Assert.That(results).HasCount().EqualTo(2);
    await Assert.That(results.Select(r => r.Id)).Contains(id1);
    await Assert.That(results.Select(r => r.Id)).Contains(id3);
  }

  [Test]
  public async Task Query_CanFilterByScopeFields_ReturnsMatchingRowsAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    var scope1 = new PerspectiveScope { TenantId = "tenant-1", UserId = "user-1" };
    var scope2 = new PerspectiveScope { TenantId = "tenant-2", UserId = "user-2" };

    var id1 = _idProvider.NewGuid();
    var id2 = _idProvider.NewGuid();
    var id3 = _idProvider.NewGuid();

    await SeedPerspectiveAsync(context, id1, new TestModel { Name = "Test1", Value = 1 }, scope: scope1);
    await SeedPerspectiveAsync(context, id2, new TestModel { Name = "Test2", Value = 2 }, scope: scope2);
    await SeedPerspectiveAsync(context, id3, new TestModel { Name = "Test3", Value = 3 }, scope: scope1);

    // Act - Filter by scope field
    var results = await lensQuery.Query
        .Where(row => row.Scope.TenantId == "tenant-1")
        .ToListAsync();

    // Assert
    await Assert.That(results).HasCount().EqualTo(2);
    await Assert.That(results.Select(r => r.Id)).Contains(id1);
    await Assert.That(results.Select(r => r.Id)).Contains(id3);
  }

  [Test]
  public async Task Query_CanProjectAcrossColumns_ReturnsAnonymousTypeAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = "evt-1",
      Timestamp = DateTime.UtcNow
    };

    var scope = new PerspectiveScope { TenantId = "tenant-1" };

    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Alice", Value = 100 }, metadata, scope);

    // Act - Project fields from data, metadata, and scope
    var results = await lensQuery.Query
        .Select(row => new {
          Name = row.Data.Name,
          EventType = row.Metadata.EventType,
          TenantId = row.Scope.TenantId
        })
        .ToListAsync();

    // Assert
    await Assert.That(results).HasCount().EqualTo(1);
    await Assert.That(results[0].Name).IsEqualTo("Alice");
    await Assert.That(results[0].EventType).IsEqualTo("OrderCreated");
    await Assert.That(results[0].TenantId).IsEqualTo("tenant-1");
  }

  [Test]
  public async Task Query_SupportsCombinedFilters_FromAllColumnsAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    var metadata1 = new PerspectiveMetadata { EventType = "OrderCreated", EventId = "evt-1", Timestamp = DateTime.UtcNow };
    var metadata2 = new PerspectiveMetadata { EventType = "OrderUpdated", EventId = "evt-2", Timestamp = DateTime.UtcNow };
    var scope1 = new PerspectiveScope { TenantId = "tenant-1" };
    var scope2 = new PerspectiveScope { TenantId = "tenant-2" };

    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Alice", Value = 100 }, metadata1, scope1);
    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Bob", Value = 200 }, metadata2, scope1);
    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Charlie", Value = 100 }, metadata1, scope2);

    // Act - Filter by data + metadata + scope in single query
    var results = await lensQuery.Query
        .Where(row =>
            row.Data.Value == 100 &&
            row.Metadata.EventType == "OrderCreated" &&
            row.Scope.TenantId == "tenant-1")
        .ToListAsync();

    // Assert
    await Assert.That(results).HasCount().EqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Alice");
  }

  [Test]
  public async Task Query_SupportsComplexLinqOperations_WithOrderByAndSkipTakeAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var lensQuery = new EFCorePostgresLensQuery<TestModel>(context, "test_perspective");

    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Alice", Value = 300 });
    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Bob", Value = 100 });
    await SeedPerspectiveAsync(context, _idProvider.NewGuid(), new TestModel { Name = "Charlie", Value = 200 });

    // Act - Complex LINQ with OrderBy, Skip, Take
    var results = await lensQuery.Query
        .OrderBy(row => row.Data.Value)
        .Skip(1)
        .Take(1)
        .ToListAsync();

    // Assert
    await Assert.That(results).HasCount().EqualTo(1);
    await Assert.That(results[0].Data.Name).IsEqualTo("Charlie");
    await Assert.That(results[0].Data.Value).IsEqualTo(200);
  }

  [Test]
  public async Task Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    var exception = await Assert.That(() =>
        new EFCorePostgresLensQuery<TestModel>(null!, "test_perspective")
    ).ThrowsException();

    await Assert.That(exception).IsTypeOf<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();

    // Act & Assert
    var exception = await Assert.That(() =>
        new EFCorePostgresLensQuery<TestModel>(context, null!)
    ).ThrowsException();

    await Assert.That(exception).IsTypeOf<ArgumentNullException>();
  }
}

/// <summary>
/// Test model for lens query tests.
/// </summary>
public class TestModel {
  public required string Name { get; init; }
  public required int Value { get; init; }
}

/// <summary>
/// Test DbContext for EF Core InMemory testing.
/// Note: Uses owned types instead of JSON for InMemory compatibility.
/// The actual PostgreSQL implementation will use JSON columns.
/// </summary>
public class TestDbContext : DbContext {
  public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // Configure PerspectiveRow<TestModel> as entity
    ConfigurePerspectiveRow<TestModel>(modelBuilder);

    // Configure PerspectiveRow<StoreTestModel> as entity (for store tests)
    ConfigurePerspectiveRow<StoreTestModel>(modelBuilder);
  }

  private static void ConfigurePerspectiveRow<TModel>(ModelBuilder modelBuilder)
      where TModel : class {
    modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
      entity.HasKey(e => e.Id);

      // Use owned types for InMemory provider (InMemory doesn't support JSON queries)
      // The actual PostgreSQL implementation will use .ToJson() instead
      entity.OwnsOne(e => e.Data, data => {
        data.WithOwner();
      });

      entity.OwnsOne(e => e.Metadata, metadata => {
        metadata.WithOwner();
        metadata.Property(m => m.EventType).IsRequired();
        metadata.Property(m => m.EventId).IsRequired();
        metadata.Property(m => m.Timestamp).IsRequired();
      });

      entity.OwnsOne(e => e.Scope, scope => {
        scope.WithOwner();
      });
    });
  }
}
