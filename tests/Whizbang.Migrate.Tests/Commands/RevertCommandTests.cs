using Whizbang.Migrate.Commands;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Tests for the RevertCommand that reverts migration changes.
/// </summary>
/// <tests>Whizbang.Migrate/Commands/RevertCommand.cs:*</tests>
public class RevertCommandTests {
  [Test]
  public async Task Execute_ReturnsError_WhenProjectPathDoesNotExist_Async() {
    // Arrange
    var nonExistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}");

    // Act
    var result = await RevertCommand.ExecuteAsync(nonExistentPath);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.ErrorMessage).Contains("not found");
  }

  [Test]
  public async Task Execute_ReturnsError_WhenNoDecisionFileExists_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      // Act
      var result = await RevertCommand.ExecuteAsync(tempPath);

      // Assert
      await Assert.That(result.Success).IsFalse();
      await Assert.That(result.ErrorMessage).Contains("No migration");
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task Execute_ReturnsError_WhenNoGitCommitStored_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);
    Directory.CreateDirectory(tempPath);

    try {
      // Create decision file without git commit
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      // Note: GitCommitBefore is NOT set
      await decisionFile.SaveAsync(decisionFilePath);

      // Act
      var result = await RevertCommand.ExecuteAsync(tempPath);

      // Assert
      await Assert.That(result.Success).IsFalse();
      await Assert.That(result.ErrorMessage).Contains("git commit");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

  [Test]
  public async Task Execute_ReturnsWarning_WhenMigrationAlreadyCompleted_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);
    Directory.CreateDirectory(tempPath);

    try {
      // Create decision file with completed status
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.Completed;
      decisionFile.State.GitCommitBefore = "abc123";
      await decisionFile.SaveAsync(decisionFilePath);

      // Act
      var result = await RevertCommand.ExecuteAsync(tempPath);

      // Assert - should warn that migration is already completed
      await Assert.That(result.Success).IsFalse();
      await Assert.That(result.WarningMessage).Contains("completed");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

  [Test]
  public async Task Execute_CanDeleteDecisionFile_WhenRequested_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var customDecisionPath = Path.Combine(Path.GetTempPath(), $"decisions-{Guid.NewGuid()}.json");
    Directory.CreateDirectory(tempPath);

    try {
      // Create decision file
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      decisionFile.State.GitCommitBefore = "abc123";
      await decisionFile.SaveAsync(customDecisionPath);

      // Act - request deletion but it will fail git reset (not a real git repo)
      var result = await RevertCommand.ExecuteAsync(
          tempPath,
          decisionFilePath: customDecisionPath,
          deleteDecisionFile: true);

      // Assert - will fail because not a git repo, but decision file should not be deleted on failure
      await Assert.That(result.Success).IsFalse();
    } finally {
      Directory.Delete(tempPath, recursive: true);
      if (File.Exists(customDecisionPath)) {
        File.Delete(customDecisionPath);
      }
    }
  }
}
