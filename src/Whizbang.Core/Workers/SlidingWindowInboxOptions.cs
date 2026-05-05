namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="SlidingWindowInboxBatchStrategy"/>. Defaults match the
/// pump-then-process plan: 50 ms debounce / 1 s hard cap / 100-message batch ceiling.
/// </summary>
/// <docs>internals/inbox-batch-strategy</docs>
public sealed record SlidingWindowInboxOptions {
  /// <summary>
  /// Debounce window after the last append. Resets on each new arrival. When this elapses
  /// without new arrivals, the current batch flushes. Default: 50 ms.
  /// </summary>
  public TimeSpan SlidingWindow { get; init; } = TimeSpan.FromMilliseconds(50);

  /// <summary>
  /// Hard cap on time from the first append in a batch. Even a continuously-busy producer
  /// flushes within this window. Default: 1 s.
  /// </summary>
  public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum messages per flushed batch. The batch flushes immediately when this is reached.
  /// Default: 100.
  /// </summary>
  public int MaxSize { get; init; } = 100;
}
