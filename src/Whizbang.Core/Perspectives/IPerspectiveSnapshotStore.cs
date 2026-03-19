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
  /// Gets the latest snapshot for a stream/perspective pair.
  /// Returns null if no snapshots exist.
  /// </summary>
  /// <param name="streamId">Stream to look up</param>
  /// <param name="perspectiveName">Perspective name</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Tuple of (snapshotEventId, snapshotData) or null if no snapshots exist</returns>
  Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default);

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
