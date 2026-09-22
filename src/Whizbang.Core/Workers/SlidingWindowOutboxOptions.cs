namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="SlidingWindowOutboxBatchStrategy"/>. Defaults match the
/// pump-then-process plan: 50 ms debounce / 1 s hard cap / 100-message batch ceiling per stream.
/// </summary>
/// <docs>internals/outbox-batch-strategy</docs>
public sealed record SlidingWindowOutboxOptions {
  /// <summary>
  /// Debounce window after the last append PER STREAM. Resets on each new arrival for the
  /// same stream. When this elapses without new arrivals, the stream's batch flushes.
  /// Default: 50 ms.
  /// </summary>
  public TimeSpan SlidingWindow { get; set; } = TimeSpan.FromMilliseconds(50);

  /// <summary>
  /// Hard cap on time from the first append in a batch (per stream). A continuously-busy
  /// stream still flushes within this window. Default: 1 s.
  /// </summary>
  public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum messages per stream batch. The stream's batch flushes immediately when this is
  /// reached. Default: 100.
  /// </summary>
  public int MaxSize { get; set; } = 100;

  /// <summary>
  /// A stream's per-stream buffer is evicted when no items have been appended for this duration.
  /// Bounds memory under workloads with many short-lived streams. Default: 30 s.
  /// </summary>
  public TimeSpan IdleEvictionWindow { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How often the idle sweep runs to find and dispose evicted streams. Default: 10 s.
  /// </summary>
  public TimeSpan IdleSweepInterval { get; set; } = TimeSpan.FromSeconds(10);
}
