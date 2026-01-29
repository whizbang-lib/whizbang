using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the WizardRunner that orchestrates the migration wizard flow.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/WizardRunner.cs:*</tests>
public class WizardRunnerTests {
  [Test]
  public async Task Create_CreatesWizardRunner_WithProjectPath_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      // Act
      var runner = WizardRunner.Create(tempPath);

      // Assert
      await Assert.That(runner.ProjectPath).IsEqualTo(tempPath);
      await Assert.That(runner.State).IsNotNull();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task State_InitializesWithNotStarted_WhenNoExistingDecisionFile_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      // Act
      var runner = WizardRunner.Create(tempPath);

      // Assert
      await Assert.That(runner.State.Status).IsEqualTo(MigrationStatus.NotStarted);
      await Assert.That(runner.State.HasMigrationInProgress).IsFalse();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task AddBatch_AddsCategoryBatchToRunner_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      var batch = CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]);

      // Act
      runner.AddBatch(batch);

      // Assert
      await Assert.That(runner.Batches.Count).IsEqualTo(1);
      await Assert.That(runner.Batches[0].Category).IsEqualTo(MigrationCategory.Handlers);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task GetCurrentBatch_ReturnsFirstIncompleteBatch_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      var handlersBatch = CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]);
      handlersBatch.MarkItemComplete(0); // Complete the handlers batch

      var projectionsBatch = CategoryBatch.Create(MigrationCategory.Projections, [
        new MigrationItem("p1.cs", "Projection1", MigrationItemType.Projection)
      ]);

      runner.AddBatch(handlersBatch);
      runner.AddBatch(projectionsBatch);

      // Act
      var currentBatch = runner.GetCurrentBatch();

      // Assert
      await Assert.That(currentBatch).IsNotNull();
      await Assert.That(currentBatch!.Category).IsEqualTo(MigrationCategory.Projections);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task GetCurrentBatch_ReturnsNull_WhenAllBatchesComplete_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      var batch = CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]);
      batch.MarkItemComplete(0);

      runner.AddBatch(batch);

      // Act
      var currentBatch = runner.GetCurrentBatch();

      // Assert
      await Assert.That(currentBatch).IsNull();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task TotalItems_ReturnsSumOfAllBatchItems_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler),
        new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler)
      ]));

      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Projections, [
        new MigrationItem("p1.cs", "Projection1", MigrationItemType.Projection)
      ]));

      // Act
      var total = runner.TotalItems;

      // Assert
      await Assert.That(total).IsEqualTo(3);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task CompletedItems_ReturnsSumOfCompletedItems_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      var batch1 = CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler),
        new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler)
      ]);
      batch1.MarkItemComplete(0);

      var batch2 = CategoryBatch.Create(MigrationCategory.Projections, [
        new MigrationItem("p1.cs", "Projection1", MigrationItemType.Projection)
      ]);
      batch2.MarkItemComplete(0);

      runner.AddBatch(batch1);
      runner.AddBatch(batch2);

      // Act
      var completed = runner.CompletedItems;

      // Assert
      await Assert.That(completed).IsEqualTo(2);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task IsComplete_ReturnsTrue_WhenAllBatchesComplete_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      var batch = CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]);
      batch.MarkItemComplete(0);

      runner.AddBatch(batch);

      // Act
      var isComplete = runner.IsComplete;

      // Assert
      await Assert.That(isComplete).IsTrue();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task ProgressPercentage_ReturnsCorrectPercentage_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      var batch = CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler),
        new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler),
        new MigrationItem("h3.cs", "Handler3", MigrationItemType.Handler),
        new MigrationItem("h4.cs", "Handler4", MigrationItemType.Handler)
      ]);
      batch.MarkItemComplete(0);
      batch.MarkItemComplete(1);

      runner.AddBatch(batch);

      // Act
      var percentage = runner.ProgressPercentage;

      // Assert
      await Assert.That(percentage).IsEqualTo(50);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task GenerateDecisionFile_CreatesFileWithBatchInfo_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler),
        new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler)
      ]));

      // Act
      var decisionFile = runner.GenerateDecisionFile();

      // Assert
      await Assert.That(decisionFile.ProjectPath).IsEqualTo(tempPath);
      await Assert.That(decisionFile.Version).IsEqualTo("1.0");
      await Assert.That(decisionFile.Decisions.Handlers.Default).IsEqualTo(DecisionChoice.Convert);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task LoadDecisionFile_AppliesDecisionsToBatches_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var decisionFilePath = Path.Combine(Path.GetTempPath(), $"decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      // Create a decision file with some choices
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.Decisions.Handlers.Overrides["h1.cs"] = DecisionChoice.Skip;
      await decisionFile.SaveAsync(decisionFilePath);

      // Create wizard runner and load decisions
      var runner = WizardRunner.Create(tempPath);
      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler),
        new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler)
      ]));

      // Act
      await runner.LoadDecisionFileAsync(decisionFilePath);

      // Assert
      await Assert.That(runner.DecisionFile).IsNotNull();
      await Assert.That(runner.DecisionFile!.Decisions.Handlers.Overrides["h1.cs"]).IsEqualTo(DecisionChoice.Skip);
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(decisionFilePath)) {
        File.Delete(decisionFilePath);
      }
    }
  }

  [Test]
  public async Task SaveDecisionFile_PersistsCurrentState_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var decisionFilePath = Path.Combine(Path.GetTempPath(), $"decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]));

      // Generate decision file and modify it
      var decisionFile = runner.GenerateDecisionFile();
      decisionFile.SetHandlerDecision("h1.cs", DecisionChoice.Skip);
      runner.SetDecisionFile(decisionFile);

      // Act
      await runner.SaveDecisionFileAsync(decisionFilePath);

      // Assert
      await Assert.That(File.Exists(decisionFilePath)).IsTrue();
      var loaded = await DecisionFile.LoadAsync(decisionFilePath);
      await Assert.That(loaded.Decisions.Handlers.Overrides["h1.cs"]).IsEqualTo(DecisionChoice.Skip);
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(decisionFilePath)) {
        File.Delete(decisionFilePath);
      }
    }
  }

  [Test]
  public async Task GetDecisionForItem_ReturnsOverrideIfExists_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.Decisions.Handlers.Default = DecisionChoice.Convert;
      decisionFile.Decisions.Handlers.Overrides["h1.cs"] = DecisionChoice.Skip;
      runner.SetDecisionFile(decisionFile);

      var item1 = new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler);
      var item2 = new MigrationItem("h2.cs", "Handler2", MigrationItemType.Handler);

      // Act
      var decision1 = runner.GetDecisionForItem(item1, MigrationCategory.Handlers);
      var decision2 = runner.GetDecisionForItem(item2, MigrationCategory.Handlers);

      // Assert
      await Assert.That(decision1).IsEqualTo(DecisionChoice.Skip);
      await Assert.That(decision2).IsEqualTo(DecisionChoice.Convert);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }
}
