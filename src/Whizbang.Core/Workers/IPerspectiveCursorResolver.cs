namespace Whizbang.Core.Workers;

/// <summary>
/// Resolves the current cursor (last applied event id + commit sequence) for a
/// <c>(streamId, perspectiveName)</c> pair — cache first, persisted fallback
/// on a cold lookup. Used by the perspective worker's inversion detector so
/// that out-of-order events trigger rewinds reliably even after process
/// restart, post-rewind cache invalidation, or any other path where the
/// in-memory <see cref="PerspectiveCursorCache"/> is empty for a stream.
/// </summary>
/// <remarks>
/// Without this resolver, <see cref="PerspectiveCursorCache.TryGet"/> returns
/// false on a cold lookup, the inversion detector compares against a null
/// cursor and can never fire, and a late event slips into the runner's
/// forward-apply path. The runner's own idempotency filter then reads the
/// persisted <c>metadata.EventId</c> (which IS current) and drops the event
/// — silently, because <c>wh_perspective_events.processed_at</c> gets stamped
/// and the event_work_id is gone. This is the JDX bulk-import
/// "N lost events, 0 rewinds" symptom.
/// </remarks>
/// <docs>fundamentals/perspectives/cursor-inversion</docs>
public interface IPerspectiveCursorResolver {
  /// <summary>
  /// Returns the current cursor for the given perspective on the given stream.
  /// Hits the in-memory cache when populated; otherwise reads from the work
  /// coordinator (joining <c>wh_perspective_cursors</c> to
  /// <c>wh_event_store</c> for the commit sequence) and warms the cache.
  /// </summary>
  /// <param name="streamId">Stream id.</param>
  /// <param name="perspectiveName">Perspective name (already normalized).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>(LastEventId, LastCommitSequence) — both nullable when the
  /// perspective has never advanced on this stream.</returns>
  Task<(Guid? LastEventId, long? LastCommitSequence)> GetAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default);
}
