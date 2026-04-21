using System.Collections.Concurrent;

namespace Whizbang.Core.Workers;

/// <summary>
/// Thread-safe in-memory cache of perspective cursor positions.
/// Eliminates redundant GetPerspectiveCursorAsync DB calls in drain mode.
/// Updated after each successful RunWithEventsAsync. Invalidated on rewind/rebuild.
/// </summary>
/// <remarks>
/// Keys use (StreamId, PerspectiveName) where PerspectiveName is normalized via TypeNameFormatter.Format.
/// Strong-typed generic methods provide compile-time safety at call sites.
/// String-keyed methods available for internal use where perspective name is already known.
/// </remarks>
public sealed class PerspectiveCursorCache {
  private readonly ConcurrentDictionary<(Guid StreamId, string PerspectiveName), Guid?> _cache = new();

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
    return _cache.TryGetValue((streamId, TypeNameFormatter.Format(typeof(TPerspective))), out lastEventId);
  }

  /// <summary>
  /// Gets the cached cursor position by perspective name string.
  /// </summary>
  public bool TryGet(Guid streamId, string perspectiveName, out Guid? lastEventId) {
    return _cache.TryGetValue((streamId, perspectiveName), out lastEventId);
  }

  /// <summary>
  /// Sets the cached cursor position after a successful perspective run.
  /// </summary>
  /// <typeparam name="TPerspective">The perspective type (compile-time safe).</typeparam>
  public void Set<TPerspective>(Guid streamId, Guid? lastEventId) {
    _cache[(streamId, TypeNameFormatter.Format(typeof(TPerspective)))] = lastEventId;
  }

  /// <summary>
  /// Sets the cached cursor position by perspective name string.
  /// </summary>
  public void Set(Guid streamId, string perspectiveName, Guid? lastEventId) {
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
  }

  /// <summary>
  /// Invalidates ALL cached cursors for a stream (all perspectives).
  /// Called when a stream is rebuilt or all its perspectives need reprocessing.
  /// </summary>
  public void InvalidateStream(Guid streamId) {
    var keysToRemove = _cache.Keys.Where(k => k.StreamId == streamId).ToList();
    foreach (var key in keysToRemove) {
      _cache.TryRemove(key, out _);
    }
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
  }
}
