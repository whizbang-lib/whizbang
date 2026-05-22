namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="SlidingWindowApplyBatchStrategy"/>. Defaults match the user
/// direction for the perspective apply boundary: 300 ms debounce / 3 s hard cap. Drain
/// signals for the same stream coalesce into a single apply flush within that window.
/// </summary>
/// <docs>internals/apply-batch-strategy</docs>
public sealed record SlidingWindowApplyOptions {
  /// <summary>
  /// Debounce window after the last drain signal PER STREAM. Resets on each new signal for
  /// the same stream. When this elapses without new signals, the stream flushes — the
  /// downstream callback runs ONE apply cycle for everything pending on that stream.
  /// Default: 300 ms.
  /// </summary>
  public TimeSpan SlidingWindow { get; init; } = TimeSpan.FromMilliseconds(300);

  /// <summary>
  /// Hard cap on time from the first drain signal in a batch (per stream). A continuously
  /// busy stream still flushes within this window — bounds end-to-end apply latency for
  /// hot streams like the Order saga aggregate. Default: 3 s.
  /// </summary>
  public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(3);

  /// <summary>
  /// Maximum drain signals per stream batch. The stream flushes immediately when this is
  /// reached. Signals dedupe semantically (one apply cycle picks up all pending events
  /// regardless of signal count), so this is mostly a memory bound. Default: 1000.
  /// </summary>
  public int MaxSize { get; init; } = 1000;

  /// <summary>
  /// A stream's per-stream buffer is evicted when no signals have been appended for this
  /// duration. Bounds memory under workloads with many short-lived streams. Default: 30 s.
  /// </summary>
  public TimeSpan IdleEvictionWindow { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How often the idle sweep runs to find and dispose evicted streams. Default: 10 s.
  /// </summary>
  public TimeSpan IdleSweepInterval { get; init; } = TimeSpan.FromSeconds(10);
}
