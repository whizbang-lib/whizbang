using Whizbang.Core.Attributes;

namespace Whizbang.Core.Archival;

/// <summary>
/// A1 (Archival &amp; Compaction) — the occurrence event a "close the books" schedule (F2 temporal engine)
/// fires. The built-in <see cref="ScheduledStreamCloseReceptor"/> reacts to it by calling
/// <see cref="Whizbang.Core.Lifecycle.IStreamCloser.CloseAsync"/> with these parameters. The close-point
/// (<see cref="ThroughVersion"/>) is <strong>domain-owned</strong> — set when the schedule is created and
/// updated per period (e.g. "close the month" advances it each cycle), since the close-point is domain
/// knowledge, like the carry-forward itself.
/// </summary>
/// <remarks>
/// A <see cref="ICommand"/> (imperative "close this stream"), not an event — the target stream is a payload
/// parameter, not the message's own stream, so no <c>[StreamId]</c> resolution is needed. It rides F2's
/// occurrence mechanism (stored + routed with the schedule's own stream id) and is handled by a built-in
/// receptor, exactly like the framework's <c>RebuildPerspectiveCommand</c>.
/// </remarks>
/// <param name="StreamId">The stream to close.</param>
/// <param name="ThroughVersion">The inclusive per-stream version below which detail is truncated.</param>
/// <param name="Archive">Whether to preserve the detail in cold storage before truncating.</param>
/// <docs>fundamentals/events/ephemeral-events</docs>
[PinnedId("b6f3c2a1-7d84-4e59-9c0b-2a1f8e6d3b70")]
public sealed record ScheduledStreamClose(Guid StreamId, long ThroughVersion, bool Archive) : ICommand;
