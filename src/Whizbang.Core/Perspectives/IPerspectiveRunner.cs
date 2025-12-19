using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Runs perspective event replay for a specific stream.
/// Loads events from event store, applies them to perspectives, and tracks checkpoint progress.
/// Generated code implements this interface for each discovered perspective.
/// </summary>
public interface IPerspectiveRunner {
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
  Task<PerspectiveCheckpointCompletion> RunAsync(
    Guid streamId,
    string perspectiveName,
    Guid? lastProcessedEventId,
    CancellationToken cancellationToken = default
  );
}
