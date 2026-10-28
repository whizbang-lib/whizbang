// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>Default byte budget (#1793): 64 MiB of payload, measured as the payload's UTF-8 JSON.</summary>
  public const long DEFAULT_MAX_BYTES = 64L * 1024 * 1024;
  private static readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(2);

  private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
  private readonly ITimeProvider _timeProvider;
  private readonly TimeSpan _ttl;
  private readonly int _maxEntries;
  private readonly long _maxBytes;
  private long _bytes;
  private readonly Lock _evictionLock = new();

  /// <summary>
  /// Creates a new <see cref="InboxDeserializeCache"/>.
  /// </summary>
  /// <param name="timeProvider">Time source — inject a fake in tests for deterministic TTL.</param>
  /// <param name="ttl">Time-to-live for an entry. Default 2 minutes.</param>
  /// <param name="maxEntries">Hard cap on resident entries. Default 10k. When exceeded, the oldest ~10% evict on next insert.</param>
  /// <param name="maxBytes">Byte budget across resident entries (#1793). Default <see cref="DEFAULT_MAX_BYTES"/>.</param>
  public InboxDeserializeCache(
    ITimeProvider timeProvider,
    TimeSpan? ttl = null,
    int maxEntries = DEFAULT_MAX_ENTRIES,
    long maxBytes = DEFAULT_MAX_BYTES) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    if (maxEntries < 1) {
      throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be at least 1.");
    }
    var ttlValue = ttl ?? _defaultTtl;
    if (ttlValue <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(ttl), "ttl must be positive.");
    }
    if (maxBytes < 1) {
      throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be at least 1.");
    }
    _timeProvider = timeProvider;
    _ttl = ttlValue;
    _maxEntries = maxEntries;
    _maxBytes = maxBytes;
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
  public void Set(Guid messageId, object message) => Set(messageId, message, 0);

  /// <summary>
  /// Stores <paramref name="message"/> with the size of the payload it was parsed from (#1793). Past the byte budget the
  /// oldest entries are evicted until it fits; a payload larger than the whole budget is not cached, and any older
  /// entry for the key is dropped so a stale copy is never served.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDeserializeCacheByteBudgetTests.cs</tests>
  public void Set(Guid messageId, object message, long sizeBytes) {
    ArgumentNullException.ThrowIfNull(message);
    var size = Math.Max(0, sizeBytes);
    lock (_evictionLock) {
      _removeLocked(messageId);
      if (size > _maxBytes) {
        return;
      }
      _entries[messageId] = new Entry(message, _timeProvider.GetUtcNow() + _ttl, size);
      _bytes += size;
      _enforceCapLocked();
    }
  }

  /// <summary>Drops the entry for a row that completed (#1793); its payload will not be parsed again.</summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDeserializeCacheByteBudgetTests.cs</tests>
  public void Remove(Guid messageId) {
    lock (_evictionLock) {
      _removeLocked(messageId);
    }
  }

  /// <summary>Payload bytes the resident entries were parsed from (#1793).</summary>
  public long Bytes => Interlocked.Read(ref _bytes);

  private void _removeLocked(Guid messageId) {
    if (_entries.TryRemove(messageId, out var old)) {
      _bytes -= old.Size;
    }
  }

  /// <summary>
  /// Drops every entry whose expiry is at or before now. Called periodically by a background
  /// sweep worker (or directly in tests). Safe to call concurrently with reads/writes.
  /// </summary>
  public void SweepExpired() {
    var now = _timeProvider.GetUtcNow();
    lock (_evictionLock) {
      foreach (var pair in _entries) {
        if (pair.Value.ExpiresAt <= now) {
          _removeLocked(pair.Key);
        }
      }
    }
  }

  private void _enforceCapLocked() {
    var overflow = _entries.Count - _maxEntries;
    if (overflow > 0) {
      // Evict the oldest ~10% so we don't hit the cap again on the very next insert.
      foreach (var key in SelectEvictionKeys(_entries, Math.Max(overflow, _maxEntries / 10))) {
        _removeLocked(key);
      }
    }
    if (_bytes <= _maxBytes) {
      return;
    }
    // #1793: oldest first until the payloads fit the byte budget.
    foreach (var key in SelectEvictionKeys(_entries, _entries.Count)) {
      _removeLocked(key);
      if (_bytes <= _maxBytes) {
        return;
      }
    }
  }

  /// <summary>
  /// The keys a cap enforcement evicts: the oldest by expiry, up to <paramref name="batch"/> of
  /// them.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Separated from the enforcement so the selection can be driven directly. The entries it is
  /// given are a live concurrent dictionary in production, which constrains how it may read them
  /// and is the whole reason this is its own method.
  /// </para>
  /// <para>
  /// It is enumerated, never materialized through its <c>Count</c>. A
  /// <see cref="ConcurrentDictionary{TKey, TValue}"/> is designed to be walked with its own
  /// enumerator, which yields a moving but coherent snapshot and tolerates concurrent writes. Going
  /// through the <see cref="ICollection{T}"/> face instead -- which is what LINQ does when it
  /// buffers an ordered source for a <c>Take</c> -- reads <c>Count</c>, allocates an array of that
  /// size, and then calls <c>CopyTo</c>, which refuses if the dictionary has grown in between. That
  /// threw under load and the dispatch worker logged it as a failed deserialization and continued,
  /// so a message stopped being processed for a reason nothing named.
  /// </para>
  /// <para>
  /// The result is therefore best effort, and that is the correct contract here: the cache is a
  /// performance aid whose cap exists to bound memory, so a cap briefly off by a few entries costs
  /// nothing and throwing costs a message. The count may move while this runs; what it must never
  /// do is fail.
  /// </para>
  /// </remarks>
  internal static Guid[] SelectEvictionKeys(
      IEnumerable<KeyValuePair<Guid, Entry>> entries, int batch) {
    ArgumentNullException.ThrowIfNull(entries);
    if (batch <= 0) {
      return [];
    }
    // The foreach is the point: one pass, no size question asked of the source.
    var snapshot = new List<KeyValuePair<Guid, Entry>>();
#pragma warning disable RCS1235 // Optimize method call -> "Optimize 'Add' call". AddRange and the
    // collection initializer both reach for the source's Count to size their destination, which is
    // exactly the call that threw here and the reason this method exists. See the remarks above. Do
    // not take this suggestion, and check the id against the analyzer output before changing it: an
    // id that names a rule this code does not raise suppresses nothing and the finding ships.
    foreach (var pair in entries) {
      snapshot.Add(pair);
    }
#pragma warning restore RCS1235
    // Sorting a private list, never the live source: the ordering below cannot ask the dictionary
    // anything, because it is no longer looking at it.
    snapshot.Sort(static (left, right) => left.Value.ExpiresAt.CompareTo(right.Value.ExpiresAt));

    var take = Math.Min(batch, snapshot.Count);
    var keys = new Guid[take];
    for (var i = 0; i < take; i++) {
      keys[i] = snapshot[i].Key;
    }
    return keys;
  }

  internal readonly record struct Entry(object Payload, DateTimeOffset ExpiresAt, long Size = 0);
}
