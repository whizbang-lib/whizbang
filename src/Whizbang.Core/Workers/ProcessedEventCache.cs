using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Thread-safe two-phase TTL cache of processed event IDs for deduplication.
/// Prevents duplicate Apply calls when SQL re-delivers events during the batched completion window.
/// </summary>
/// <remarks>
/// <para><strong>Phase 1 (InFlight)</strong>: Event added after Apply, no expiry — guards until DB confirms.</para>
/// <para><strong>Phase 2 (Retained)</strong>: After DB ack via <see cref="ActivateRetention"/>,
/// TTL starts counting down from the lease-aligned retention period.</para>
/// <para><strong>Evicted</strong>: After TTL expires, entries are removed. SQL re-delivery is then
/// allowed (correct for rewind/rebuild scenarios).</para>
/// </remarks>
/// <docs>operations/workers/perspective-worker#event-deduplication</docs>
/// <tests>Whizbang.Core.Tests/Workers/ProcessedEventCacheTests.cs</tests>
internal sealed class ProcessedEventCache {
  private readonly ConcurrentDictionary<Guid, EventCacheEntry> _entries = new();
  private readonly TimeSpan _retentionPeriod;
  private readonly TimeProvider _timeProvider;
  private readonly IProcessedEventCacheObserver _observer;

  /// <summary>
  /// Creates a new ProcessedEventCache with the specified retention period.
  /// </summary>
  /// <param name="retentionPeriod">How long retained entries survive after DB acknowledgement (aligned to lease duration).</param>
  /// <param name="timeProvider">Time provider for testability. Defaults to <see cref="TimeProvider.System"/>.</param>
  /// <param name="observer">Observer for lifecycle callbacks. Defaults to <see cref="NullProcessedEventCacheObserver"/>.</param>
  public ProcessedEventCache(
    TimeSpan retentionPeriod,
    TimeProvider? timeProvider = null,
    IProcessedEventCacheObserver? observer = null) {
    _retentionPeriod = retentionPeriod;
    _timeProvider = timeProvider ?? TimeProvider.System;
    _observer = observer ?? NullProcessedEventCacheObserver.Instance;
  }

  /// <summary>
  /// The observer for lifecycle callbacks.
  /// </summary>
  internal IProcessedEventCacheObserver Observer => _observer;

  /// <summary>
  /// Number of active entries in the cache (InFlight + non-expired Retained).
  /// </summary>
  public int Count => _entries.Count;

  /// <summary>
  /// Returns true if the event ID is in the cache and has not expired.
  /// InFlight entries always return true. Retained entries return true only within the TTL window.
  /// </summary>
  public bool Contains(Guid eventId) {
    if (!_entries.TryGetValue(eventId, out var entry)) {
      return false;
    }

    // InFlight (AckedAt == null) → always present
    if (entry.AckedAt is null) {
      return true;
    }

    // Retained → check if within TTL window
    return _timeProvider.GetUtcNow() < entry.AckedAt.Value + _retentionPeriod;
  }

  /// <summary>
  /// Adds event IDs to the cache. By default, entries are InFlight (no TTL).
  /// Idempotent — duplicate IDs are ignored.
  /// </summary>
  /// <param name="eventIds">Event IDs to cache.</param>
  /// <param name="startTtl">If true, entries start as Retained with TTL immediately. Default: false (InFlight).</param>
  public void AddRange(IEnumerable<Guid> eventIds, bool startTtl = false) {
    var added = new List<Guid>();
    var ackedAt = startTtl ? _timeProvider.GetUtcNow() : (DateTimeOffset?)null;

    foreach (var eventId in eventIds) {
      if (_entries.TryAdd(eventId, new EventCacheEntry(ackedAt))) {
        added.Add(eventId);
      }
    }

    if (added.Count > 0) {
      _observer.OnEventsMarkedInFlight(added);
    }
  }

  /// <summary>
  /// Transitions all InFlight entries to Retained, starting the TTL countdown.
  /// Called after the database acknowledges completion of the work batch.
  /// Already-Retained entries are not affected.
  /// </summary>
  public void ActivateRetention() {
    var now = _timeProvider.GetUtcNow();
    var activatedCount = 0;

    foreach (var kvp in _entries) {
      if (kvp.Value.AckedAt is null) {
        if (_entries.TryUpdate(kvp.Key, new EventCacheEntry(now), kvp.Value)) {
          activatedCount++;
        }
      }
    }

    if (activatedCount > 0) {
      _observer.OnRetentionActivated(activatedCount);
    }
  }

  /// <summary>
  /// Removes expired Retained entries from the cache.
  /// InFlight entries are never evicted. Call at the start of each poll cycle.
  /// </summary>
  public void EvictExpired() {
    var now = _timeProvider.GetUtcNow();
    var evictedCount = 0;

    foreach (var kvp in _entries) {
      // Only evict Retained entries that are past their TTL
      if (kvp.Value.AckedAt is not null && now >= kvp.Value.AckedAt.Value + _retentionPeriod
          && _entries.TryRemove(kvp.Key, out _)) {
        evictedCount++;
      }
    }

    if (evictedCount > 0) {
      _observer.OnEvicted(evictedCount);
    }
  }

  /// <summary>
  /// Force-removes an event ID from the cache.
  /// Used during rewind to allow replay of previously processed events.
  /// </summary>
  public void Remove(Guid eventId) {
    if (_entries.TryRemove(eventId, out _)) {
      _observer.OnEventsRemoved([eventId]);
    }
  }

  /// <summary>
  /// Force-removes multiple event IDs from the cache.
  /// Used during rewind to allow replay of previously processed events.
  /// </summary>
  public void RemoveRange(IEnumerable<Guid> eventIds) {
    var removed = new List<Guid>();
    foreach (var eventId in eventIds) {
      if (_entries.TryRemove(eventId, out _)) {
        removed.Add(eventId);
      }
    }

    if (removed.Count > 0) {
      _observer.OnEventsRemoved(removed);
    }
  }

  /// <summary>
  /// Entry in the processed event cache.
  /// AckedAt == null → InFlight (never expires).
  /// AckedAt != null → Retained (expires at AckedAt + retentionPeriod).
  /// </summary>
  private readonly record struct EventCacheEntry(DateTimeOffset? AckedAt);
}
