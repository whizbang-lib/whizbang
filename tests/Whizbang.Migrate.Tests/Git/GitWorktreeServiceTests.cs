using Whizbang.Migrate.Core;
using Whizbang.Migrate.Git;

namespace Whizbang.Migrate.Tests.Git;

/// <summary>
/// Tests for the git worktree service that manages isolated migration environments.
/// These are integration tests that create real git repositories.
/// </summary>
/// <tests>Whizbang.Migrate/Git/GitWorktreeService.cs:*</tests>
[Category("Integration")]
public class GitWorktreeServiceTests {
  private string _tempDirectory = null!;
  private string _repoPath = null!;

  [Before(Test)]
  public async Task SetUpAsync() {
    _tempDirectory = Path.Combine(Path.GetTempPath(), $"whizbang-git-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDirectory);

    // Create a git repository for testing
    _repoPath = Path.Combine(_tempDirectory, "test-repo");
    Directory.CreateDirectory(_repoPath);

    await _runGitAsync(_repoPath, "init");
    await _runGitAsync(_repoPath, "config user.email \"test@test.com\"");
    await _runGitAsync(_repoPath, "config user.name \"Test User\"");

    // Create initial commit so we have a valid HEAD
    var testFile = Path.Combine(_repoPath, "README.md");
    await File.WriteAllTextAsync(testFile, "# Test Repository\n");
    await _runGitAsync(_repoPath, "add .");
    await _runGitAsync(_repoPath, "commit -m \"Initial commit\"");
  }

  [After(Test)]
  public void TearDown() {
    if (Directory.Exists(_tempDirectory)) {
      // Force delete git directories which may have read-only files
      _forceDeleteDirectory(_tempDirectory);
    }
  }

  [Test]
  public async Task CreateWorktreeAsync_CreatesNewWorktree_Async() {
    // Arrange
    var service = new GitWorktreeService();

    // Act
    var result = await service.CreateWorktreeAsync(_repoPath);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(Directory.Exists(result.WorktreePath)).IsTrue();
    await Assert.That(result.BranchName).IsNotEmpty();
  }

  [Test]
  public async Task CreateWorktreeAsync_WithCustomBranchName_UsesBranchName_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var branchName = "whizbang-migrate/custom-branch";

    // Act
    var result = await service.CreateWorktreeAsync(_repoPath, branchName);

    // Assert
    await Assert.That(result.BranchName).IsEqualTo(branchName);
  }

  [Test]
  public async Task CreateWorktreeAsync_WorktreeContainsRepoFiles_Async() {
    // Arrange
    var service = new GitWorktreeService();

    // Act
    var result = await service.CreateWorktreeAsync(_repoPath);

    // Assert - worktree should contain the README from the repo
    var readmePath = Path.Combine(result.WorktreePath, "README.md");
    await Assert.That(File.Exists(readmePath)).IsTrue();
  }

  [Test]
  public async Task RemoveWorktreeAsync_RemovesWorktreeDirectory_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree = await service.CreateWorktreeAsync(_repoPath);

    // Act
    await service.RemoveWorktreeAsync(worktree.WorktreePath);

