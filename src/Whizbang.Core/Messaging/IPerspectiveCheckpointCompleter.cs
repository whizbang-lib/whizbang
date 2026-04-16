namespace Whizbang.Core.Messaging;

/// <summary>
/// Persists perspective cursor checkpoints produced by a runner without going through the
/// full <see cref="IWorkCoordinator.ProcessWorkBatchAsync"/> pipeline. Used by
/// <see cref="Perspectives.PerspectiveRebuilder"/> so rebuild operations leave
/// <c>wh_perspective_cursors</c> consistent with the replayed event log.
/// </summary>
/// <remarks>
/// The live processing path (<see cref="Workers.PerspectiveWorker"/>) continues to persist
/// cursor completions via <c>ProcessWorkBatchAsync</c>. This interface exists so callers
/// that only need the cursor-persistence primitive (e.g., rebuild) don't have to pay for
/// event claiming, lease management, or transport concerns.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
public interface IPerspectiveCheckpointCompleter {
  /// <summary>
  /// Persists the supplied cursor completions. Upserts the <c>wh_perspective_cursors</c>
  /// row for each <c>(StreamId, PerspectiveName)</c> pair: sets <c>last_event_id</c>,
  /// <c>status</c>, and <c>processed_at</c>; clears any lingering rewind flags. Rows
  /// that don't yet exist are inserted.
  /// </summary>
  /// <param name="completions">Completions to persist. Empty list is a no-op.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task CompleteAsync(
      IReadOnlyList<PerspectiveCursorCompletion> completions,
      CancellationToken cancellationToken = default);
}
