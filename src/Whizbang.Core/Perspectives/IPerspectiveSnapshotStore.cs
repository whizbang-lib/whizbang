using System.Text.Json;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Stores and retrieves perspective snapshots for efficient rewind after late-arriving events.
/// Snapshots capture the full model state at a specific event, enabling replay from that point
/// instead of replaying from event zero.
/// </summary>
/// <docs>fundamentals/perspectives/snapshots</docs>
public interface IPerspectiveSnapshotStore {
  /// <summary>
  /// Creates a snapshot of the perspective model state at the given event.
  /// </summary>
  /// <param name="streamId">Stream the snapshot belongs to</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="snapshotEventId">Last event ID included in this snapshot</param>
  /// <param name="snapshotData">Serialized model state as JSON</param>
  /// <param name="ct">Cancellation token</param>
  Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default);

  /// <summary>
  /// Slice 26.11 — creates a snapshot with both the event_id and the commit_sequence anchor.
  /// Default implementation falls through to <see cref="CreateSnapshotAsync(Guid,string,Guid,JsonDocument,CancellationToken)"/>
  /// so legacy stores keep compiling; the commit-sequence-aware EFCore + Dapper implementations
  /// override to persist <c>snapshot_commit_sequence</c>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  Task CreateSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      Guid snapshotEventId,
      long? snapshotCommitSequence,
      JsonDocument snapshotData,
      CancellationToken ct = default) =>
    CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, snapshotData, ct);

  /// <summary>
  /// Gets the latest snapshot for a stream/perspective pair.
  /// Returns null if no snapshots exist.
  /// </summary>
  /// <param name="streamId">Stream to look up</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Tuple of (snapshotEventId, snapshotData) or null if no snapshots exist</returns>
  Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default);

  /// <summary>
  /// Slot-3 G7 — commit-sequence-aware variant of <see cref="GetLatestSnapshotAsync"/>.
  /// Returns the latest snapshot along with its stamped <c>snapshot_commit_sequence</c>.
  /// Mirrors the slice 26.11 pattern (kept original + added commit-sequence variant) for the
  /// anchored lookup. Default implementation falls through to the legacy method, surfacing
  /// null for <c>SnapshotCommitSequence</c> on stores that don't track it.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  async Task<(Guid SnapshotEventId, long? SnapshotCommitSequence, JsonDocument SnapshotData)?> GetLatestSnapshotWithCommitSequenceAsync(
      Guid streamId, string perspectiveName, CancellationToken ct = default) {
    var legacy = await GetLatestSnapshotAsync(streamId, perspectiveName, ct).ConfigureAwait(false);
    return legacy.HasValue ? (legacy.Value.SnapshotEventId, (long?)null, legacy.Value.SnapshotData) : null;
  }

  /// <summary>
  /// Gets the latest snapshot that was taken BEFORE the specified event ID.
  /// Used during rewind to find a safe restore point before the late event.
  /// Returns null if no qualifying snapshot exists.
  /// </summary>
  /// <param name="streamId">Stream to look up</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="beforeEventId">Find snapshot taken before this event (UUID7 comparison)</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Tuple of (snapshotEventId, snapshotData) or null if no qualifying snapshot exists</returns>
  Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default);

  /// <summary>
  /// Slice 26.11 — commit-sequence-anchored snapshot lookup. Returns the latest snapshot whose
  /// <c>snapshot_commit_sequence</c> is &lt; <paramref name="beforeCommitSequence"/>. Used by
  /// rewind when an inversion violator's commit_sequence triggers replay: the snapshot
  /// returned by this method represents model state strictly before the violator, so replay
  /// from there reproduces the live-apply order regardless of UUIDv7 generation timing.
  /// Default implementation returns null so legacy stores keep compiling.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  Task<(Guid SnapshotEventId, long? SnapshotCommitSequence, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeCommitSequenceAsync(
      Guid streamId, string perspectiveName, long beforeCommitSequence, CancellationToken ct = default) =>
    Task.FromResult<(Guid SnapshotEventId, long? SnapshotCommitSequence, JsonDocument SnapshotData)?>(null);

  /// <summary>
  /// Checks whether any snapshot exists for a stream/perspective pair.
  /// Cheap index-scan check used for bootstrap detection.
  /// </summary>
  /// <param name="streamId">Stream to check</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>True if at least one snapshot exists</returns>
  Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default);

  /// <summary>
  /// Deletes old snapshots, keeping only the most recent N per stream/perspective.
  /// </summary>
  /// <param name="streamId">Stream to prune</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="keepCount">Number of most recent snapshots to keep</param>
  /// <param name="ct">Cancellation token</param>
  Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default);

  /// <summary>
  /// Deletes all snapshots for a stream/perspective pair.
  /// Used during perspective rebuild to invalidate stale snapshots.
  /// </summary>
  /// <param name="streamId">Stream to clear</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="ct">Cancellation token</param>
  Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default);
}
