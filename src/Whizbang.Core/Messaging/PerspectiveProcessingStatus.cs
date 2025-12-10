namespace Whizbang.Core.Messaging;

/// <summary>
/// Status flags for tracking perspective processing of events.
/// Stored in wh_perspective_checkpoints table as checkpoints (per stream, per perspective).
/// Multiple flags can be combined using bitwise OR.
/// </summary>
[Flags]
public enum PerspectiveProcessingStatus {
  /// <summary>
  /// No processing has occurred.
  /// </summary>
  None = 0,

  /// <summary>
  /// Perspective is currently processing events.
  /// Indicates work in progress.
  /// </summary>
  Processing = 1 << 0,

  /// <summary>
  /// Perspective is up-to-date with the latest events in the stream.
  /// Normal operational state.
  /// </summary>
  Completed = 1 << 1,

  /// <summary>
  /// Perspective processing failed for this stream.
  /// May be retried later based on retry policy.
  /// </summary>
  Failed = 1 << 2,

  /// <summary>
  /// Perspective is catching up after being added or after falling behind.
  /// Indicates time-travel/replay scenario where old events are being processed.
  /// </summary>
  CatchingUp = 1 << 3

  // Bits 4-15 reserved for future use
}
