namespace Whizbang.Migrate.Core;

/// <summary>
/// Information about a git worktree used for migration.
/// </summary>
/// <param name="Path">The filesystem path to the worktree.</param>
/// <param name="Branch">The branch name used for migration.</param>
public sealed record WorktreeInfo(string Path, string Branch);

/// <summary>
/// A checkpoint representing a recoverable point in the migration.
/// </summary>
/// <param name="Id">Unique identifier for the checkpoint.</param>
/// <param name="CommitSha">Git commit SHA at this checkpoint.</param>
/// <param name="Description">Human-readable description of the checkpoint.</param>
/// <param name="CreatedAt">When the checkpoint was created.</param>
public sealed record Checkpoint(
    string Id,
    string CommitSha,
    string Description,
    DateTimeOffset CreatedAt);

/// <summary>
/// Record of a transformation that was applied.
/// </summary>
/// <param name="TransformerName">Name of the transformer that was applied.</param>
/// <param name="Status">Current status of the transformation.</param>
/// <param name="Files">Files affected by the transformation.</param>
/// <param name="StartedAt">When the transformation started.</param>
/// <param name="CompletedAt">When the transformation completed (if applicable).</param>
public sealed record TransformationRecord(
    string TransformerName,
    TransformationStatus Status,
    IReadOnlyList<string> Files,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null);

/// <summary>
/// Journal status values.
/// </summary>
public enum JournalStatus {
  /// <summary>Migration not started.</summary>
  NotStarted,
  /// <summary>Migration in progress.</summary>
  InProgress,
  /// <summary>Migration completed successfully.</summary>
  Completed,
  /// <summary>Migration failed and requires attention.</summary>
  Failed
}

/// <summary>
/// Status of a transformation.
/// </summary>
public enum TransformationStatus {
  /// <summary>Transformation pending.</summary>
  Pending,
  /// <summary>Transformation in progress.</summary>
  InProgress,
  /// <summary>Transformation completed successfully.</summary>
  Completed,
  /// <summary>Transformation failed.</summary>
  Failed,
  /// <summary>Transformation was skipped.</summary>
  Skipped
}

/// <summary>
/// Result of creating a worktree.
/// </summary>
/// <param name="WorktreePath">Path to the created worktree.</param>
/// <param name="BranchName">Name of the branch in the worktree.</param>
/// <param name="BaseBranch">The branch the worktree was created from.</param>
public sealed record WorktreeResult(
    string WorktreePath,
    string BranchName,
    string BaseBranch);
