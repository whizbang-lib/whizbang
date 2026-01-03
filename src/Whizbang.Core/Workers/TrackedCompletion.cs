namespace Whizbang.Core.Workers;

/// <summary>
/// Wrapper for completions with status tracking and retry metadata.
/// </summary>
/// <typeparam name="T">Type of completion (MessageCompletion, PerspectiveCheckpointCompletion, etc.)</typeparam>
public sealed class TrackedCompletion<T> where T : notnull {
  /// <summary>
  /// The actual completion data being tracked.
  /// </summary>
  public required T Completion { get; init; }

  /// <summary>
  /// Current status in the acknowledgement lifecycle.
  /// </summary>
  public CompletionStatus Status { get; set; } = CompletionStatus.Pending;

  /// <summary>
  /// Timestamp when this completion was sent to ProcessWorkBatchAsync.
  /// </summary>
  public DateTimeOffset SentAt { get; set; }

  /// <summary>
  /// Number of times this completion has been retried after timeout.
  /// Used for exponential backoff calculation.
  /// </summary>
  public int RetryCount { get; set; }

  /// <summary>
  /// Unique identifier for tracking this specific completion instance.
  /// </summary>
  public Guid TrackingId { get; init; } = Guid.NewGuid();
}
