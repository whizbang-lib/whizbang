using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Shared singleton that prevents the same message+stage combination from being
/// processed by multiple workers. First caller to <see cref="TryClaim"/> wins;
/// subsequent callers skip.
/// </summary>
/// <remarks>
/// This prevents double-fire when multiple workers (TransportConsumerWorker,
/// WorkCoordinatorPublisherWorker) attempt to fire the same lifecycle stage
/// for the same inbox message.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LifecycleStageTrackerTests.cs</tests>
public sealed class LifecycleStageTracker {
  private readonly ConcurrentDictionary<(Guid MessageId, LifecycleStage Stage), DateTimeOffset> _processed = new();

  /// <summary>
  /// Attempts to claim a message+stage for processing.
  /// Returns true if this is the first claim (caller should fire).
  /// Returns false if already claimed (caller should skip).
  /// </summary>
  public bool TryClaim(Guid messageId, LifecycleStage stage) =>
    _processed.TryAdd((messageId, stage), DateTimeOffset.UtcNow);

  /// <summary>
  /// Releases a claim, allowing the message+stage to be reprocessed.
  /// Used when processing fails and a retry is needed.
  /// </summary>
  public void Release(Guid messageId, LifecycleStage stage) =>
    _processed.TryRemove((messageId, stage), out _);

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
