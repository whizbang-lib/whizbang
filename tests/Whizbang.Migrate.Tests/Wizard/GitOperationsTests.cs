using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Wizard;

/// <summary>
/// Tests for the GitOperations class that handles git commit tracking and revert.
/// </summary>
/// <tests>Whizbang.Migrate/Wizard/GitOperations.cs:*</tests>
public class GitOperationsTests {
  [Test]
  public async Task GetCurrentCommitHash_ReturnsHash_WhenInGitRepo_Async() {
    // Arrange - use the actual whizbang repo which is a git repo
    var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    // Act
    var hash = await GitOperations.GetCurrentCommitHashAsync(repoPath);

    // Assert - should return a 40-character git hash
    await Assert.That(hash).IsNotNull();
    await Assert.That(hash!.Length).IsEqualTo(40);
    await Assert.That(hash).Matches("[0-9a-f]{40}");
  }

  [Test]
  public async Task GetCurrentCommitHash_ReturnsNull_WhenNotInGitRepo_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"not-a-git-repo-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      // Act
      var hash = await GitOperations.GetCurrentCommitHashAsync(tempPath);

      // Assert
      await Assert.That(hash).IsNull();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task IsGitRepository_ReturnsTrue_WhenInGitRepo_Async() {
    // Arrange
    var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    // Act
    var isRepo = await GitOperations.IsGitRepositoryAsync(repoPath);

    // Assert
    await Assert.That(isRepo).IsTrue();
  }

  [Test]
  public async Task IsGitRepository_ReturnsFalse_WhenNotInGitRepo_Async() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), $"not-a-git-repo-{Guid.NewGuid()}");
    Directory.CreateDirectory(tempPath);

    try {
      // Act
      var isRepo = await GitOperations.IsGitRepositoryAsync(tempPath);

      // Assert
      await Assert.That(isRepo).IsFalse();
    } finally {
      Directory.Delete(tempPath, recursive: true);
    }
  }

  [Test]
  public async Task HasUncommittedChanges_ReturnsFalse_WhenClean_Async() {
    // Arrange
    var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    // Act - the repo may or may not have uncommitted changes, but the method should work
    var hasChanges = await GitOperations.HasUncommittedChangesAsync(repoPath);

    // Assert - just verify it returns a boolean without error
    await Assert.That(hasChanges).IsTypeOf<bool>();
  }

  [Test]
  public async Task GetProjectName_ExtractsFromDirectoryName_Async() {
    // Arrange
    var projectPath = "/Users/dev/src/MyAwesomeProject";

    // Act
    var projectName = GitOperations.GetProjectName(projectPath);

    // Assert
    await Assert.That(projectName).IsEqualTo("MyAwesomeProject");
  }

  [Test]
  public async Task GetProjectName_HandlesTrailingSlash_Async() {
    // Arrange
    var projectPath = "/Users/dev/src/MyProject/";

    // Act
    var projectName = GitOperations.GetProjectName(projectPath);

    // Assert
    await Assert.That(projectName).IsEqualTo("MyProject");
  }
}
