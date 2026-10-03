namespace Whizbang.Core.Perspectives;

/// <summary>
/// Provides perspective rebuild operations in multiple modes.
/// Used internally by the migration system and available to developers for operational needs.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildInPlaceAsync_ReplaysAllStreamsAndUpdatesProjectionAndCursorsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildStreamsAsync_WithSubset_UpdatesOnlyTargetedStreamsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsCollectiveEvent_OnlyIntoTheTargetRowAsync</tests>
public interface IPerspectiveRebuilder {
  /// <summary>
  /// Blue-green rebuild: replays every stream into a shadow table while reads keep being served from the live
  /// table, catches the shadow up with streams written meanwhile, and swaps it in atomically. The previous table
  /// is kept or dropped as <see cref="BlueGreenRebuildOptions.KeepPreviousTable"/> says. Progress, phase included,
  /// is reported through <see cref="GetRebuildStatusAsync"/>. A driver with no <see cref="IPerspectiveTableSwapper"/>
  /// rebuilds in place.
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
    DateTimeOffset StartedAt) {
  /// <summary>
  /// What the rebuild is doing; <see cref="TotalStreams"/> and <see cref="ProcessedStreams"/> count the streams
  /// of this phase.
  /// </summary>
  public RebuildPhase Phase { get; init; } = RebuildPhase.Replaying;
}

/// <summary>A phase of a perspective rebuild.</summary>
public enum RebuildPhase {
  /// <summary>Replaying every stream.</summary>
  Replaying,
  /// <summary>Blue-green: replaying the streams written while the rebuild ran, into the shadow table.</summary>
  CatchingUp,
  /// <summary>Blue-green: catching up the last streams with the live table closed to writers, then swapping.</summary>
  Swapping
}

/// <summary>
/// Mode of a perspective rebuild operation.
/// </summary>
public enum RebuildMode {
  /// <summary>Replay into a shadow table while reads use the live one, then swap atomically.</summary>
  BlueGreen,
  /// <summary>Truncate active table, replay events in place.</summary>
  InPlace,
  /// <summary>Replay events for specific streams only.</summary>
  SelectedStreams
}
