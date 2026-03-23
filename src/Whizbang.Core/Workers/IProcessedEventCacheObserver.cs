namespace Whizbang.Core.Workers;

/// <summary>
/// Observer hooks for <see cref="ProcessedEventCache"/> lifecycle events.
/// Implement for debugging, deterministic test assertions, or custom metrics.
/// Register via DI to receive callbacks when events are deduped, cached, or evicted.
/// </summary>
/// <docs>operations/workers/perspective-worker#dedup-observer</docs>
/// <tests>Whizbang.Core.Tests/Workers/ProcessedEventCacheTests.cs</tests>
public interface IProcessedEventCacheObserver {
  /// <summary>
  /// Called when events are filtered out as duplicates before Apply.
  /// </summary>
  /// <param name="dedupedEventIds">Event IDs that were skipped.</param>
  /// <param name="perspectiveName">Perspective that would have processed these events.</param>
  /// <param name="streamId">Stream the events belong to.</param>
  void OnEventsDeduped(IReadOnlyList<Guid> dedupedEventIds, string perspectiveName, Guid streamId);

  /// <summary>
  /// Called when events are added to cache after successful Apply (InFlight phase).
  /// </summary>
  /// <param name="eventIds">Event IDs that were marked as in-flight.</param>
  void OnEventsMarkedInFlight(IReadOnlyList<Guid> eventIds);

  /// <summary>
  /// Called when <see cref="ProcessedEventCache.ActivateRetention"/> moves InFlight entries to Retained.
  /// TTL countdown begins from this moment.
  /// </summary>
  /// <param name="count">Number of entries that transitioned from InFlight to Retained.</param>
  void OnRetentionActivated(int count);

  /// <summary>
  /// Called when expired entries are evicted from the cache.
  /// </summary>
  /// <param name="count">Number of entries removed.</param>
  void OnEvicted(int count);

  /// <summary>
  /// Called when entries are force-removed (e.g., for rewind replay).
  /// </summary>
  /// <param name="eventIds">Event IDs that were removed.</param>
  void OnEventsRemoved(IReadOnlyList<Guid> eventIds);
}

/// <summary>
/// No-op observer. Registered by default — zero overhead.
/// </summary>
internal sealed class NullProcessedEventCacheObserver : IProcessedEventCacheObserver {
  public static readonly NullProcessedEventCacheObserver Instance = new();

  public void OnEventsDeduped(IReadOnlyList<Guid> dedupedEventIds, string perspectiveName, Guid streamId) { }
  public void OnEventsMarkedInFlight(IReadOnlyList<Guid> eventIds) { }
  public void OnRetentionActivated(int count) { }
  public void OnEvicted(int count) { }
  public void OnEventsRemoved(IReadOnlyList<Guid> eventIds) { }
}
