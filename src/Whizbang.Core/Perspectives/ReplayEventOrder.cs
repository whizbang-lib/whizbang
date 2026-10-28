// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The order a replay folds a stream's events in. Used by every path that re-derives a row from the stream it
/// read: the rewind and the rebuild.
/// </summary>
/// <remarks>
/// <para>
/// One comparator per batch, chosen by what the batch can prove:
/// </para>
/// <list type="number">
/// <item><description>A <b>single-origin</b> stream, where every event carries the same non-empty
/// <see cref="MessageEnvelope{T}.SourceServiceId"/> and a positive
/// <see cref="MessageEnvelope{T}.SourceCommitSequence"/>, replays in origin order. A reconciled straggler keeps
/// its original origin slot but lands with an arrival-fresh local sequence, and a local-order replay would apply
/// it last.</description></item>
/// <item><description>Otherwise <b>stream order</b>, when every event carries the version the local store appended it
/// at (<see cref="MessageEnvelope{T}.LocalStreamVersion"/>). That is the order the stream was written in and the
/// order the live apply folded it in. A commit sequence only records when the stamper reached a row: rows stamped
/// after the fact carry sequences unrelated to their versions, and folding by them applied a stream's last event
/// first, so a rebuild or a rewind left the row holding an older value (#1352).</description></item>
/// <item><description>Otherwise <b>commit order</b>, when every event carries its local stamp. An event id is
/// minted by whoever produced the event and records when, not when the stream stored it: a seed, an import or a
/// slow transaction can commit an event whose id sorts after events committed later. Folding by id then applies
/// the stream's first event last and checkpoints on it (#1331).</description></item>
/// <item><description>Any unstamped event (stamper lag) falls the whole batch back to id order, because a partial
/// mix would interleave two orderings.</description></item>
/// </list>
/// <para>
/// Ties break on the id's canonical text, so the order is total and the same on every run.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/ReplayEventOrderTests.cs</tests>
public static class ReplayEventOrder {
  /// <summary>
  /// Returns <paramref name="events"/> in replay order. A batch of zero or one event is returned as it is.
  /// </summary>
  /// <param name="events">The events read for one stream.</param>
  /// <returns>The same events, ordered for folding.</returns>
  public static IReadOnlyList<MessageEnvelope<IEvent>> Order(IReadOnlyList<MessageEnvelope<IEvent>> events) {
    ArgumentNullException.ThrowIfNull(events);
    // A row folded from several streams arrives merged across them (RebuildRowRouting.Merge); ordering it as one
    // stream would sort it by versions of different streams (#1820).
    if (events.Count < 2 || events is MergedReplayBatch) {
      return events;
    }
    var firstOrigin = events[0].SourceServiceId;
    if (firstOrigin != Guid.Empty
        && events.All(e => e.SourceServiceId == firstOrigin && e.SourceCommitSequence > 0)) {
      return [.. events.OrderBy(e => e.SourceCommitSequence).ThenBy(_tieBreak, StringComparer.Ordinal)];
    }
    if (events.All(e => e.LocalStreamVersion.HasValue)) {
      return [.. events.OrderBy(e => e.LocalStreamVersion!.Value).ThenBy(_tieBreak, StringComparer.Ordinal)];
    }
    if (events.All(e => e.LocalCommitSequence.HasValue)) {
      return [.. events.OrderBy(e => e.LocalCommitSequence!.Value).ThenBy(_tieBreak, StringComparer.Ordinal)];
    }
    return [.. events.OrderByMessageId()];
  }

  private static string _tieBreak(MessageEnvelope<IEvent> envelope) => envelope.MessageId.Value.ToString("D");
}
