namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="SlidingWindowInboxBatchStrategy"/>. Per-slice-23 defaults
/// align with the perspective apply boundary: 300 ms debounce / 3 s hard cap / 1000-message
/// batch ceiling per stream / 30 s idle eviction.
/// </summary>
/// <remarks>
/// The longer-than-the-old-50ms window is required for fan-in saga aggregates: events
/// arriving across multiple transport messages for the same stream coalesce into one
/// flush before downstream applies, eliminating cross-batch cursor inversions.
/// </remarks>
/// <docs>internals/inbox-batch-strategy</docs>
public sealed record SlidingWindowInboxOptions {
  /// <summary>
  /// Debounce window after the last append PER STREAM. Resets on each new arrival for the
  /// same stream. When this elapses without new arrivals, the stream's batch flushes.
  /// Default: 300 ms (slice 23). Matches the apply-boundary window so a hot stream
  /// receiving cross-producer events sees them all in one apply cycle.
  /// </summary>
  public TimeSpan SlidingWindow { get; set; } = TimeSpan.FromMilliseconds(300);

  /// <summary>
  /// Hard cap on time from the first append in a batch (per stream). A continuously-busy
  /// stream still flushes within this window. Default: 3 s (slice 23).
  /// </summary>
  public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(3);

  /// <summary>
  /// Maximum messages per stream batch. The stream's batch flushes immediately when this is
  /// reached. Default: 1000 (slice 23 — raised from 100 to accommodate larger fan-in bursts).
  /// </summary>
  public int MaxSize { get; set; } = 1000;

  /// <summary>
  /// A stream's per-stream buffer is evicted when no items have been appended for this
  /// duration. Bounds memory under workloads with many short-lived streams. Default: 30 s.
  /// </summary>
  public TimeSpan IdleEvictionWindow { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How often the idle sweep runs to find and dispose evicted streams. Default: 10 s.
  /// </summary>
  public TimeSpan IdleSweepInterval { get; set; } = TimeSpan.FromSeconds(10);
}
