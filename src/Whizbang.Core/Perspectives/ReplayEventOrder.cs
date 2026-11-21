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
/// An event of an origin that carries no origin position (a redelivery that lost it) is still that origin's event: it is
/// dealt into the origin's slots too, placed among the positioned events by its id, which the origin minted in the order
/// it produced its events (#2379). Kept in its arrival slot instead, an older event redelivered late replayed after the
/// newer one it preceded.
/// </para>
/// <para>
/// Ties break on the id's canonical text, so the order is total and the same on every run.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReplayEventOrderTests.cs</tests>
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
      return [.. events.OrderBy(e => e.LocalStreamVersion!.Value).ThenBy(TieBreak, StringComparer.Ordinal)];
    }
    if (events.All(e => e.LocalCommitSequence.HasValue)) {
      return [.. events.OrderBy(e => e.LocalCommitSequence!.Value).ThenBy(TieBreak, StringComparer.Ordinal)];
    }
    return [.. events.OrderByMessageId()];
  }

  // #2257: each origin's events dealt, in origin order, into the places that origin holds in the local order. #2379: an
  // event of the origin with no origin position takes its place among them by its id, which the origin minted in order.
  private static MessageEnvelope<IEvent>[] _inOriginOrderWithinTheirSlots(MessageEnvelope<IEvent>[] local) =>
    InOriginOrderWithinTheirSlots(local, e => e.SourceServiceId, e => e.SourceCommitSequence, e => e.MessageId.Value);

  /// <summary>
  /// Deals each origin's items, in the origin's order, into the places that origin holds in <paramref name="local"/>
  /// (#2257, #2379). Items whose origin is <see cref="Guid.Empty"/> (local) keep their places, and so does the
  /// interleaving of different origins. The one rule a replay folds by and the received-stream re-sequence (#1629)
  /// stores by, so the two can never disagree.
  /// </summary>
  /// <typeparam name="T">An event, or a stored row standing for one.</typeparam>
  /// <param name="local">The items in local order. Written in place and returned.</param>
  /// <param name="origin">The service the item was received from; <see cref="Guid.Empty"/> for a local item.</param>
  /// <param name="originSequence">Its position at the origin; zero or less when it carries none.</param>
  /// <param name="id">Its id.</param>
  /// <returns><paramref name="local"/>, each origin's items now in the origin's order.</returns>
  internal static T[] InOriginOrderWithinTheirSlots<T>(
      T[] local, Func<T, Guid> origin, Func<T, long> originSequence, Func<T, Guid> id) {
    var slotsByOrigin = new Dictionary<Guid, List<int>>();
    for (var i = 0; i < local.Length; i++) {
      var from = origin(local[i]);
      if (from == Guid.Empty) {
        continue;
      }
      if (!slotsByOrigin.TryGetValue(from, out var slots)) {
        slotsByOrigin[from] = slots = [];
      }
      slots.Add(i);
    }
    var ordered = local;
    foreach (var slots in slotsByOrigin.Values.Where(s => s.Count > 1)) {
      // Read before any write: each origin's slots are its own, so dealing one origin never moves another's events.
      var inOriginOrder = _inOriginOrder(slots.Select(i => local[i]), originSequence, id);
      for (var k = 0; k < slots.Count; k++) {
        ordered[slots[k]] = inOriginOrder[k];
      }
    }
    return ordered;
  }

  /// <summary>
  /// One origin's events in the origin's order (#2257, #2379): those with an origin position by it, ties broken on the
  /// id's canonical text; those without one (a redelivery that lost it) placed among them by their ids, which the origin
  /// minted in the order it produced them. The two are merged on the id: an event without a position goes before the
  /// first positioned event whose id sorts after its own.
  /// </summary>
  /// <param name="events">The events of one origin.</param>
  /// <param name="originSequence">An event's position at the origin.</param>
  /// <param name="id">An event's id.</param>
  /// <returns>The same events in the origin's order.</returns>
  private static List<T> _inOriginOrder<T>(IEnumerable<T> events, Func<T, long> originSequence, Func<T, Guid> id) {
    var positioned = new List<T>();
    var unpositioned = new List<T>();
    foreach (var item in events) {
      (originSequence(item) > 0 ? positioned : unpositioned).Add(item);
    }
    string tieBreak(T item) => id(item).ToString("D");
    var byPosition = positioned.OrderBy(originSequence).ThenBy(tieBreak, StringComparer.Ordinal).ToList();
    var byId = unpositioned.OrderBy(tieBreak, StringComparer.Ordinal).ToList();
    var merged = new List<T>(byPosition.Count + byId.Count);
    int p = 0, u = 0;
    while (p < byPosition.Count || u < byId.Count) {
      var takeUnpositioned = p == byPosition.Count
        || (u < byId.Count && string.CompareOrdinal(tieBreak(byId[u]), tieBreak(byPosition[p])) < 0);
      merged.Add(takeUnpositioned ? byId[u++] : byPosition[p++]);
    }
    return merged;
  }

  /// <summary>The id's canonical text: the tie-break of every ordering here, and the order of an origin's unpositioned events.</summary>
  /// <param name="envelope">The event.</param>
  /// <returns>The event id as lowercase canonical text.</returns>
  internal static string TieBreak(MessageEnvelope<IEvent> envelope) => envelope.MessageId.Value.ToString("D");
}
