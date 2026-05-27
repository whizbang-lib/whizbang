namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="PerStreamSerializer{T}"/>.
/// </summary>
/// <docs>internals/stream-affinity</docs>
public sealed record PerStreamSerializerOptions {
  /// <summary>
  /// Bounded capacity of the per-stream channel. When a single stream's queue reaches this
  /// limit, <see cref="PerStreamSerializer{T}.EnqueueAsync"/> awaits until the channel has
  /// room (backpressure). Cross-stream parallelism unaffected. Default: 1000.
  /// </summary>
  public int StreamChannelCapacity { get; init; } = 1000;

  /// <summary>
  /// Drain accumulator window. After receiving the first item in a drain cycle, the worker
  /// waits up to this long for additional items to arrive before processing. Lets near-
  /// simultaneous enqueues (which may have completed in non-monotonic order due to
  /// concurrent producer threads) be batched and sorted via the optional comparer.
  /// Set to <see cref="TimeSpan.Zero"/> to disable batching (per-item processing). Default: 50 ms.
  /// </summary>
  public TimeSpan DrainBatchWindow { get; init; } = TimeSpan.FromMilliseconds(50);

  /// <summary>
  /// A stream's per-stream channel + worker is evicted when no items have been enqueued for
  /// this duration. Bounds memory under workloads with many short-lived streams. Default: 30 s.
  /// </summary>
  public TimeSpan IdleEvictionWindow { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How often the idle sweep runs to find and dispose evicted streams. Independent of
  /// <see cref="IdleEvictionWindow"/> — sweep interval can be shorter (responsive) or longer
  /// (cheap) than the eviction window. Default: 10 s.
  /// </summary>
  public TimeSpan IdleSweepInterval { get; init; } = TimeSpan.FromSeconds(10);
}
