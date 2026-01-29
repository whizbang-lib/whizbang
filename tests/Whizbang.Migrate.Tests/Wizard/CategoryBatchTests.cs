using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the CategoryBatch model that groups migration items by category.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/CategoryBatch.cs:*</tests>
public class CategoryBatchTests {
  [Test]
  public async Task Create_CreatesHandlersBatch_WithCorrectProperties_Async() {
    // Arrange
    var items = new List<MigrationItem> {
      new("src/Handlers/OrderHandler.cs", "OrderHandler", MigrationItemType.Handler),
      new("src/Handlers/CustomerHandler.cs", "CustomerHandler", MigrationItemType.Handler)
    };

    // Act
    var batch = CategoryBatch.Create(MigrationCategory.Handlers, items);

    // Assert
    await Assert.That(batch.Category).IsEqualTo(MigrationCategory.Handlers);
    await Assert.That(batch.DisplayName).IsEqualTo("Handlers");
    await Assert.That(batch.Items.Count).IsEqualTo(2);
    await Assert.That(batch.TotalCount).IsEqualTo(2);
    await Assert.That(batch.CompletedCount).IsEqualTo(0);
  }

  [Test]
  public async Task MarkItemComplete_IncrementsCompletedCount_Async() {
    // Arrange
    var items = new List<MigrationItem> {
      new("src/Handler1.cs", "Handler1", MigrationItemType.Handler),
      new("src/Handler2.cs", "Handler2", MigrationItemType.Handler)
    };
    var batch = CategoryBatch.Create(MigrationCategory.Handlers, items);

    // Act
    batch.MarkItemComplete(0);

    // Assert
    await Assert.That(batch.CompletedCount).IsEqualTo(1);
    await Assert.That(batch.Items[0].IsComplete).IsTrue();
    await Assert.That(batch.Items[1].IsComplete).IsFalse();
  }

  [Test]
  public async Task IsComplete_ReturnsTrue_WhenAllItemsComplete_Async() {
    // Arrange
    var items = new List<MigrationItem> {
      new("src/Handler1.cs", "Handler1", MigrationItemType.Handler),
      new("src/Handler2.cs", "Handler2", MigrationItemType.Handler)
    };
    var batch = CategoryBatch.Create(MigrationCategory.Handlers, items);

    // Act
    batch.MarkItemComplete(0);
    batch.MarkItemComplete(1);

    // Assert
    await Assert.That(batch.IsComplete).IsTrue();
  }

  [Test]
  public async Task GetNextIncompleteIndex_ReturnsFirstIncomplete_Async() {
    // Arrange
    var items = new List<MigrationItem> {
      new("src/Handler1.cs", "Handler1", MigrationItemType.Handler),
      new("src/Handler2.cs", "Handler2", MigrationItemType.Handler),
      new("src/Handler3.cs", "Handler3", MigrationItemType.Handler)
    };
    var batch = CategoryBatch.Create(MigrationCategory.Handlers, items);
    batch.MarkItemComplete(0);

    // Act
    var nextIndex = batch.GetNextIncompleteIndex();

    // Assert
    await Assert.That(nextIndex).IsEqualTo(1);
  }

  [Test]
  public async Task GetNextIncompleteIndex_ReturnsMinusOne_WhenAllComplete_Async() {
    // Arrange
    var items = new List<MigrationItem> {
      new("src/Handler1.cs", "Handler1", MigrationItemType.Handler)
    };
    var batch = CategoryBatch.Create(MigrationCategory.Handlers, items);
    batch.MarkItemComplete(0);

    // Act
    var nextIndex = batch.GetNextIncompleteIndex();

    // Assert
    await Assert.That(nextIndex).IsEqualTo(-1);
  }

  [Test]
  public async Task ProgressPercentage_ReturnsCorrectValue_Async() {
    // Arrange
    var items = new List<MigrationItem> {
      new("src/Handler1.cs", "Handler1", MigrationItemType.Handler),
      new("src/Handler2.cs", "Handler2", MigrationItemType.Handler),
      new("src/Handler3.cs", "Handler3", MigrationItemType.Handler),
      new("src/Handler4.cs", "Handler4", MigrationItemType.Handler)
    };
    var batch = CategoryBatch.Create(MigrationCategory.Handlers, items);
    batch.MarkItemComplete(0);
    batch.MarkItemComplete(1);

    // Act
    var percentage = batch.ProgressPercentage;

    // Assert
    await Assert.That(percentage).IsEqualTo(50);
  }

  [Test]
  public async Task CategoryDisplayNames_AreCorrect_Async() {
    // Act & Assert
    await Assert.That(CategoryBatch.Create(MigrationCategory.Handlers, []).DisplayName)
        .IsEqualTo("Handlers");
    await Assert.That(CategoryBatch.Create(MigrationCategory.Projections, []).DisplayName)
        .IsEqualTo("Projections");
    await Assert.That(CategoryBatch.Create(MigrationCategory.EventStore, []).DisplayName)
        .IsEqualTo("Event Store Operations");
    await Assert.That(CategoryBatch.Create(MigrationCategory.IdGeneration, []).DisplayName)
        .IsEqualTo("ID Generation");
    await Assert.That(CategoryBatch.Create(MigrationCategory.DiRegistration, []).DisplayName)
        .IsEqualTo("DI Registration");
  }
}
