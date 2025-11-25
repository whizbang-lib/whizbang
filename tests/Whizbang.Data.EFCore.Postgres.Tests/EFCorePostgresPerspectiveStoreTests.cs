using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for EFCorePostgresPerspectiveStore implementation of IPerspectiveStore.
/// These tests use EF Core InMemory provider for fast, isolated testing.
/// </summary>
public class EFCorePostgresPerspectiveStoreTests {

  private TestDbContext CreateInMemoryDbContext() {
    var options = new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
        .Options;

    return new TestDbContext(options);
  }

  [Test]
  public async Task UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");
    var model = new StoreTestModel { Name = "Alice", Value = 100 };

    // Act
    await store.UpsertAsync("test-id", model);

    // Assert - verify record was created
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Alice");
    await Assert.That(row.Data.Value).IsEqualTo(100);
    await Assert.That(row.Version).IsEqualTo(1);
  }

  [Test]
  public async Task UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Create initial record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Alice", Value = 100 });

    // Act - update the record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Bob", Value = 200 });

    // Assert - verify record was updated
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Bob");
    await Assert.That(row.Data.Value).IsEqualTo(200);
    await Assert.That(row.Version).IsEqualTo(2); // Version incremented
  }

  [Test]
  public async Task UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Act - multiple updates
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "V1", Value = 1 });
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "V2", Value = 2 });
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "V3", Value = 3 });

    // Assert - version should be 3
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(3);
    await Assert.That(row.Data.Name).IsEqualTo("V3");
  }

  [Test]
  public async Task UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Create initial record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Alice", Value = 100 });

    var firstRow = await context.Set<PerspectiveRow<StoreTestModel>>()
        .AsNoTracking()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    var originalUpdatedAt = firstRow!.UpdatedAt;

    // Small delay to ensure timestamp difference
    await Task.Delay(10);

    // Act - update the record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Bob", Value = 200 });

    // Assert - UpdatedAt should be newer
    var updatedRow = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(updatedRow!.UpdatedAt).IsGreaterThan(originalUpdatedAt);
    await Assert.That(updatedRow.CreatedAt).IsEqualTo(firstRow.CreatedAt); // CreatedAt unchanged
  }

  [Test]
  public async Task UpdateFieldsAsync_UpdatesSpecificFields_InDataColumnAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Create initial record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Alice", Value = 100 });

    // Act - update only the Value field
    await store.UpdateFieldsAsync("test-id", new Dictionary<string, object> {
      ["Value"] = 999
    });

    // Assert - Value updated, Name unchanged
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Alice"); // Unchanged
    await Assert.That(row.Data.Value).IsEqualTo(999); // Updated
  }

  [Test]
  public async Task UpdateFieldsAsync_IncrementsVersionNumberAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Create initial record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Alice", Value = 100 });

    // Act - update a field
    await store.UpdateFieldsAsync("test-id", new Dictionary<string, object> {
      ["Value"] = 200
    });

    // Assert - version incremented
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Version).IsEqualTo(2);
  }

  [Test]
  public async Task UpdateFieldsAsync_WhenRecordDoesNotExist_ThrowsExceptionAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Act & Assert - should throw when record doesn't exist
    var exception = await Assert.That(async () =>
        await store.UpdateFieldsAsync("nonexistent-id", new Dictionary<string, object> {
          ["Value"] = 999
        })
    ).ThrowsException();

    await Assert.That(exception is InvalidOperationException).IsTrue();
  }

  [Test]
  public async Task UpdateFieldsAsync_UpdatesMultipleFields_InSingleOperationAsync() {
    // Arrange
    var context = CreateInMemoryDbContext();
    var store = new EFCorePostgresPerspectiveStore<StoreTestModel>(context, "test_perspective");

    // Create initial record
    await store.UpsertAsync("test-id", new StoreTestModel { Name = "Alice", Value = 100 });

    // Act - update multiple fields
    await store.UpdateFieldsAsync("test-id", new Dictionary<string, object> {
      ["Name"] = "Bob",
      ["Value"] = 999
    });

    // Assert - both fields updated
    var row = await context.Set<PerspectiveRow<StoreTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == "test-id");

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Bob");
    await Assert.That(row!.Data.Value).IsEqualTo(999);
  }
}

/// <summary>
/// Test model for perspective store tests.
/// </summary>
public class StoreTestModel {
  public required string Name { get; init; }
  public required int Value { get; init; }
}
