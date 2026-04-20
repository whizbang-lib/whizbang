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
  public bool TryClaim(Guid messageId, LifecycleStage stage, Type? perspectiveType) =>
    _processed.TryAdd((messageId, stage, perspectiveType), DateTimeOffset.UtcNow);

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
  /// Call periodically to prevent unbounded memory growth.
  /// </summary>
  public void Purge(TimeSpan maxAge) {
    var cutoff = DateTimeOffset.UtcNow - maxAge;
    foreach (var kvp in _processed) {
      if (kvp.Value < cutoff) {
        _processed.TryRemove(kvp.Key, out _);
      }
    }
  }
}
