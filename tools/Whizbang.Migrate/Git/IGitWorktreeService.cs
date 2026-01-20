using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Git;

/// <summary>
/// Service for managing git worktrees for isolated migration work.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public interface IGitWorktreeService {
  /// <summary>
  /// Creates a new worktree for migration work.
  /// </summary>
  /// <param name="repositoryPath">Path to the main repository.</param>
  /// <param name="branchName">Name for the new branch (optional, auto-generated if not provided).</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Information about the created worktree.</returns>
  Task<WorktreeResult> CreateWorktreeAsync(
      string repositoryPath,
      string? branchName = null,
      CancellationToken ct = default);

  /// <summary>
  /// Removes a worktree and optionally its branch.
  /// </summary>
  /// <param name="worktreePath">Path to the worktree to remove.</param>
  /// <param name="deleteBranch">Whether to also delete the branch.</param>
  /// <param name="ct">Cancellation token.</param>
  Task RemoveWorktreeAsync(
      string worktreePath,
      bool deleteBranch = false,
      CancellationToken ct = default);

  /// <summary>
  /// Creates a checkpoint commit in the worktree.
  /// </summary>
  /// <param name="worktreePath">Path to the worktree.</param>
  /// <param name="message">Commit message for the checkpoint.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The SHA of the checkpoint commit.</returns>
  Task<string> CreateCheckpointAsync(
      string worktreePath,
      string message,
      CancellationToken ct = default);

  /// <summary>
  /// Rolls back the worktree to a specific checkpoint.
  /// </summary>
  /// <param name="worktreePath">Path to the worktree.</param>
  /// <param name="commitSha">The commit SHA to roll back to.</param>
  /// <param name="ct">Cancellation token.</param>
  Task RollbackToCheckpointAsync(
      string worktreePath,
      string commitSha,
      CancellationToken ct = default);

  /// <summary>
  /// Stashes any uncommitted changes in the main repository.
  /// </summary>
  /// <param name="repositoryPath">Path to the repository.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The stash reference if changes were stashed, null otherwise.</returns>
  Task<string?> StashChangesAsync(
      string repositoryPath,
      CancellationToken ct = default);

  /// <summary>
  /// Applies a previously created stash.
  /// </summary>
  /// <param name="repositoryPath">Path to the repository.</param>
  /// <param name="stashReference">The stash reference to apply.</param>
  /// <param name="ct">Cancellation token.</param>
  Task ApplyStashAsync(
      string repositoryPath,
      string stashReference,
      CancellationToken ct = default);

  /// <summary>
  /// Lists all worktrees for a repository.
  /// </summary>
  /// <param name="repositoryPath">Path to the repository.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>List of worktree information.</returns>
  Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(
      string repositoryPath,
      CancellationToken ct = default);

  /// <summary>
  /// Checks if the repository has uncommitted changes.
  /// </summary>
  /// <param name="repositoryPath">Path to the repository.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>True if there are uncommitted changes.</returns>
  Task<bool> HasUncommittedChangesAsync(
      string repositoryPath,
      CancellationToken ct = default);
}
