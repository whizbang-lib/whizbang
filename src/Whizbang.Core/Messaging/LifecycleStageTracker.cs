using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Shared singleton that prevents the same message+stage combination from being
/// processed by multiple workers. First caller to <see cref="TryClaim(Guid, LifecycleStage)"/>
/// wins; subsequent callers skip.
/// </summary>
/// <remarks>
/// <para>
/// This prevents double-fire when multiple workers (TransportConsumerWorker,
/// WorkCoordinatorPublisherWorker) attempt to fire the same lifecycle stage
/// for the same inbox message.
/// </para>
/// <para>
/// Perspective-scoped stages (Pre/PostPerspective*, ImmediateDetached under a
/// perspective) are per-perspective rather than per-event: callers pass the running
/// perspective's <see cref="Type"/> via the <see cref="TryClaim(Guid, LifecycleStage, Type?)"/>
/// overload so N perspectives on the same event each get a distinct claim.
/// <see cref="PostAllPerspectivesInline"/> and
/// <see cref="PostAllPerspectivesDetached"/> are NOT perspective-scoped —
/// they fire exactly once per message.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LifecycleStageTrackerTests.cs</tests>
public sealed class LifecycleStageTracker {
  // Key is (messageId, stage, perspectiveType?). The perspective-type component lets
  // perspective-scoped stages (PostPerspectiveInline, PostPerspectiveDetached,
  // PrePerspectiveInline, PrePerspectiveDetached, ImmediateDetached) dedup per-perspective
  // rather than once per event — otherwise N perspectives all processing the same event
  // would see only the first perspective's stage fire. Null perspectiveType preserves
  // the original cross-worker dedup semantics for inbox/outbox/distribute stages.
  private readonly ConcurrentDictionary<(Guid MessageId, LifecycleStage Stage, Type? PerspectiveType), DateTimeOffset> _processed = new();

  // Insertion order, used to evict the oldest claims once the ceiling is reached. May hold keys
  // the map no longer has (anything released for retry); _evictOldest tolerates that by design.
  private readonly ConcurrentQueue<(Guid MessageId, LifecycleStage Stage, Type? PerspectiveType)> _claimOrder = new();

  private readonly int _maxTrackedClaims;

  /// <summary>Creates the tracker with a hard ceiling on how many claims it retains.</summary>
  /// <param name="maxTrackedClaims">Maximum retained claims before the oldest are evicted.</param>
  public LifecycleStageTracker(int maxTrackedClaims = 100_000) {
    _maxTrackedClaims = maxTrackedClaims > 0 ? maxTrackedClaims : 100_000;
  }

  /// <summary>How many claims are currently retained. Diagnostic; also what bounds memory.</summary>
  public int TrackedClaims => _processed.Count;

  /// <summary>
  /// Attempts to claim a message+stage for processing.
  /// Returns true if this is the first claim (caller should fire).
  /// Returns false if already claimed (caller should skip).
  /// </summary>
  public bool TryClaim(Guid messageId, LifecycleStage stage) =>
    TryClaim(messageId, stage, perspectiveType: null);

  /// <summary>
  /// Attempts to claim a message+stage+perspectiveType triple for processing. Perspective-scoped
  /// stages (Pre/PostPerspective*, ImmediateDetached) should pass the running perspective's
  /// type so that N perspectives processing the same event each get a distinct claim.
  /// </summary>
  public bool TryClaim(Guid messageId, LifecycleStage stage, Type? perspectiveType) {
    var key = (messageId, stage, perspectiveType);
    if (!_processed.TryAdd(key, DateTimeOffset.UtcNow)) {
      return false;
    }
    _claimOrder.Enqueue(key);
    _evictOldest();
    return true;
  }

  /// <summary>
  /// Drops the oldest claims until the retained set is back within its ceiling.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Eviction is FIFO rather than a scan for the oldest timestamp, for two reasons. It is
  /// equivalent — claims are added and never refreshed, so insertion order IS touch order — and
  /// it keeps this path lock-free. The receptor invoker consults the tracker for every message,
  /// so serializing behind a lock to compute a minimum would cost more than the growth did.
  /// </para>
  /// <para>
  /// Dropping the oldest cannot resurrect a double-fire. The tracker exists to stop two workers
  /// firing the same stage for the same message CONCURRENTLY, so only recent history is
  /// load-bearing; at any sane ceiling the evicted entries are far older than anything still in
  /// flight.
  /// </para>
  /// <para>
  /// The loop condition tests the MAP, not the queue: a key released through
  /// <see cref="Release(Guid, LifecycleStage, Type?)"/> is already gone from the map but still
  /// sits in the queue, and treating that stale dequeue as having freed a slot would let the map
  /// drift above capacity — leaking again at exactly the rate retries occur.
  /// </para>
  /// </remarks>
  private void _evictOldest() {
    while (_processed.Count > _maxTrackedClaims && _claimOrder.TryDequeue(out var oldest)) {
      _processed.TryRemove(oldest, out _);
    }
  }

  /// <summary>
  /// Releases a claim, allowing the message+stage to be reprocessed.
  /// Used when processing fails and a retry is needed.
  /// </summary>
  public void Release(Guid messageId, LifecycleStage stage) =>
    Release(messageId, stage, perspectiveType: null);

  /// <summary>
  /// Releases a claim for a specific perspective-scoped stage.
  /// </summary>
  public void Release(Guid messageId, LifecycleStage stage, Type? perspectiveType) =>
    _processed.TryRemove((messageId, stage, perspectiveType), out _);

  /// <summary>
  /// Removes entries older than <paramref name="maxAge"/>.
  /// </summary>
  /// <remarks>
  /// Optional. Memory safety does NOT depend on this being called — the retained set is capped
  /// and evicts its oldest entries automatically. It used to say "call periodically to prevent
  /// unbounded memory growth", which was an obligation on callers that nothing in the framework
  /// ever honored, so the set grew for the life of the process. Age-based trimming remains
  /// available for callers that want a tighter window than the capacity bound gives them.
  /// </remarks>
  public void Purge(TimeSpan maxAge) {
    var cutoff = DateTimeOffset.UtcNow - maxAge;
    foreach (var kvp in _processed) {
      if (kvp.Value < cutoff) {
        _processed.TryRemove(kvp.Key, out _);
      }
    }
  }
}
