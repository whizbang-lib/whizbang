using Whizbang.Migrate.Core;

namespace Whizbang.Migrate.Journal;

/// <summary>
/// Interface for migration journal that tracks progress and enables idempotent resumption.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public interface IMigrationJournal {
  /// <summary>
  /// Gets the current journal status.
  /// </summary>
  JournalStatus Status { get; }

  /// <summary>
  /// Gets the worktree information if migration is in progress.
  /// </summary>
  WorktreeInfo? Worktree { get; }

  /// <summary>
  /// Gets all checkpoints recorded in the journal.
  /// </summary>
  IReadOnlyList<Checkpoint> Checkpoints { get; }

  /// <summary>
  /// Gets all transformations recorded in the journal.
  /// </summary>
  IReadOnlyList<TransformationRecord> Transformations { get; }

  /// <summary>
  /// Loads the journal from disk if it exists.
  /// </summary>
  Task LoadAsync(CancellationToken ct = default);

  /// <summary>
  /// Saves the journal to disk.
  /// </summary>
  Task SaveAsync(CancellationToken ct = default);

  /// <summary>
  /// Sets the worktree information.
  /// </summary>
  void SetWorktree(WorktreeInfo worktree);

  /// <summary>
  /// Adds a checkpoint to the journal.
  /// </summary>
  void AddCheckpoint(Checkpoint checkpoint);

  /// <summary>
  /// Records a transformation in the journal.
  /// </summary>
  void RecordTransformation(TransformationRecord transformation);

  /// <summary>
  /// Updates the status of a transformation.
  /// </summary>
  void UpdateTransformationStatus(string transformerName, TransformationStatus status);

  /// <summary>
  /// Marks the migration as complete.
  /// </summary>
  void MarkComplete();
}
