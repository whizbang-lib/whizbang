using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Per-instance in-memory cooldown cache keyed by <c>event_work_id</c>. Phase H step 7 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// The perspective drainer consults this cache as the FIRST filter in its prefetch pipeline.
/// Any work-id seen within the configured TTL is treated as already processed — the drainer
/// skips body fetch and apply for that id. This catches duplicates from any source:
/// </para>
/// <list type="bullet">
/// <item><description>Cursor-flush race: <c>wh_perspective_events</c> row still pending in the
/// ~25 ms window between drain finish and <c>PerspectiveCompletionFlushWorker</c> DELETE.</description></item>
/// <item><description>Orphan re-claim: lease expired and <c>claim_orphaned_perspective_events</c>
/// re-issued the row before the original claim's completion landed.</description></item>
/// <item><description>Late NOTIFY signals piling up after the actual work completed.</description></item>
/// </list>
/// <para>
/// Restart-safe perf optimization, not a correctness primitive — the runner template's defensive
/// idempotency guard catches anything that slips through. After a process restart the cache is
/// empty; the first duplicate-fetch flow falls back to the runner-side guard, which still works
/// correctly. Subsequent duplicates within the TTL window are blocked once the id is re-marked.
/// </para>
/// <para>
/// Capacity is bounded by <c>maxEntries</c>. When an insert would exceed the cap, the oldest
/// ~10% (by expiry time, ascending) are evicted in the same call. Time advances via the
/// injected <see cref="ITimeProvider"/> so tests can drive expiry deterministically without
/// <c>Task.Delay</c> per <c>feedback_no_timing_tests</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
public sealed class RecentlyProcessedEventCache {
  private const int DEFAULT_MAX_ENTRIES = 100_000;
  private static readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(5);

  private readonly ConcurrentDictionary<Guid, DateTimeOffset> _entries = new();
  private readonly ITimeProvider _timeProvider;
  private readonly TimeSpan _ttl;
  private readonly int _maxEntries;
  private readonly Lock _evictionLock = new();

  /// <summary>
  /// Creates a new <see cref="RecentlyProcessedEventCache"/>.
  /// </summary>
  /// <param name="timeProvider">Time source — inject a fake in tests for deterministic TTL.</param>
  /// <param name="ttl">Time-to-live for an entry. Default 5 minutes.</param>
  /// <param name="maxEntries">Hard cap on resident entries. Default 100k. When exceeded, the oldest ~10% evict on next insert.</param>
  public RecentlyProcessedEventCache(
    ITimeProvider timeProvider,
    TimeSpan? ttl = null,
    int maxEntries = DEFAULT_MAX_ENTRIES) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    if (maxEntries < 1) {
      throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be at least 1.");
    }
    var ttlValue = ttl ?? _defaultTtl;
    if (ttlValue <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(ttl), "ttl must be positive.");
    }
    _timeProvider = timeProvider;
    _ttl = ttlValue;
    _maxEntries = maxEntries;
  }

  /// <summary>Number of resident entries (may include some expired-but-not-swept).</summary>
  public int Count => _entries.Count;

  /// <summary>
  /// Returns <c>true</c> when this <paramref name="eventWorkId"/> was marked processed within
  /// the TTL window. Lazy expiry — past-TTL entries return <c>false</c> even before
  /// <see cref="SweepExpired"/> runs.
  /// </summary>
  public bool WasRecentlyProcessed(Guid eventWorkId) {
    if (!_entries.TryGetValue(eventWorkId, out var expiresAt)) {
      return false;
    }
    return expiresAt > _timeProvider.GetUtcNow();
  }

  /// <summary>
  /// Marks <paramref name="eventWorkId"/> as recently processed. Re-marking refreshes the expiry.
  /// Triggers cap-eviction if the insert pushes <see cref="Count"/> past the configured maximum.
  /// </summary>
  public void MarkProcessed(Guid eventWorkId) {
    var expiresAt = _timeProvider.GetUtcNow() + _ttl;
    _entries[eventWorkId] = expiresAt;
    _enforceCapIfNeeded();
  }

  /// <summary>
  /// Bulk variant of <see cref="MarkProcessed(Guid)"/>. Cap-eviction runs once at the end.
  /// </summary>
  public void MarkProcessed(IEnumerable<Guid> eventWorkIds) {
    ArgumentNullException.ThrowIfNull(eventWorkIds);
    var expiresAt = _timeProvider.GetUtcNow() + _ttl;
    foreach (var id in eventWorkIds) {
      _entries[id] = expiresAt;
    }
    _enforceCapIfNeeded();
  }

  /// <summary>
  /// Drops every entry whose expiry is at or before now. Called periodically by the
  /// <see cref="RecentlyProcessedEventCacheSweepWorker"/> background service. Safe to call
  /// concurrently with reads/writes.
  /// </summary>
  public void SweepExpired() {
    var now = _timeProvider.GetUtcNow();
    foreach (var pair in _entries) {
      if (pair.Value <= now) {
        // ConcurrentDictionary.TryRemove(KeyValuePair) is the race-safe variant — only removes
        // if the value still matches, so a concurrent re-mark is preserved.
        ((System.Collections.Generic.ICollection<KeyValuePair<Guid, DateTimeOffset>>)_entries)
          .Remove(pair);
      }
    }
  }

  private void _enforceCapIfNeeded() {
    if (_entries.Count <= _maxEntries) {
      return;
    }
    lock (_evictionLock) {
      // Re-check inside the lock — another thread may have evicted in the meantime.
      var overflow = _entries.Count - _maxEntries;
      if (overflow <= 0) {
        return;
      }
      // Evict the oldest 10% so we don't hit the cap again on the very next insert.
      var batch = Math.Max(overflow, _maxEntries / 10);
      var toEvict = _entries
        .OrderBy(static p => p.Value)
        .Take(batch)
        .Select(static p => p.Key)
        .ToArray();
      foreach (var key in toEvict) {
        _entries.TryRemove(key, out _);
      }
    }
  }
}
