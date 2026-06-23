namespace Whizbang.Core.Workers;

/// <summary>
/// Tuning knobs for the intra-pod per-stream serialization gate in
/// <see cref="PerspectiveWorker"/>. The gate dictionary grows by one entry per
/// distinct stream the worker has ever seen; activity-triggered eviction keeps
/// the dictionary bounded without paying for a background timer. See
/// <c>plans/perspective-worker-stream-affinity.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Activity-triggered design: every gate acquire and release stamps the entry's
/// <c>LastActivity</c>. After every release, if at least <see cref="SweepInterval"/>
/// has elapsed since the previous sweep, the worker walks the dictionary and
/// drops entries whose <c>LastActivity</c> is older than <see cref="IdleEvictionWindow"/>
/// AND whose semaphore is free (<c>CurrentCount == 1</c>). No background timer,
/// no thread woken just to GC — the sweep cost is amortized over the next item
/// of real work.
/// </para>
/// <para>
/// The semaphore-cache cleanup pattern is independent of
/// <see cref="PerspectiveCursorCache"/>, which today has no automatic eviction at
/// all (manual <c>InvalidateStream</c> only). Both caches can share these settings
/// in a follow-up that adopts the same activity-triggered eviction shape.
/// </para>
/// </remarks>
public class PerspectiveStreamAffinityOptions {

  /// <summary>
  /// How long a stream's gate entry can be idle (no acquire/release) before becoming
  /// eligible for eviction on the next sweep. Default: 15 minutes — comfortably
  /// longer than any reasonable batch processing window so a stream that's actively
  /// being drained isn't evicted between consecutive batches, but short enough that
  /// a saga's per-item streams (one-time use) don't accumulate forever after the
  /// import completes.
  /// </summary>
  public TimeSpan IdleEvictionWindow { get; set; } = TimeSpan.FromMinutes(15);

  /// <summary>
  /// Minimum time between sweeps. Throttles the per-release "should I sweep?" check
  /// so a hot stream's repeated activity doesn't pay for dictionary-wide walks on
  /// every event. Default: 1 minute — strikes the balance between memory bound and
  /// per-event overhead.
  /// </summary>
  public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);
}
