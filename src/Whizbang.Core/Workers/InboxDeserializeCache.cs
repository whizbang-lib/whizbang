using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Per-instance bounded LRU cache for deserialized inbox payloads. Slice 15 of
/// plans/pump-then-process.md.
/// </summary>
/// <remarks>
/// <para>
/// The inbox dispatch path fires four lifecycle stages per message
/// (PreInbox / PostInbox / PostAllPerspectives / PostLifecycle). Without this cache, the
/// JSON payload re-deserializes from <see cref="System.Text.Json.JsonElement"/> on each stage
/// — wasted work for what is the exact same payload. This cache holds the deserialized object
/// keyed by <c>messageId</c> so all four stages reuse the same instance, AND so transport
/// redelivery / lease re-claim of the same message within the TTL window also reuses it.
/// </para>
/// <para>
/// Configurable TTL (default 2 minutes per slice 15 spec). Configurable size cap with
/// oldest-first eviction (default 10k entries) prevents unbounded memory growth on long-running
/// services. Time advances via <see cref="ITimeProvider"/> so tests can drive expiry
/// deterministically without <c>Task.Delay</c> per <c>feedback_no_timing_tests</c>.
/// </para>
/// <para>
/// AOT-safe: zero reflection, zero <c>Activator.CreateInstance</c>, the cached object is
/// returned by reference exactly as the deserializer produced it.
/// </para>
/// <para>
/// <strong>Restart-safe perf optimization, not a correctness primitive.</strong> Process
/// restart yields an empty cache; the next dispatch of any in-flight message simply
/// re-deserializes once and re-populates. The dispatcher's behavior is identical with or
/// without the cache.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/inbox-dispatch</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDeserializeCacheTests.cs</tests>
public sealed class InboxDeserializeCache {
  private const int DEFAULT_MAX_ENTRIES = 10_000;
  private static readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(2);

  private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
  private readonly ITimeProvider _timeProvider;
  private readonly TimeSpan _ttl;
  private readonly int _maxEntries;
  private readonly Lock _evictionLock = new();

  /// <summary>
  /// Creates a new <see cref="InboxDeserializeCache"/>.
  /// </summary>
  /// <param name="timeProvider">Time source — inject a fake in tests for deterministic TTL.</param>
  /// <param name="ttl">Time-to-live for an entry. Default 2 minutes.</param>
  /// <param name="maxEntries">Hard cap on resident entries. Default 10k. When exceeded, the oldest ~10% evict on next insert.</param>
  public InboxDeserializeCache(
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
  /// Returns <c>true</c> when <paramref name="messageId"/> has a cached deserialized payload
  /// within the TTL window. Lazy expiry — past-TTL entries return <c>false</c> even before
  /// <see cref="SweepExpired"/> runs.
  /// </summary>
  public bool TryGet(Guid messageId, out object? message) {
    if (!_entries.TryGetValue(messageId, out var entry)) {
      message = null;
      return false;
    }
    if (entry.ExpiresAt <= _timeProvider.GetUtcNow()) {
      message = null;
      return false;
    }
    message = entry.Payload;
    return true;
  }

  /// <summary>
  /// Stores <paramref name="message"/> against <paramref name="messageId"/> with a fresh TTL.
  /// Re-storing an existing key resets the expiry. Triggers cap-eviction if the insert pushes
  /// <see cref="Count"/> past the configured maximum.
  /// </summary>
  public void Set(Guid messageId, object message) {
    ArgumentNullException.ThrowIfNull(message);
    var expiresAt = _timeProvider.GetUtcNow() + _ttl;
    _entries[messageId] = new Entry(message, expiresAt);
    _enforceCapIfNeeded();
  }

  /// <summary>
  /// Drops every entry whose expiry is at or before now. Called periodically by a background
  /// sweep worker (or directly in tests). Safe to call concurrently with reads/writes.
  /// </summary>
  public void SweepExpired() {
    var now = _timeProvider.GetUtcNow();
    foreach (var pair in _entries) {
      if (pair.Value.ExpiresAt <= now) {
        ((System.Collections.Generic.ICollection<KeyValuePair<Guid, Entry>>)_entries)
          .Remove(pair);
      }
    }
  }

  private void _enforceCapIfNeeded() {
    if (_entries.Count <= _maxEntries) {
      return;
    }
    lock (_evictionLock) {
      var overflow = _entries.Count - _maxEntries;
      if (overflow <= 0) {
        return;
      }
      // Evict the oldest ~10% so we don't hit the cap again on the very next insert.
      var batch = Math.Max(overflow, _maxEntries / 10);
      var toEvict = _entries
        .OrderBy(static p => p.Value.ExpiresAt)
        .Take(batch)
        .Select(static p => p.Key)
        .ToArray();
      foreach (var key in toEvict) {
        _entries.TryRemove(key, out _);
      }
    }
  }

  private readonly record struct Entry(object Payload, DateTimeOffset ExpiresAt);
}
