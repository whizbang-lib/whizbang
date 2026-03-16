namespace Whizbang.Core.Perspectives;

/// <summary>
/// Provides perspective rebuild operations in multiple modes.
/// Used internally by the migration system and available to developers for operational needs.
/// </summary>
/// <docs>core-concepts/perspectives#rebuild</docs>
public interface IPerspectiveRebuilder {
  /// <summary>
  /// Blue-green rebuild: create new table, replay all events, swap when complete.
  /// Old table kept as backup. App continues serving reads from old table during rebuild.
  /// </summary>
  Task<RebuildResult> RebuildBlueGreenAsync(
      string perspectiveName, CancellationToken ct = default);

  /// <summary>
  /// In-place rebuild: truncate the active table and replay all events.
  /// Faster but causes temporary data loss during replay.
  /// </summary>
  Task<RebuildResult> RebuildInPlaceAsync(
      string perspectiveName, CancellationToken ct = default);

  /// <summary>
  /// Rebuild specific streams: replay events only for the given stream IDs.
  /// Useful for fixing individual corrupted/stale projections.
  /// </summary>
  Task<RebuildResult> RebuildStreamsAsync(
      string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default);

  /// <summary>
  /// Get status of an in-progress rebuild.
  /// </summary>
  Task<RebuildStatus?> GetRebuildStatusAsync(
      string perspectiveName, CancellationToken ct = default);
}

/// <summary>
/// Result of a perspective rebuild operation.
/// </summary>
public record RebuildResult(
    string PerspectiveName,
    int StreamsProcessed,
    int EventsReplayed,
    TimeSpan Duration,
    bool Success,
    string? Error);

/// <summary>
/// Status of an in-progress rebuild operation.
/// </summary>
public record RebuildStatus(
    string PerspectiveName,
    RebuildMode Mode,
    int TotalStreams,
    int ProcessedStreams,
    DateTimeOffset StartedAt);

/// <summary>
/// Mode of a perspective rebuild operation.
/// </summary>
public enum RebuildMode {
  /// <summary>Create new table, replay events, atomic swap.</summary>
  BlueGreen,
  /// <summary>Truncate active table, replay events in place.</summary>
  InPlace,
  /// <summary>Replay events for specific streams only.</summary>
  SelectedStreams
}
