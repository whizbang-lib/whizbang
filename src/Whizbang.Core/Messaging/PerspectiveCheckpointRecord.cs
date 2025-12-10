namespace Whizbang.Core.Messaging;

/// <summary>
/// Checkpoint-style tracking of perspective processing per stream.
/// Stored in wh_perspective_checkpoints table.
/// Tracks the last processed event for each perspective on each stream.
/// Enables time-travel scenarios where new perspectives can catch up by replaying events.
/// </summary>
public sealed class PerspectiveCheckpointRecord {
  /// <summary>
  /// The stream being processed.
  /// </summary>
  public required Guid StreamId { get; set; }

  /// <summary>
  /// Name of the perspective processing this stream.
  /// </summary>
  public required string PerspectiveName { get; set; }

  /// <summary>
  /// Last event ID processed on this stream.
  /// UUIDv7 - naturally ordered by time, doubles as sequence number.
  /// </summary>
  public required Guid LastEventId { get; set; }

  /// <summary>
  /// Current processing status flags for this checkpoint.
  /// </summary>
  public required PerspectiveProcessingStatus Status { get; set; }

  /// <summary>
  /// When this checkpoint was last updated.
  /// </summary>
  public DateTime ProcessedAt { get; set; }

  /// <summary>
  /// Error message if processing failed at this checkpoint.
  /// </summary>
  public string? Error { get; set; }
}
