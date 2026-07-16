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
  /// Snapshot cadence for EPHEMERAL perspectives (those that apply at least one ephemeral event) — far more
  /// aggressive than <see cref="SnapshotEveryNEvents"/>, because an ephemeral stream must keep a fresh rewind
  /// floor within its (short) grace window before its consumed bodies are reaped. Default: 10 events.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  public int EphemeralSnapshotEveryNEvents { get; set; } = 10;

  /// <summary>
  /// Maximum snapshots kept per (stream, perspective) for EPHEMERAL perspectives. Single-slot by default (1):
  /// you can never rewind below the reap boundary, so only the latest snapshot is useful. Default: 1.
  /// </summary>
  /// <docs>fundamentals/events/ephemeral-events</docs>
  public int EphemeralMaxSnapshotsPerStream { get; set; } = 1;

  /// <summary>
  /// Whether snapshot creation is enabled.
  /// When disabled, no snapshots are created and rewinds always replay from event zero.
  /// Default: true.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// During a rewind replay, take an additional snapshot every N events applied. This puts
  /// snapshots at multiple historical points along the stream's event timeline so future
  /// rewinds — including for "very late" events whose MessageId falls *between* the end-of-
  /// rewind snapshot and earlier events — find a qualifying snapshot to roll back to.
  /// </summary>
  /// <remarks>
  /// Default: 10. With MaxSnapshotsPerStream=5 a 50-event rewind produces ~5 snapshots at
  /// events 10, 20, 30, 40, 50 (the most recent 5 are kept post-prune). Each in-replay
  /// snapshot adds one JSONB write + one prune call, but rewinds are uncommon enough that
  /// the cost is offset many times over by avoiding future full-replays-from-zero. Set to
  /// 0 to disable in-replay snapshots and keep only the end-of-rewind snapshot (legacy
  /// behavior).
  /// </remarks>
  public int RewindSnapshotIntervalEvents { get; set; } = 10;

  /// <summary>
  /// What to do when a stored snapshot's serialization version does not match the current
  /// <see cref="Whizbang.Core.Serialization.SerializationVersion.CURRENT"/> (e.g. after a snapshot
  /// serialization-format change, or for legacy unversioned blobs).
  /// Default: <see cref="SnapshotUpgradePolicy.RebuildFromEvents"/> — discard the stale snapshot and
  /// replay from events, which is always correct since snapshots are a derived cache.
  /// </summary>
  public SnapshotUpgradePolicy UpgradePolicy { get; set; } = SnapshotUpgradePolicy.RebuildFromEvents;
}
