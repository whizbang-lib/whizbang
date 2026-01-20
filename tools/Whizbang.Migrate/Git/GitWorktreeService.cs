using System.Diagnostics;
using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Git;

/// <summary>
/// Service for managing git worktrees for isolated migration work.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class GitWorktreeService : IGitWorktreeService {
  /// <inheritdoc />
  public async Task<WorktreeResult> CreateWorktreeAsync(
      string repositoryPath,
      string? branchName = null,
      CancellationToken ct = default) {
    // Generate branch name if not provided
    var branch = branchName ?? $"whizbang-migrate/{Guid.NewGuid():N}";

    // Get base branch name (current HEAD)
    var baseBranch = await _runGitAsync(repositoryPath, "rev-parse --abbrev-ref HEAD", ct);
    baseBranch = baseBranch.Trim();

    // Create worktree directory path - use a predictable location in temp
    var worktreePath = Path.Combine(
        Path.GetTempPath(),
        $"whizbang-migrate-{Guid.NewGuid():N}");

    // Create the worktree with a new branch
    await _runGitAsync(repositoryPath, $"worktree add -b \"{branch}\" \"{worktreePath}\"", ct);

    // Resolve the actual path as git sees it (handles symlinks on macOS)
    var resolvedPath = await _resolveWorktreePathAsync(repositoryPath, branch, ct) ?? worktreePath;

    return new WorktreeResult(resolvedPath, branch, baseBranch);
  }

  /// <inheritdoc />
  public async Task RemoveWorktreeAsync(
      string worktreePath,
      bool deleteBranch = false,
      CancellationToken ct = default) {
    // Get the branch name before removing if we need to delete it
    string? branchName = null;
    if (deleteBranch) {
      try {
        var branchOutput = await _runGitAsync(worktreePath, "rev-parse --abbrev-ref HEAD", ct);
        branchName = branchOutput.Trim();
      } catch {
        // Ignore errors getting branch name
      }
    }

    // Find the main repository path from the worktree
    var repoPath = await _getMainRepoPathAsync(worktreePath, ct);

    // Remove the worktree
    await _runGitAsync(repoPath, $"worktree remove \"{worktreePath}\" --force", ct);

    // Delete the branch if requested
    if (deleteBranch && !string.IsNullOrEmpty(branchName)) {
      await _runGitAsync(repoPath, $"branch -D \"{branchName}\"", ct);
    }
  }

  /// <inheritdoc />
  public async Task<string> CreateCheckpointAsync(
      string worktreePath,
      string message,
      CancellationToken ct = default) {
    // Create the commit
    await _runGitAsync(worktreePath, $"commit -m \"{_escapeMessage(message)}\"", ct);

    // Get the commit SHA
    var sha = await _runGitAsync(worktreePath, "rev-parse HEAD", ct);
    return sha.Trim();
  }

  /// <inheritdoc />
  public async Task RollbackToCheckpointAsync(
      string worktreePath,
      string commitSha,
      CancellationToken ct = default) {
    await _runGitAsync(worktreePath, $"reset --hard {commitSha}", ct);
  }

  /// <inheritdoc />
  public async Task<string?> StashChangesAsync(
      string repositoryPath,
      CancellationToken ct = default) {
    // Check if there are changes to stash
    var hasChanges = await HasUncommittedChangesAsync(repositoryPath, ct);
    if (!hasChanges) {
      return null;
    }

    // Get current stash count
    var stashListBefore = await _runGitAsync(repositoryPath, "stash list", ct);
    var stashCountBefore = string.IsNullOrEmpty(stashListBefore.Trim())
        ? 0
        : stashListBefore.Trim().Split('\n').Length;

    // Create stash with untracked files
    await _runGitAsync(repositoryPath, "stash push --include-untracked", ct);

    // Return the stash reference
    return "stash@{0}";
  }

  /// <inheritdoc />
  public async Task ApplyStashAsync(
      string repositoryPath,
      string stashReference,
      CancellationToken ct = default) {
    await _runGitAsync(repositoryPath, $"stash apply {stashReference}", ct);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(
      string repositoryPath,
      CancellationToken ct = default) {
    var output = await _runGitAsync(repositoryPath, "worktree list --porcelain", ct);
    var worktrees = new List<WorktreeInfo>();

    // Parse porcelain output - each worktree block has: worktree, HEAD, branch (separated by blank lines)
    var lines = output.Split('\n');
    string? currentPath = null;
    string? currentBranch = null;

    foreach (var line in lines) {
      if (line.StartsWith("worktree ", StringComparison.Ordinal)) {
        // If we have a previous worktree entry, save it
        if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(currentBranch)) {
          worktrees.Add(new WorktreeInfo(currentPath, currentBranch));
        }

        currentPath = line["worktree ".Length..];
        currentBranch = null;
      } else if (line.StartsWith("branch ", StringComparison.Ordinal)) {
        currentBranch = line["branch ".Length..];
        // Remove refs/heads/ prefix if present
        if (currentBranch.StartsWith("refs/heads/", StringComparison.Ordinal)) {
          currentBranch = currentBranch["refs/heads/".Length..];
        }
      }
      // Ignore HEAD and other lines
    }

    // Handle last worktree entry
    if (!string.IsNullOrEmpty(currentPath) && !string.IsNullOrEmpty(currentBranch)) {
      worktrees.Add(new WorktreeInfo(currentPath, currentBranch));
    }

    return worktrees;
  }

  /// <inheritdoc />
  public async Task<bool> HasUncommittedChangesAsync(
      string repositoryPath,
      CancellationToken ct = default) {
    var output = await _runGitAsync(repositoryPath, "status --porcelain", ct);
    return !string.IsNullOrWhiteSpace(output);
  }

  private static async Task<string> _runGitAsync(
      string workingDirectory,
      string arguments,
      CancellationToken ct = default) {
    using var process = new Process();
    process.StartInfo = new ProcessStartInfo {
      FileName = "git",
      Arguments = arguments,
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    process.Start();

    // Read both streams
    var outputTask = process.StandardOutput.ReadToEndAsync(ct);
    var errorTask = process.StandardError.ReadToEndAsync(ct);

    await process.WaitForExitAsync(ct);

    if (process.ExitCode != 0) {
      var error = await errorTask;
      throw new InvalidOperationException($"Git command failed: git {arguments}\n{error}");
    }

    return await outputTask;
  }

  private static async Task<string> _getMainRepoPathAsync(
      string worktreePath,
      CancellationToken ct = default) {
    // Get the main repository path from a worktree
    var gitDir = await _runGitAsync(worktreePath, "rev-parse --git-common-dir", ct);
    gitDir = gitDir.Trim();

    // The git-common-dir returns the .git directory of the main repo
    if (gitDir.EndsWith(".git", StringComparison.Ordinal)) {
      return Path.GetDirectoryName(gitDir)!;
    }

    // If it's just .git (relative), use parent directory
    if (gitDir == ".git") {
      return worktreePath;
    }

    return Path.GetDirectoryName(gitDir) ?? worktreePath;
  }

  private static string _escapeMessage(string message) {
    return message.Replace("\"", "\\\"");
  }

  private async Task<string?> _resolveWorktreePathAsync(
      string repositoryPath,
      string branchName,
      CancellationToken ct = default) {
    // Get the worktree path as git sees it (resolves symlinks)
    var worktrees = await ListWorktreesAsync(repositoryPath, ct);
    var worktree = worktrees.FirstOrDefault(w =>
        w.Branch.Equals(branchName, StringComparison.Ordinal) ||
        w.Branch.Equals($"refs/heads/{branchName}", StringComparison.Ordinal));
    return worktree?.Path;
  }
}
