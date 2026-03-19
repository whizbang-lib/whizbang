namespace Whizbang.Core.Perspectives;

/// <summary>
/// Configuration options for perspective snapshot creation and management.
/// Controls snapshot frequency, retention, and whether snapshots are enabled.
/// </summary>
/// <docs>fundamentals/perspectives/snapshots</docs>
public class PerspectiveSnapshotOptions {
  /// <summary>
  /// Create a snapshot every N events processed.
  /// Default: 100 events.
  /// </summary>
  public int SnapshotEveryNEvents { get; set; } = 100;

  /// <summary>
  /// Maximum number of snapshots to keep per (stream, perspective) pair.
  /// Oldest snapshots are pruned after each new snapshot creation.
  /// Default: 5 snapshots.
  /// </summary>
  public int MaxSnapshotsPerStream { get; set; } = 5;

  /// <summary>
  /// Whether snapshot creation is enabled.
  /// When disabled, no snapshots are created and rewinds always replay from event zero.
  /// Default: true.
  /// </summary>
  public bool Enabled { get; set; } = true;
}
