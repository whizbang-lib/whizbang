namespace Whizbang.Core.Workers;

/// <summary>
/// The key a fetched outbox/inbox row is drained under.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the WHERE clause shared by <c>fetch_outbox_batch</c> and <c>fetch_inbox_batch</c>:
/// a routable row matches on <c>stream_id</c>, while a non-routable one — <c>stream_id</c> either
/// NULL (the documented singleton-stream marker) or <see cref="Guid.Empty"/> (a producer writing
/// the default instead of NULL) — matches on <c>message_id</c> instead. Both sentinels must map to
/// the message_id, or a fetched row is filed under a key no caller looks up and is never dispatched.
/// </para>
/// <para>
/// Shared deliberately. The two drain workers are documented mirrors and have diverged twice —
/// once on batched fetching, once on this key — so the rule lives in one place rather than being
/// restated on each side.
/// </para>
/// </remarks>
internal static class DrainKey {
  /// <summary>Resolves the drain key for a row's stream id and message id.</summary>
  public static Guid For(Guid? streamId, Guid messageId) =>
    streamId is { } s && s != Guid.Empty ? s : messageId;
}
