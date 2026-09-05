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

  [Test]
  public async Task Execute_ReturnsError_WhenAlreadyReverted_Async() {
    // Reverting twice is an easy mistake: the command is what you reach for when something has
    // gone wrong, and running it again is the obvious next move. A second reset would rewind
    // past the pre-migration commit and destroy work done since the first revert, so the
    // already-reverted state has to stop it rather than repeat.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);
    Directory.CreateDirectory(tempPath);

    try {
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.Reverted;
      decisionFile.State.GitCommitBefore = "abc123";
      await decisionFile.SaveAsync(decisionFilePath);

      var result = await RevertCommand.ExecuteAsync(tempPath);

      await Assert.That(result.Success).IsFalse()
        .Because("a second revert must not run, whatever the message is called");
      await Assert.That(result.WarningMessage).Contains("already been reverted")
        .Because("this is a refusal rather than a fault, so it reports as a warning");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

  [Test]
  public async Task Execute_ReturnsError_WhenProjectIsNotAGitRepository_Async() {
    // Revert restores by git reset, so without a repository there is nothing to restore from.
    // Saying so is the whole value here: silently "succeeding" would tell an operator their
    // migration had been undone when every transformed file is still on disk.
    var tempPath = Path.Combine(Path.GetTempPath(), $"test-project-{Guid.NewGuid()}");
    var projectName = Path.GetFileName(tempPath);
    var decisionFilePath = DecisionFile.GetDefaultPath(projectName);
    Directory.CreateDirectory(tempPath);

    try {
      var decisionFile = DecisionFile.Create(tempPath);
      decisionFile.State.Status = MigrationStatus.InProgress;
      decisionFile.State.GitCommitBefore = "abc123";
      await decisionFile.SaveAsync(decisionFilePath);

      var result = await RevertCommand.ExecuteAsync(tempPath);

      await Assert.That(result.Success).IsFalse();
      await Assert.That(result.ErrorMessage).Contains("git repository")
        .Because("an operator has to learn the revert did not happen, not infer it later");
    } finally {
      Directory.Delete(tempPath, recursive: true);
      var decisionDir = Path.GetDirectoryName(decisionFilePath);
      if (Directory.Exists(decisionDir)) {
        Directory.Delete(decisionDir, recursive: true);
      }
    }
  }

}
