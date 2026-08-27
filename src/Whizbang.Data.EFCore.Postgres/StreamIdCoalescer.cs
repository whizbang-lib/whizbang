using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Turns the <c>List&lt;WorkBatchRow&gt;</c> projected by the <c>claim_work</c> SQL
/// function into the three stream_id lists (<c>perspective</c>, <c>outbox</c>,
/// <c>inbox</c>) consumed by the drain channels.
/// </summary>
/// <remarks>
/// <para>
/// Production forensic fix (v0.657): the pre-v0.657 inline coalesce
/// <c>r.StreamId ?? r.WorkId ?? Guid.Empty</c> only caught NULL — rows whose
/// <c>stream_id = Guid.Empty</c> (real zero-UUID, distinct from NULL) slipped
/// through the <c>??</c> and were then dropped by the <c>.Where(g != Empty)</c>
/// filter. Those rows were claimed every cycle (<c>attempts++</c>) but never
/// reached the drainer — silently stuck forever, no DLQ, no error, no log.
/// </para>
/// <para>
/// This helper treats <see cref="System.Guid.Empty"/> the same as NULL: fall
/// back to <c>WorkId</c> as the singleton-stream identity, emit a Warning
/// naming the offending row. The recovery is unconditional (independent of
/// <see cref="Whizbang.Core.Configuration.EmptyStreamIdPolicy"/>) because the
/// only alternative is "leave the row stuck forever."
/// </para>
/// </remarks>
/// <docs>operations/configuration/empty-stream-id-policy</docs>
internal static partial class StreamIdCoalescer {

  /// <summary>
  /// Projects <paramref name="rows"/> into the three stream-id lists used by the
  /// drain channels. Empty <c>stream_id</c> is coalesced to <c>WorkId</c> with a
  /// Warning emitted via <paramref name="logger"/>; NULL is coalesced silently
  /// (NULL is the legitimate singleton-stream marker).
  /// </summary>
  internal static (
    List<Guid> Perspective,
    List<Guid> Outbox,
    List<Guid> Inbox
  ) Coalesce(List<WorkBatchRow> rows, ILogger? logger) {
    var perspective = new List<Guid>();
    var outbox = new List<Guid>();
    var inbox = new List<Guid>();
    foreach (var r in rows) {
      switch (r.Source) {
        case "perspective_stream":
          if (_tryResolve(r, "perspective", logger, out var pg)) {
            perspective.Add(pg);
          }
          break;
        case "outbox":
          if (_tryResolve(r, "outbox", logger, out var og)) {
            outbox.Add(og);
          }
          break;
        case "inbox":
          if (_tryResolve(r, "inbox", logger, out var ig)) {
            inbox.Add(ig);
          }
          break;
      }
    }
    return (
      perspective.Distinct().ToList(),
      outbox.Distinct().ToList(),
      inbox.Distinct().ToList()
    );
  }

  private static bool _tryResolve(WorkBatchRow r, string source, ILogger? logger, out Guid streamId) {
    if (r.StreamId is { } sid && sid != Guid.Empty) {
      streamId = sid;
      return true;
    }
    var isEmptyNotNull = r.StreamId is { } e && e == Guid.Empty;
    var fallback = r.WorkId ?? Guid.Empty;
    if (fallback == Guid.Empty) {
      streamId = Guid.Empty;
      return false;
    }
    if (isEmptyNotNull && logger is not null) {
      LogEmptyStreamIdFallback((ILogger)logger, r.WorkId ?? Guid.Empty, source);
    }
    streamId = fallback;
    return true;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Empty stream_id (00000000-0000-0000-0000-000000000000) detected on {Source} row {MessageId} — falling back to WorkId as singleton-stream identity. Producer-side fix needed; see operations/configuration/empty-stream-id-policy.")]
  static partial void LogEmptyStreamIdFallback(ILogger logger, Guid messageId, string source);
}
