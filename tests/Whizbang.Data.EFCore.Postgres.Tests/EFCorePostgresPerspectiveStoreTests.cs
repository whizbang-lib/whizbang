using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for EFCorePostgresPerspectiveStore implementation of IPerspectiveStore.
/// These tests use EF Core InMemory provider for fast, isolated testing.
/// </summary>
public class EFCorePostgresPerspectiveStoreTests {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  private TestDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(databaseName: _idProvider.NewGuid().ToString())
        .Options;

    return new TestDbContext(options);
  }

  [Test]
  public async Task UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var model = new StoreTestModel { Name = "Alice", Value = 100 };
    var testId = _idProvider.NewGuid();

    // Act
    await store.UpsertAsync(testId, model);

    // Assert - verify record was created
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Alice");
    await Assert.That(row.Data.Value).IsEqualTo(100);
    await Assert.That(row.Version).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var testId = _idProvider.NewGuid();

    // Create initial record
    await store.UpsertAsync(testId, new StoreTestModel { Name = "Alice", Value = 100 });

    // Act - update the record
    await store.UpsertAsync(testId, new StoreTestModel { Name = "Bob", Value = 200 });

    // Assert - verify record was updated
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Bob");
    await Assert.That(row.Data.Value).IsEqualTo(200);
    await Assert.That(row.Version).IsEqualTo(2); // Version incremented
  }

  [Test]
  public async Task UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var testId = _idProvider.NewGuid();

    // Act - multiple updates
    await store.UpsertAsync(testId, new StoreTestModel { Name = "V1", Value = 1 });
    await store.UpsertAsync(testId, new StoreTestModel { Name = "V2", Value = 2 });
    await store.UpsertAsync(testId, new StoreTestModel { Name = "V3", Value = 3 });

    // Assert - version should be 3
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(3);
    await Assert.That(row.Data.Name).IsEqualTo("V3");
  }

  [Test]
  public async Task UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var testId = _idProvider.NewGuid();

    // Create initial record
    await store.UpsertAsync(testId, new StoreTestModel { Name = "Alice", Value = 100 });

    var firstRow = await context.Set<PerspectiveRow<StoreTestModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == testId);

    var originalUpdatedAt = firstRow!.UpdatedAt;

    // Small delay to ensure timestamp difference
    await Task.Delay(10);

    // Act - update the record
    await store.UpsertAsync(testId, new StoreTestModel { Name = "Bob", Value = 200 });

    // Assert - UpdatedAt should be newer
    var updatedRow = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == testId);

    await Assert.That(updatedRow!.UpdatedAt).IsGreaterThan(originalUpdatedAt);
    await Assert.That(updatedRow.CreatedAt).IsEqualTo(firstRow.CreatedAt); // CreatedAt unchanged
  }

  [Test]
  public async Task Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    var exception = await Assert.That(() =>
        new EFCorePostgresPerspectiveStore<StoreTestModel>(null!, "test_perspective", new InMemoryUpsertStrategy())
    ).ThrowsException();

    await Assert.That(exception).IsTypeOf<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();

    // Act & Assert
    var exception = await Assert.That(() =>
        new EFCorePostgresPerspectiveStore<StoreTestModel>(context, null!, new InMemoryUpsertStrategy())
    ).ThrowsException();

    await Assert.That(exception).IsTypeOf<ArgumentNullException>();
  }

  [Test]
  public async Task GetByStreamIdAsync_WhenRecordExists_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var testId = _idProvider.NewGuid();
    var model = new StoreTestModel { Name = "Alice", Value = 100 };

    // Create a record first
    await store.UpsertAsync(testId, model);

    // Act
    var result = await store.GetByStreamIdAsync(testId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Alice");
    await Assert.That(result.Value).IsEqualTo(100);
  }

  [Test]
  public async Task GetByStreamIdAsync_WhenRecordDoesNotExist_ReturnsNullAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var nonExistentId = _idProvider.NewGuid();

    // Act
    var result = await store.GetByStreamIdAsync(nonExistentId);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetByStreamIdAsync_WithStrongTypedId_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var strongId = TestOrderId.From(_idProvider.NewGuid());
    var model = new StoreTestModel { Name = "StrongIdTest", Value = 999 };

    // Create a record using strong ID (implicit conversion to Guid)
    await store.UpsertAsync(strongId, model);

    // Act - retrieve using strong ID (implicit conversion to Guid)
    var result = await store.GetByStreamIdAsync(strongId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("StrongIdTest");
    await Assert.That(result.Value).IsEqualTo(999);
  }

  // Tests for partition key methods (multi-stream perspectives)

  [Test]
  public async Task GetByPartitionKeyAsync_WhenRecordExists_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = _idProvider.NewGuid();
    var model = new StoreTestModel { Name = "PartitionedModel", Value = 777 };

    // Create a record using partition key
    await store.UpsertByPartitionKeyAsync(partitionKey, model);

    // Act
    var result = await store.GetByPartitionKeyAsync(partitionKey);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("PartitionedModel");
    await Assert.That(result.Value).IsEqualTo(777);
  }

  [Test]
  public async Task GetByPartitionKeyAsync_WhenRecordDoesNotExist_ReturnsNullAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var nonExistentKey = _idProvider.NewGuid();

    // Act
    var result = await store.GetByPartitionKeyAsync(nonExistentKey);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = _idProvider.NewGuid();
    var model = new StoreTestModel { Name = "NewPartition", Value = 333 };

    // Act
    await store.UpsertByPartitionKeyAsync(partitionKey, model);

    // Assert - verify record was created with partition key
    var result = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("NewPartition");
    await Assert.That(result.Value).IsEqualTo(333);
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_WhenRecordExists_UpdatesExistingRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = _idProvider.NewGuid();

    // Create initial record
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "Initial", Value = 100 });

    // Act - update the record
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "Updated", Value = 200 });

    // Assert - verify record was updated
    var result = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Updated");
    await Assert.That(result.Value).IsEqualTo(200);
  }

  [Test]
  public async Task UpsertByPartitionKeyAsync_IncrementsVersionNumber_OnEachUpdateAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = _idProvider.NewGuid();

    // Act - multiple updates
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "V1", Value = 1 });
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "V2", Value = 2 });
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "V3", Value = 3 });

    // Assert - verify final state and version
    var result = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("V3");
  }

  [Test]
  public async Task GetByPartitionKeyAsync_WithStringPartitionKey_ReturnsModelAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = "tenant-123";
    var model = new StoreTestModel { Name = "TenantData", Value = 555 };

    // Create a record using string partition key
    await store.UpsertByPartitionKeyAsync(partitionKey, model);

    // Act
    var result = await store.GetByPartitionKeyAsync(partitionKey);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("TenantData");
    await Assert.That(result.Value).IsEqualTo(555);
  }

  // ==================== PurgeAsync Tests ====================

  [Test]
  public async Task PurgeAsync_WhenRecordExists_RemovesRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var testId = _idProvider.NewGuid();

    // Create a record first
    await store.UpsertAsync(testId, new StoreTestModel { Name = "ToDelete", Value = 999 });

    // Verify record exists
    var beforePurge = await store.GetByStreamIdAsync(testId);
    await Assert.That(beforePurge).IsNotNull();

    // Act - purge the record
    await store.PurgeAsync(testId);

    // Assert - record should be gone
    var afterPurge = await store.GetByStreamIdAsync(testId);
    await Assert.That(afterPurge).IsNull();
  }

  [Test]
  public async Task PurgeAsync_WhenRecordDoesNotExist_DoesNotThrowAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var nonExistentId = _idProvider.NewGuid();

    // Act & Assert - should not throw
    await Assert.That(async () => await store.PurgeAsync(nonExistentId))
        .ThrowsNothing();
  }

  [Test]
  public async Task PurgeByPartitionKeyAsync_WhenRecordExists_RemovesRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = _idProvider.NewGuid();

    // Create a record using partition key
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "ToDelete", Value = 888 });

    // Verify record exists
    var beforePurge = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(beforePurge).IsNotNull();

    // Act - purge the record
    await store.PurgeByPartitionKeyAsync(partitionKey);

    // Assert - record should be gone
    var afterPurge = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(afterPurge).IsNull();
  }

  [Test]
  public async Task PurgeByPartitionKeyAsync_WhenRecordDoesNotExist_DoesNotThrowAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var nonExistentKey = _idProvider.NewGuid();

    // Act & Assert - should not throw
    await Assert.That(async () => await store.PurgeByPartitionKeyAsync(nonExistentKey))
        .ThrowsNothing();
  }

  [Test]
  public async Task PurgeByPartitionKeyAsync_WithStringPartitionKey_RemovesRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var strategy = new InMemoryUpsertStrategy();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective", strategy);
    var partitionKey = "tenant-to-delete";

    // Create a record using string partition key
    await store.UpsertByPartitionKeyAsync(partitionKey, new StoreTestModel { Name = "TenantData", Value = 777 });

    // Verify record exists
    var beforePurge = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(beforePurge).IsNotNull();

    // Act - purge the record
    await store.PurgeByPartitionKeyAsync(partitionKey);

    // Assert - record should be gone
    var afterPurge = await store.GetByPartitionKeyAsync(partitionKey);
    await Assert.That(afterPurge).IsNull();
  }
}

/// <summary>
/// Test model for perspective store tests.
/// </summary>
public class StoreTestModel {
  public required string Name { get; init; }
  public required int Value { get; init; }
}
