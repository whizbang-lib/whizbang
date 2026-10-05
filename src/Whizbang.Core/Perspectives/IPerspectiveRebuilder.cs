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
  // The RebuildOrigin overloads carry default bodies that forward to the origin-less ones. An implementation that
  // predates provenance keeps compiling and simply records none, which is the honest outcome: it cannot know who
  // asked. PerspectiveRebuilder overrides all three and does record it.

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
  /// Blue-green rebuild, recording who asked for it. See <see cref="RebuildOrigin"/>: the origin is written onto
  /// the <c>PerspectiveRebuild*</c> events so the rebuild can be accounted for afterwards.
  /// </summary>
  Task<RebuildResult> RebuildBlueGreenAsync(
      string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) =>
    RebuildBlueGreenAsync(perspectiveName, ct);

  /// <summary>
  /// In-place rebuild: truncate the active table and replay all events.
  /// Faster but causes temporary data loss during replay.
  /// </summary>
  Task<RebuildResult> RebuildInPlaceAsync(
      string perspectiveName, CancellationToken ct = default);

  /// <summary>
  /// In-place rebuild, recording who asked for it. See <see cref="RebuildOrigin"/>.
  /// </summary>
  Task<RebuildResult> RebuildInPlaceAsync(
      string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) =>
    RebuildInPlaceAsync(perspectiveName, ct);

  /// <summary>
  /// Rebuild specific streams: replay events only for the given stream IDs.
  /// Useful for fixing individual corrupted/stale projections.
  /// </summary>
  Task<RebuildResult> RebuildStreamsAsync(
      string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default);

  /// <summary>
  /// Rebuild specific streams, recording who asked for it. This is the repair path an operator reaches for, so it
  /// is the one where provenance matters most. See <see cref="RebuildOrigin"/>.
  /// </summary>
  Task<RebuildResult> RebuildStreamsAsync(
      string perspectiveName, IEnumerable<Guid> streamIds, RebuildOrigin origin,
      CancellationToken ct = default) =>
    RebuildStreamsAsync(perspectiveName, streamIds, ct);

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
/// Who asked for a rebuild, and under what request. Carried onto the <c>PerspectiveRebuild*</c> events so a
/// rebuild's outcome can be found afterwards instead of being known only to the service's log.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequestId"/> is the handle that makes a rebuild answerable. A rebuild command is broadcast to
/// every service, and each one independently rebuilds only the perspectives it hosts, so a caller cannot tell
/// from the acknowledgement whether ANY service owned the name it asked for. With a request id on the emitted
/// events, the absence of a <c>PerspectiveRebuildStarted</c> for that id is itself the answer: nothing ran.
/// </para>
/// </remarks>
/// <param name="RequestId">Correlates every service's events for one requested rebuild. Null when unknown.</param>
/// <param name="Trigger">What caused the rebuild.</param>
/// <param name="RequestedBy">Who asked, when the caller supplied it.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public record RebuildOrigin(
    Guid? RequestId = null,
    RebuildTrigger Trigger = RebuildTrigger.Unknown,
    string? RequestedBy = null);

/// <summary>
/// What caused a rebuild. Distinguishes an operator repairing data from the framework rebuilding on its own,
/// which is the difference between "someone is waiting on this" and routine background work.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives#rebuild-events</docs>
public enum RebuildTrigger {
  /// <summary>Not recorded. The value an event deserialized from before provenance existed carries.</summary>
  Unknown = 0,
  /// <summary>An operator or an application asked for it, via the rebuild command or the rebuilder directly.</summary>
  Requested = 1,
  /// <summary>A schema or stored-form migration needed the perspective rebuilt.</summary>
  Migration = 2,
  /// <summary>The startup scan found the perspective needed rebuilding.</summary>
  StartupScan = 3
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
