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
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

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
}

/// <summary>
/// Test model for perspective store tests.
/// </summary>
public class StoreTestModel {
  public required string Name { get; init; }
  public required int Value { get; init; }
}
