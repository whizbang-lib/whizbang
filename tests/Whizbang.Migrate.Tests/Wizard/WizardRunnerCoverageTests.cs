using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Coverage for <see cref="WizardRunner"/> branches not exercised by
/// <see cref="WizardRunnerTests"/>: looking up a batch by category, saving without a
/// decision file, and the decision-lookup branches for missing/projection/unhandled categories.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/WizardRunner.cs:79,80,81,122,133,138,139</tests>
public class WizardRunnerCoverageTests {
  [Test]
  public async Task GetBatch_ReturnsMatchingBatch_WhenCategoryWasAdded_Async() {
    // If lookup-by-category silently returned the wrong batch (or the first one added,
    // regardless of category), a command that means to act on "Projections" could act on
    // "Handlers" instead, converting or skipping the wrong files.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]));
      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Projections, [
        new MigrationItem("p1.cs", "Projection1", MigrationItemType.Projection)
      ]));

      // Act
      var batch = runner.GetBatch(MigrationCategory.Projections);

      // Assert
      await Assert.That(batch).IsNotNull();
      await Assert.That(batch!.Category).IsEqualTo(MigrationCategory.Projections);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task GetBatch_ReturnsNull_WhenCategoryWasNeverAdded_Async() {
    // A caller that assumes GetBatch always returns something for a valid enum value would
    // null-reference deep in command logic instead of getting a clear "no such batch" signal.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      runner.AddBatch(CategoryBatch.Create(MigrationCategory.Handlers, [
        new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler)
      ]));

      // Act
      var batch = runner.GetBatch(MigrationCategory.Projections);

      // Assert
      await Assert.That(batch).IsNull();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task SaveDecisionFileAsync_ThrowsInvalidOperationException_WhenNoDecisionFileExists_Async() {
    // Without this guard, saving before generating or loading a decision file would either
    // NullReferenceException deep inside DecisionFile.SaveAsync, or (worse) write an empty
    // file that looks like a valid, deliberately-empty decision set on the next run.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var decisionFilePath = Path.Combine(Path.GetTempPath(), $"decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);

      // Act & Assert
      await Assert.That(async () => await runner.SaveDecisionFileAsync(decisionFilePath))
        .ThrowsExactly<InvalidOperationException>();
      await Assert.That(File.Exists(decisionFilePath)).IsFalse()
        .Because("a failed save must not leave a partial or empty decision file on disk");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(decisionFilePath)) {
        File.Delete(decisionFilePath);
      }
    }
  }

  [Test]
  public async Task GetDecisionForItem_ReturnsPrompt_WhenNoDecisionFileIsLoaded_Async() {
    // If this fell through to a hardcoded Convert or Skip instead of Prompt, a run started
    // before any decisions were made or loaded would silently transform (or silently skip)
    // every item instead of asking the developer what to do.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      var item = new MigrationItem("h1.cs", "Handler1", MigrationItemType.Handler);

      // Act
      var decision = runner.GetDecisionForItem(item, MigrationCategory.Handlers);

      // Assert
      await Assert.That(decision).IsEqualTo(DecisionChoice.Prompt);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task GetDecisionForItem_ReturnsProjectionOverride_WhenCategoryIsProjections_Async() {
    // This path was untested even though Handlers' equivalent was covered, so a regression
    // that stopped checking per-file projection overrides could silently convert a projection
    // the developer had explicitly chosen to skip.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.Decisions.Projections.Default = DecisionChoice.Convert;
      decisionFile.Decisions.Projections.Overrides["p1.cs"] = DecisionChoice.Skip;
      runner.SetDecisionFile(decisionFile);

      var overriddenItem = new MigrationItem("p1.cs", "Projection1", MigrationItemType.Projection);
      var defaultItem = new MigrationItem("p2.cs", "Projection2", MigrationItemType.Projection);

      // Act
      var overriddenDecision = runner.GetDecisionForItem(overriddenItem, MigrationCategory.Projections);
      var defaultDecision = runner.GetDecisionForItem(defaultItem, MigrationCategory.Projections);

      // Assert
      await Assert.That(overriddenDecision).IsEqualTo(DecisionChoice.Skip);
      await Assert.That(defaultDecision).IsEqualTo(DecisionChoice.Convert);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task GetDecisionForItem_ReturnsPrompt_ForCategoryWithNoDedicatedLookup_Async() {
    // Handlers and Projections have per-file override support; other categories don't. If the
    // fallback silently returned Convert instead of Prompt, an EventStore item would be
    // auto-converted with no way for a developer to have overridden that per file.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      var runner = WizardRunner.Create(tempPath);
      var decisionFile = DecisionFile.Create(tempPath);
      runner.SetDecisionFile(decisionFile);

      var item = new MigrationItem("es1.cs", "EventStoreOp1", MigrationItemType.EventStoreOperation);

      // Act
      var decision = runner.GetDecisionForItem(item, MigrationCategory.EventStore);

      // Assert
      await Assert.That(decision).IsEqualTo(DecisionChoice.Prompt);
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }
}
