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
/// Two steps. First the batch takes its <b>local order</b>, one comparator per batch, chosen by what the batch can prove:
/// </para>
/// <list type="number">
/// <item><description><b>Stream order</b>, when every event carries the version the local store appended it
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
/// Then <b>origin order within each origin's slots</b> (#2257). An event received from another service carries its
/// origin (<see cref="MessageEnvelope{T}.SourceServiceId"/>, non-empty) and its position there
/// (<see cref="MessageEnvelope{T}.SourceCommitSequence"/>, positive). A receiver stores events in arrival order, which
/// delivery across topics and partitions, and integrity redelivery, do not keep in the origin's order: an update the
/// origin committed before a delete can arrive after it. So the places each origin's events hold in the local order are
/// kept, and the origin's events are dealt into those places in origin order. Local and origin-less events keep their
/// places, and the interleaving of different origins stays the arrival order, the only order the receiver knows between
/// them. A stream with one origin throughout therefore replays in pure origin order, and a stream with no origins in
/// pure local order.
/// </para>
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
    return _inOriginOrderWithinTheirSlots(_localOrder(events));
  }

  private static MessageEnvelope<IEvent>[] _localOrder(IReadOnlyList<MessageEnvelope<IEvent>> events) {
    if (events.All(e => e.LocalStreamVersion.HasValue)) {
      return [.. events.OrderBy(e => e.LocalStreamVersion!.Value).ThenBy(_tieBreak, StringComparer.Ordinal)];
    }
    if (events.All(e => e.LocalCommitSequence.HasValue)) {
      return [.. events.OrderBy(e => e.LocalCommitSequence!.Value).ThenBy(_tieBreak, StringComparer.Ordinal)];
    }
    return [.. events.OrderByMessageId()];
  }

  // #2257: each origin's events dealt, in origin order, into the places that origin holds in the local order.
  private static MessageEnvelope<IEvent>[] _inOriginOrderWithinTheirSlots(MessageEnvelope<IEvent>[] local) {
    var slotsByOrigin = new Dictionary<Guid, List<int>>();
    for (var i = 0; i < local.Length; i++) {
      var origin = local[i].SourceServiceId;
      if (origin == Guid.Empty || local[i].SourceCommitSequence <= 0) {
        continue;
      }
      if (!slotsByOrigin.TryGetValue(origin, out var slots)) {
        slotsByOrigin[origin] = slots = [];
      }
      slots.Add(i);
    }
    var ordered = local;
    foreach (var slots in slotsByOrigin.Values.Where(s => s.Count > 1)) {
      // Read before any write: each origin's slots are its own, so dealing one origin never moves another's events.
      var inOriginOrder = slots.Select(i => local[i])
        .OrderBy(e => e.SourceCommitSequence).ThenBy(_tieBreak, StringComparer.Ordinal).ToList();
      for (var k = 0; k < slots.Count; k++) {
        ordered[slots[k]] = inOriginOrder[k];
      }
    }
    return ordered;
  }

  private static string _tieBreak(MessageEnvelope<IEvent> envelope) => envelope.MessageId.Value.ToString("D");
}