    // Assert
    await Assert.That(Directory.Exists(worktree.WorktreePath)).IsFalse();
  }

  [Test]
  public async Task RemoveWorktreeAsync_WithDeleteBranch_DeletesBranch_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree = await service.CreateWorktreeAsync(_repoPath, "whizbang-migrate/to-delete");

    // Act
    await service.RemoveWorktreeAsync(worktree.WorktreePath, deleteBranch: true);

    // Assert - branch should no longer exist
    var branchExists = await _branchExistsAsync(_repoPath, "whizbang-migrate/to-delete");
    await Assert.That(branchExists).IsFalse();
  }

  [Test]
  public async Task CreateCheckpointAsync_CreatesCommit_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree = await service.CreateWorktreeAsync(_repoPath);

    // Make a change in the worktree
    var newFile = Path.Combine(worktree.WorktreePath, "new-file.txt");
    await File.WriteAllTextAsync(newFile, "New content");
    await _runGitAsync(worktree.WorktreePath, "add .");

    // Act
    var commitSha = await service.CreateCheckpointAsync(worktree.WorktreePath, "Test checkpoint");

    // Assert
    await Assert.That(commitSha).IsNotEmpty();
    await Assert.That(commitSha.Length).IsGreaterThanOrEqualTo(7); // Short SHA at minimum
  }

  [Test]
  public async Task CreateCheckpointAsync_CommitMessageContainsDescription_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree = await service.CreateWorktreeAsync(_repoPath);

    // Make a change
    var newFile = Path.Combine(worktree.WorktreePath, "checkpoint-test.txt");
    await File.WriteAllTextAsync(newFile, "Checkpoint content");
    await _runGitAsync(worktree.WorktreePath, "add .");

    // Act
    var commitSha = await service.CreateCheckpointAsync(worktree.WorktreePath, "Migration checkpoint: Handler conversion");

    // Assert - verify commit message
    var commitMessage = await _getCommitMessageAsync(worktree.WorktreePath, commitSha);
    await Assert.That(commitMessage).Contains("Migration checkpoint: Handler conversion");
  }

  [Test]
  public async Task RollbackToCheckpointAsync_RestoresFileState_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree = await service.CreateWorktreeAsync(_repoPath);

    // Create first checkpoint
    var file1 = Path.Combine(worktree.WorktreePath, "file1.txt");
    await File.WriteAllTextAsync(file1, "Version 1");
    await _runGitAsync(worktree.WorktreePath, "add .");
    var checkpoint1 = await service.CreateCheckpointAsync(worktree.WorktreePath, "Checkpoint 1");

    // Create second checkpoint with different content
    await File.WriteAllTextAsync(file1, "Version 2");
    await _runGitAsync(worktree.WorktreePath, "add .");
    await service.CreateCheckpointAsync(worktree.WorktreePath, "Checkpoint 2");

    // Act - rollback to checkpoint 1
    await service.RollbackToCheckpointAsync(worktree.WorktreePath, checkpoint1);

    // Assert - file should have Version 1 content
    var content = await File.ReadAllTextAsync(file1);
    await Assert.That(content).IsEqualTo("Version 1");
  }

  [Test]
  public async Task HasUncommittedChangesAsync_NoChanges_ReturnsFalse_Async() {
    // Arrange
    var service = new GitWorktreeService();

    // Act
    var hasChanges = await service.HasUncommittedChangesAsync(_repoPath);

    // Assert
    await Assert.That(hasChanges).IsFalse();
  }

  [Test]
  public async Task HasUncommittedChangesAsync_WithChanges_ReturnsTrue_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var newFile = Path.Combine(_repoPath, "uncommitted.txt");
    await File.WriteAllTextAsync(newFile, "Uncommitted content");

    // Act
    var hasChanges = await service.HasUncommittedChangesAsync(_repoPath);

    // Assert
    await Assert.That(hasChanges).IsTrue();
  }

  [Test]
  public async Task HasUncommittedChangesAsync_WithStagedChanges_ReturnsTrue_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var newFile = Path.Combine(_repoPath, "staged.txt");
    await File.WriteAllTextAsync(newFile, "Staged content");
    await _runGitAsync(_repoPath, "add .");

    // Act
    var hasChanges = await service.HasUncommittedChangesAsync(_repoPath);

    // Assert
    await Assert.That(hasChanges).IsTrue();
  }

  [Test]
  public async Task StashChangesAsync_StashesUncommittedChanges_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var newFile = Path.Combine(_repoPath, "to-stash.txt");
    await File.WriteAllTextAsync(newFile, "Content to stash");

    // Act
    var stashRef = await service.StashChangesAsync(_repoPath);

    // Assert
    await Assert.That(stashRef).IsNotNull();
    await Assert.That(File.Exists(newFile)).IsFalse(); // File should be gone after stash
  }

  [Test]
  public async Task StashChangesAsync_NoChanges_ReturnsNull_Async() {
    // Arrange
    var service = new GitWorktreeService();

    // Act
    var stashRef = await service.StashChangesAsync(_repoPath);

    // Assert
    await Assert.That(stashRef).IsNull();
  }

  [Test]
  public async Task ApplyStashAsync_RestoresStashedChanges_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var newFile = Path.Combine(_repoPath, "stash-restore.txt");
    await File.WriteAllTextAsync(newFile, "Content to restore");
    var stashRef = await service.StashChangesAsync(_repoPath);

    // Act
    await service.ApplyStashAsync(_repoPath, stashRef!);

    // Assert
    await Assert.That(File.Exists(newFile)).IsTrue();
    var content = await File.ReadAllTextAsync(newFile);
    await Assert.That(content).IsEqualTo("Content to restore");
  }

  [Test]
  public async Task ListWorktreesAsync_ReturnsAllWorktrees_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree1 = await service.CreateWorktreeAsync(_repoPath, "whizbang-migrate/wt1");
    var worktree2 = await service.CreateWorktreeAsync(_repoPath, "whizbang-migrate/wt2");

    // Act
    var worktrees = await service.ListWorktreesAsync(_repoPath);

    // Assert - should include main repo plus two worktrees
    await Assert.That(worktrees.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task ListWorktreesAsync_IncludesWorktreePaths_Async() {
    // Arrange
    var service = new GitWorktreeService();
    var worktree = await service.CreateWorktreeAsync(_repoPath, "whizbang-migrate/list-test");

    // Act
    var worktrees = await service.ListWorktreesAsync(_repoPath);

    // Assert
    var paths = worktrees.Select(w => w.Path).ToList();
    await Assert.That(paths).Contains(worktree.WorktreePath);
  }

  // Helper methods

  private static async Task _runGitAsync(string workingDirectory, string arguments) {
    using var process = new System.Diagnostics.Process();
    process.StartInfo = new System.Diagnostics.ProcessStartInfo {
      FileName = "git",
      Arguments = arguments,
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    process.Start();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0) {
      var error = await process.StandardError.ReadToEndAsync();
      throw new InvalidOperationException($"Git command failed: git {arguments}\n{error}");
    }
  }

  private static async Task<bool> _branchExistsAsync(string repoPath, string branchName) {
    try {
      await _runGitAsync(repoPath, $"rev-parse --verify {branchName}");
      return true;
    } catch {
      return false;
    }
  }

  private static async Task<string> _getCommitMessageAsync(string repoPath, string commitSha) {
    using var process = new System.Diagnostics.Process();
    process.StartInfo = new System.Diagnostics.ProcessStartInfo {
      FileName = "git",
      Arguments = $"log -1 --format=%B {commitSha}",
      WorkingDirectory = repoPath,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    return output.Trim();
  }

  private static void _forceDeleteDirectory(string path) {
    var directory = new DirectoryInfo(path);
    if (!directory.Exists) {
      return;
    }

    // Remove read-only attributes
    foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories)) {
      file.Attributes = FileAttributes.Normal;
    }

    foreach (var dir in directory.GetDirectories("*", SearchOption.AllDirectories)) {
      dir.Attributes = FileAttributes.Normal;
    }

    directory.Delete(true);
  }
}
