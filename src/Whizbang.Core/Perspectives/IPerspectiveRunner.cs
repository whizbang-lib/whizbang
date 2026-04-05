using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Runs perspective event replay for a specific stream.
/// Loads events from event store, applies them to perspectives, and tracks checkpoint progress.
/// Generated code implements this interface for each discovered perspective.
/// </summary>
public interface IPerspectiveRunner {
  /// <summary>
  /// Gets the CLR type of the perspective this runner processes.
  /// Used by lifecycle stages to identify which perspective is processing.
  /// </summary>
  Type PerspectiveType { get; }

  /// <summary>
  /// Processes perspective checkpoint for a stream using unit-of-work pattern.
  /// Loads events from event store, applies them in UUID7 order, and saves checkpoint.
  /// Supports partial success - saves checkpoint at last successful event before failure.
  /// </summary>
  /// <param name="streamId">Stream ID to process</param>
  /// <param name="perspectiveName">Name of the perspective to run</param>
  /// <param name="lastProcessedEventId">Last event ID processed (null = start from beginning)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Perspective checkpoint completion with processing status and last event ID</returns>
  Task<PerspectiveCursorCompletion> RunAsync(
    Guid streamId,
    string perspectiveName,
    Guid? lastProcessedEventId,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Rewinds a perspective by restoring from the nearest snapshot before the triggering event,
  /// then replays all events from that point in order. If no qualifying snapshot exists,
  /// performs a full replay from event zero.
  /// </summary>
  /// <param name="streamId">Stream to rewind</param>
  /// <param name="perspectiveName">Perspective to rewind</param>
  /// <param name="triggeringEventId">The late event that triggered the rewind (for snapshot selection)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Perspective cursor completion with processing status and last event ID</returns>
  Task<PerspectiveCursorCompletion> RewindAndRunAsync(
    Guid streamId,
    string perspectiveName,
    Guid triggeringEventId,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Creates a bootstrap snapshot from the current model state.
  /// Called when a stream has processed events but has no snapshots yet.
  /// Idempotent — if snapshots already exist, this is never called.
  /// </summary>
  /// <param name="streamId">Stream to bootstrap</param>
  /// <param name="perspectiveName">Perspective to bootstrap</param>
  /// <param name="lastProcessedEventId">The cursor's last processed event ID to snapshot at</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task BootstrapSnapshotAsync(
    Guid streamId,
    string perspectiveName,
    Guid lastProcessedEventId,
    CancellationToken cancellationToken = default
  );
}
