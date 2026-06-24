using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Thread-safe in-memory cache of perspective cursor positions.
/// Eliminates redundant GetPerspectiveCursorAsync DB calls in drain mode.
/// Updated after each successful RunWithEventsAsync. Invalidated on rewind/rebuild.
/// </summary>
/// <remarks>
/// <para>Keys use (StreamId, PerspectiveName) where PerspectiveName is normalized via TypeNameFormatter.Format.
/// Strong-typed generic methods provide compile-time safety at call sites.
/// String-keyed methods available for internal use where perspective name is already known.</para>
/// <para>Activity-triggered eviction: every access (read or write) stamps the stream's last-activity
/// timestamp; on every access (throttled by <see cref="PerspectiveStreamAffinityOptions.SweepInterval"/>)
/// the cache evicts entries for streams idle longer than
/// <see cref="PerspectiveStreamAffinityOptions.IdleEvictionWindow"/>. On eviction, the cache raises
/// <see cref="OnStreamsEvicted"/> with the evicted stream IDs so paired caches (e.g. the per-stream
/// affinity gate dictionary in <see cref="PerspectiveWorker"/>) can drop their own entries for the
/// same streams without each running its own time-based sweep.</para>
/// </remarks>
public sealed class PerspectiveCursorCache {
  private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), Guid?> _cache = new();

  /// <summary>
  /// Slice 26 — parallel commit_sequence cache. Populated by drain-mode worker alongside
  /// the event_id cache. Inversion detection prefers commit_sequence comparison when both
  /// the cached cursor and the incoming events have it populated; falls back to event_id
  /// when either is missing (e.g., stamper hasn't caught up, or rows pre-date slice 26).
  /// </summary>
  private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), long?> _commitSeqCache = new();

  /// <summary>
  /// Per-stream last-activity tick. Updated on every read/write that touches the cache;
  /// drives the activity-triggered sweep. Per-stream (not per-(stream, perspective)) so
  /// the eviction notification is naturally stream-scoped — matches how downstream
  /// subscribers (the affinity-gate dict) think about lifecycle.
  /// </summary>
  private readonly ConcurrentDictionary<Guid, long> _streamLastActivityTicks = new();

  private readonly PerspectiveStreamAffinityOptions _options;
  private readonly TimeProvider _timeProvider;
  private long _lastSweepTicks;

  /// <summary>
  /// Raised after a sweep evicts entries for one or more streams. The list contains every
  /// stream id that had at least one (stream, perspective) entry removed during this sweep,
  /// deduplicated. Subscribers should be cheap and non-throwing; the cache logs and continues
  /// on subscriber exceptions so a faulty hook can't break the eviction pass.
  /// </summary>
  public event Action<IReadOnlyList<Guid>>? OnStreamsEvicted;

  /// <summary>
  /// Default constructor — wire defaults for <see cref="PerspectiveStreamAffinityOptions"/>.
  /// Preserves the historical no-arg construction used by <see cref="PerspectiveWorker"/>.
  /// </summary>
  public PerspectiveCursorCache() : this(new PerspectiveStreamAffinityOptions(), TimeProvider.System) { }

  /// <summary>
  /// Constructor accepting a shared <see cref="PerspectiveStreamAffinityOptions"/> instance.
  /// The intent is one options instance per PerspectiveWorker, shared between the cursor
  /// cache and the affinity-gate dict so both honor the same idle/sweep tuning.
  /// </summary>
  public PerspectiveCursorCache(PerspectiveStreamAffinityOptions options) : this(options, TimeProvider.System) { }

  /// <summary>
  /// Test-friendly constructor accepting a <see cref="TimeProvider"/>. Production callers
  /// use the parameterless or options-only overloads above (both wire
  /// <see cref="TimeProvider.System"/>); tests inject a fake time provider so eviction
  /// behavior can be exercised deterministically without wall-clock <c>Task.Delay</c>.
  /// </summary>
  public PerspectiveCursorCache(PerspectiveStreamAffinityOptions options, TimeProvider timeProvider) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _options = options;
    _timeProvider = timeProvider;
    _lastSweepTicks = _timeProvider.GetUtcNow().Ticks;
  }

  /// <summary>Number of cached cursor entries.</summary>
  public int Count => _cache.Count;

  /// <summary>
  /// Gets the cached cursor position for a (stream, perspective) pair.
  /// </summary>
  /// <typeparam name="TPerspective">The perspective type (compile-time safe).</typeparam>
  /// <param name="streamId">Stream to look up.</param>
  /// <param name="lastEventId">The cached last processed event ID, or null if not cached.</param>
  /// <returns>True if the cursor was cached; false if a DB lookup is needed.</returns>
  public bool TryGet<TPerspective>(Guid streamId, out Guid? lastEventId) {
    _touch(streamId);
    return _cache.TryGetValue((streamId, TypeNameFormatter.Format(typeof(TPerspective))), out lastEventId);
  }

  /// <summary>
  /// Gets the cached cursor position by perspective name string.
  /// </summary>
  public bool TryGet(Guid streamId, string perspectiveName, out Guid? lastEventId) {
    _touch(streamId);
    return _cache.TryGetValue((streamId, perspectiveName), out lastEventId);
  }

  /// <summary>
  /// Sets the cached cursor position after a successful perspective run.
  /// </summary>
  /// <typeparam name="TPerspective">The perspective type (compile-time safe).</typeparam>
  public void Set<TPerspective>(Guid streamId, Guid? lastEventId) {
    _touch(streamId);
    _cache[(streamId, TypeNameFormatter.Format(typeof(TPerspective)))] = lastEventId;
  }

  /// <summary>
  /// Sets the cached cursor position by perspective name string.
  /// </summary>
  public void Set(Guid streamId, string perspectiveName, Guid? lastEventId) {
    _touch(streamId);
    _cache[(streamId, perspectiveName)] = lastEventId;
  }

  /// <summary>
  /// Invalidates a single (stream, perspective) cache entry.
  /// Called when a perspective rewind is triggered.
  /// </summary>
  /// <typeparam name="TPerspective">The perspective type to invalidate.</typeparam>
  public void Invalidate<TPerspective>(Guid streamId) {
    _cache.TryRemove((streamId, TypeNameFormatter.Format(typeof(TPerspective))), out _);
  }

  /// <summary>
  /// Invalidates a single (stream, perspective) cache entry by name.
  /// </summary>
  public void Invalidate(Guid streamId, string perspectiveName) {
    _cache.TryRemove((streamId, perspectiveName), out _);
    _commitSeqCache.TryRemove((streamId, perspectiveName), out _);
  }

  /// <summary>
  /// Slice 26 — gets the cached commit_sequence cursor for a (stream, perspective) pair.
  /// </summary>
  /// <param name="streamId">Stream to look up.</param>
  /// <param name="perspectiveName">Perspective name (already normalized via TypeNameFormatter).</param>
  /// <param name="lastCommitSequence">The cached last applied commit_sequence, or null if not cached.</param>
  /// <returns>True if a commit_sequence cursor was cached; false if not.</returns>
  public bool TryGetCommitSequence(Guid streamId, string perspectiveName, out long? lastCommitSequence) {
    _touch(streamId);
    return _commitSeqCache.TryGetValue((streamId, perspectiveName), out lastCommitSequence);
  }

  /// <summary>
  /// Slice 26 — sets the cached commit_sequence cursor. Called alongside <see cref="Set(Guid,string,Guid?)"/>
  /// after a successful apply when the event has a known commit_sequence stamp.
  /// </summary>
  public void SetCommitSequence(Guid streamId, string perspectiveName, long? lastCommitSequence) {
    _touch(streamId);
    _commitSeqCache[(streamId, perspectiveName)] = lastCommitSequence;
  }

  /// <summary>
  /// Invalidates ALL cached cursors for a stream (all perspectives).
  /// Called when a stream is rebuilt or all its perspectives need reprocessing.
  /// </summary>
  public void InvalidateStream(Guid streamId) {
    var keysToRemove = _cache.Keys.Where(k => k.StreamId == streamId).ToList();
    foreach (var key in keysToRemove) {
      _cache.TryRemove(key, out _);
      _commitSeqCache.TryRemove(key, out _);
    }
    _streamLastActivityTicks.TryRemove(streamId, out _);
  }

  /// <summary>
  /// Checks if ANY cursor for this stream is cached.
  /// Used by drain mode to skip batch cursor fetch for streams already in cache.
  /// </summary>
  public bool HasStream(Guid streamId) {
    return _cache.Keys.Any(k => k.StreamId == streamId);
  }

  /// <summary>
  /// Clears all cached cursors. Called on full rebuild or worker restart.
  /// </summary>
  public void Clear() {
    _cache.Clear();
    _commitSeqCache.Clear();
    _streamLastActivityTicks.Clear();
  }

  /// <summary>
  /// Test helper — runs the sweep immediately regardless of throttle state, returns the
  /// stream ids that were evicted. Production code relies on the activity-triggered path.
  /// </summary>
  public IReadOnlyList<Guid> RunSweepNowForTests() => _sweepCore();

  /// <summary>
  /// Stamps the stream's last-activity tick and triggers a sweep if at least
  /// <see cref="PerspectiveStreamAffinityOptions.SweepInterval"/> has elapsed since the
  /// previous one.
  /// </summary>
  private void _touch(Guid streamId) {
    var nowTicks = _timeProvider.GetUtcNow().Ticks;
    _streamLastActivityTicks[streamId] = nowTicks;
    _sweepIfDue(nowTicks);
  }

  private void _sweepIfDue(long nowTicks) {
    var prevSweepTicks = Interlocked.Read(ref _lastSweepTicks);
    if (nowTicks - prevSweepTicks < _options.SweepInterval.Ticks) {
      return;
    }
    // Single CAS so only one releaser performs the sweep per interval.
    if (Interlocked.CompareExchange(ref _lastSweepTicks, nowTicks, prevSweepTicks) != prevSweepTicks) {
      return;
    }
    var evicted = _sweepCore();
    if (evicted.Count > 0) {
      _raiseEvicted(evicted);
    }
  }

  /// <summary>
  /// Walks per-stream last-activity and removes (stream, perspective) entries for any
  /// stream idle for longer than <see cref="PerspectiveStreamAffinityOptions.IdleEvictionWindow"/>.
  /// Returns the deduplicated set of evicted stream ids.
  /// </summary>
  private List<Guid> _sweepCore() {
    var nowTicks = _timeProvider.GetUtcNow().Ticks;
    var idleCutoffTicks = nowTicks - _options.IdleEvictionWindow.Ticks;
    var evictedStreams = new List<Guid>();
    foreach (var (streamId, lastActivity) in _streamLastActivityTicks) {
      if (lastActivity > idleCutoffTicks) {
        continue;
      }
      // Defensive re-read: a racing _touch between the foreach pickup and here means we
      // should NOT evict this stream — the subsequent acquire/access already moved the
      // activity tick forward, so the stream is fresh again.
      if (_streamLastActivityTicks.TryGetValue(streamId, out var liveActivity) && liveActivity > idleCutoffTicks) {
        continue;
      }
      _streamLastActivityTicks.TryRemove(streamId, out _);
      var keysToRemove = _cache.Keys.Where(k => k.StreamId == streamId).ToList();
      foreach (var key in keysToRemove) {
        _cache.TryRemove(key, out _);
        _commitSeqCache.TryRemove(key, out _);
      }
      evictedStreams.Add(streamId);
    }
    return evictedStreams;
  }

  private void _raiseEvicted(IReadOnlyList<Guid> evicted) {
    var handler = OnStreamsEvicted;
    if (handler is null) {
      return;
    }
    // Snapshot subscriber list to keep the invocation lock-free; each subscriber is invoked
    // independently with try/catch so a faulty hook can't break the eviction pass for others.
    foreach (var subscriber in handler.GetInvocationList().Cast<Action<IReadOnlyList<Guid>>>()) {
      try {
        subscriber(evicted);
      } catch {
        // Subscriber threw — swallow so other subscribers + the cache itself stay healthy.
        // The cache has no ILogger today; surface logging in a follow-up if subscribers
        // turn out to need diagnostics on hook failures.
      }
    }
  }
}
