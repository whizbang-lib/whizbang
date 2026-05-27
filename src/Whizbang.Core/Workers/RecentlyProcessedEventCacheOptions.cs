namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="RecentlyProcessedEventCache"/> and its background sweep worker.
/// Phase H step 7 slice 7.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class RecentlyProcessedEventCacheOptions {
  /// <summary>
  /// Killswitch — set to <c>false</c> to disable cooldown entirely (the drainer skips the
  /// short-circuit gate and calls the runner unconditionally). Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Time-to-live for an entry. Default 5 minutes — covers the cursor-flush race window
  /// (~25 ms), normal orphan-claim cycles (default 60 s), and maintenance sweeps with margin.
  /// </summary>
  public int TtlMinutes { get; set; } = 5;

  /// <summary>
  /// Hard cap on resident entries. Default 100k — when exceeded, the oldest ~10% evict on
  /// the next insert. At 1k events/sec sustained, the cap is reached after ~100 s; with the
  /// 5-minute TTL most entries expire before the cap matters.
  /// </summary>
  public int MaxEntries { get; set; } = 100_000;

  /// <summary>
  /// How often the background sweep runs to evict expired entries. Default 60 s. Lookups
  /// already use lazy expiry (past-TTL entries return false even before sweep), so this
  /// only affects memory footprint — set higher to reduce sweep CPU at the cost of more
  /// expired entries lingering in the dictionary.
  /// </summary>
  public int SweepIntervalSeconds { get; set; } = 60;
}
