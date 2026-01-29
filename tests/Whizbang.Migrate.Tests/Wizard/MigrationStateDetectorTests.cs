using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the MigrationStateDetector that determines the current migration state.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/MigrationStateDetector.cs:*</tests>
public class MigrationStateDetectorTests {
  [Test]
  public async Task DetectState_ReturnsNoMigration_WhenNoDecisionFileExists_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      // Act
      var state = await MigrationStateDetector.DetectStateAsync(tempPath);

      // Assert
      await Assert.That(state.HasMigrationInProgress).IsFalse();
      await Assert.That(state.DecisionFilePath).IsNull();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task DetectState_ReturnsInProgress_WhenDecisionFileExistsWithInProgressStatus_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);

    try {
      // Create decision file with in-progress status
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      decisionFile.State.StartedAt = DateTimeOffset.UtcNow.AddHours(-1);
      decisionFile.State.GitCommitBefore = "abc123";
      await decisionFile.SaveAsync(decisionFilePath);



      // Act
      var state = await MigrationStateDetector.DetectStateAsync(tempPath);

      // Assert
      await Assert.That(state.HasMigrationInProgress).IsTrue();
      await Assert.That(state.DecisionFilePath).IsEqualTo(decisionFilePath);
      await Assert.That(state.Status).IsEqualTo(MigrationStatus.InProgress);
    } finally {
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

  [Test]
  public async Task DetectState_ReturnsCompleted_WhenDecisionFileExistsWithCompletedStatus_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);

    try {
      // Create decision file with completed status
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.Completed;
      decisionFile.State.CompletedAt = DateTimeOffset.UtcNow;
      await decisionFile.SaveAsync(decisionFilePath);



      // Act
      var state = await MigrationStateDetector.DetectStateAsync(tempPath);

      // Assert
      await Assert.That(state.HasMigrationInProgress).IsFalse();
      await Assert.That(state.Status).IsEqualTo(MigrationStatus.Completed);
    } finally {
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

  [Test]
  public async Task DetectState_UsesCustomDecisionFilePath_WhenProvided_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var customDecisionPath = Path.Combine(Path.GetTempPath(), $"custom-decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      // Create decision file at custom path
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      await decisionFile.SaveAsync(customDecisionPath);



      // Act
      var state = await MigrationStateDetector.DetectStateAsync(tempPath, customDecisionPath);

      // Assert
      await Assert.That(state.HasMigrationInProgress).IsTrue();
      await Assert.That(state.DecisionFilePath).IsEqualTo(customDecisionPath);
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(customDecisionPath)) {
        File.Delete(customDecisionPath);
      }
    }
  }

  [Test]
  public async Task GetProgressSummary_ReturnsFormattedSummary_Async() {
    // Arrange
    var decisionFile = DecisionFile.Create("/src/MyProject");
    decisionFile.State.Status = MigrationStatus.InProgress;
    decisionFile.State.CompletedCategories.Add("handlers");
    decisionFile.State.CurrentCategory = "projections";
    decisionFile.State.CurrentItem = 5;

    // Act
    var summary = MigrationStateDetector.GetProgressSummary(decisionFile);

    // Assert
    await Assert.That(summary).Contains("handlers");
    await Assert.That(summary).Contains("projections");
    await Assert.That(summary).Contains("5");
  }
}
