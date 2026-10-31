// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The one place that decides which perspective row a rebuild folds an event onto, and in what order a row that
/// receives events from more than one stream folds them (#1798, #1820).
/// </summary>
/// <remarks>
/// <para>
/// Every path keys a row by the stream an event is stored on: live apply, the rewind and a rebuild. The one exception is
/// a genuine re-key, an upcaster that moved an event's <c>[StreamId]</c> as it was read (<see cref="EventRekeys"/>): a
/// rebuild folds such an event onto the row it now names. A payload that merely names another stream, with no upcaster
/// involved, is not a re-key and stays on the row of the stream it is stored on.
/// </para>
/// <para>
/// A row that receives re-keyed events is folded from its own stream's events (less any an upcaster moved away) and
/// every re-keyed event the rebuild read for it, merged by <see cref="Merge"/>. It was once folded from the re-keyed
/// events alone, starting from an empty model, so a target stream's own history was lost, and of two source streams
/// re-keyed onto one target the one rebuilt last won.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:Rebuild_FoldsEachStoredEventOntoTheRowLiveApplyKeysItByAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/RekeyThroughRebuildTests.cs</tests>
public static class RebuildRowRouting {
  /// <summary>
  /// The row an event read from <paramref name="storedOn"/> folds onto during a rebuild: the stream an upcaster
  /// re-keyed it to, or the stream it is stored on.
  /// </summary>
  /// <param name="envelope">The event as the upcasting read returned it.</param>
  /// <param name="storedOn">The stream it is stored on.</param>
  /// <returns>The row's stream.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:RowOf_OnlyAGenuineRekeyLeavesTheStoredStreamAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:Rebuild_FoldsEachStoredEventOntoTheRowLiveApplyKeysItByAsync</tests>
  public static Guid RowOf(MessageEnvelope<IEvent> envelope, Guid storedOn) {
    ArgumentNullException.ThrowIfNull(envelope);
    return EventRekeys.RekeyedTo(envelope.Payload) is { } to && to != Guid.Empty ? to : storedOn;
  }

  /// <summary>
  /// Splits the events read from <paramref name="storedOn"/> by the row each folds onto (<see cref="RowOf"/>), in the
  /// order each row first appears, keeping read order within each.
  /// </summary>
  /// <param name="storedOn">The stream the events were read from.</param>
  /// <param name="events">The events, as the upcasting read returned them.</param>
  /// <returns>One partition per row.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:Partition_KeepsReadOrderPerRowAsync</tests>
  public static IReadOnlyList<RebuildPartition> Partition(Guid storedOn, IReadOnlyList<MessageEnvelope<IEvent>> events) {
    ArgumentNullException.ThrowIfNull(events);
    var partitions = new List<RebuildPartition>();
    var byRow = new Dictionary<Guid, List<MessageEnvelope<IEvent>>>();
    foreach (var envelope in events) {
      var row = RowOf(envelope, storedOn);
      if (!byRow.TryGetValue(row, out var bucket)) {
        bucket = [];
        byRow[row] = bucket;
        partitions.Add(new RebuildPartition(row, storedOn, bucket));
      }
      bucket.Add(envelope);
    }
    return partitions;
  }

  /// <summary>
  /// The events a row folds from its own stream: those read from it that an upcaster did not move to another row.
  /// </summary>
  /// <param name="row">The row's stream.</param>
  /// <param name="storedOnRow">The events read from that stream.</param>
  /// <returns>The events that stay on the row, in read order.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:StaysOn_DropsTheEventsRekeyedAwayAsync</tests>
  public static IReadOnlyList<MessageEnvelope<IEvent>> StaysOn(Guid row, IReadOnlyList<MessageEnvelope<IEvent>> storedOnRow) {
    ArgumentNullException.ThrowIfNull(storedOnRow);
    return [.. storedOnRow.Where(e => RowOf(e, row) == row)];
  }

  /// <summary>
  /// Merges the events a row receives from several streams into the one order it folds them in. Each stream keeps its
  /// own replay order (<see cref="ReplayEventOrder.Order"/>); across streams the event committed first goes first. A
  /// batch from one origin keeps the origin's order, as a single stream's replay does.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Stream versions only order events of one stream, and an event id records when its producer minted it, not when the
  /// store committed it, so neither orders events across streams. The commit sequence does: across streams the head
  /// with the lower commit sequence goes first, and a head with none waits behind every head that has one. Heads that
  /// cannot be told apart keep the order the streams were given in, so the merge is the same on every run.
  /// </para>
  /// <para>
  /// The result is a <see cref="MergedReplayBatch"/>, which <see cref="ReplayEventOrder.Order"/> returns as it is: a
  /// per-stream order applied to the whole batch would sort it by versions of different streams.
  /// </para>
  /// </remarks>
  /// <param name="streams">Each contributing stream's events; empty ones are ignored.</param>
  /// <returns>The merged batch.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:Merge_InterleavesStreamsByCommitSequenceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:Merge_SingleOriginKeepsTheOriginsOrderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:Merge_UnstampedHeadsWaitBehindStampedOnesAsync</tests>
  public static MergedReplayBatch Merge(IReadOnlyList<IReadOnlyList<MessageEnvelope<IEvent>>> streams) {
    ArgumentNullException.ThrowIfNull(streams);
    var ordered = streams.Where(s => s.Count > 0).Select(ReplayEventOrder.Order).ToList();
    var all = ordered.SelectMany(s => s).ToList();
    if (ordered.Count < 2) {
      return new MergedReplayBatch(all);
    }
    var firstOrigin = all[0].SourceServiceId;
    if (firstOrigin != Guid.Empty && all.All(e => e.SourceServiceId == firstOrigin && e.SourceCommitSequence > 0)) {
      return new MergedReplayBatch(ReplayEventOrder.Order(all));
    }
    var merged = new List<MessageEnvelope<IEvent>>(all.Count);
    var heads = new int[ordered.Count];
    while (merged.Count < all.Count) {
      var pick = -1;
      for (var s = 0; s < ordered.Count; s++) {
        if (heads[s] < ordered[s].Count && (pick < 0 || _before(ordered[s][heads[s]], ordered[pick][heads[pick]]))) {
          pick = s;
        }
      }
      merged.Add(ordered[pick][heads[pick]++]);
    }
    return new MergedReplayBatch(merged);
  }

  /// <summary>Whether <paramref name="candidate"/> was committed strictly before <paramref name="current"/>.</summary>
  private static bool _before(MessageEnvelope<IEvent> candidate, MessageEnvelope<IEvent> current) =>
    candidate.LocalCommitSequence is long c && (current.LocalCommitSequence is not long p || c < p);
}

/// <summary>The events a rebuild read from one stream that fold onto one row.</summary>
/// <param name="Row">The row's stream.</param>
/// <param name="StoredOn">The stream they were read from.</param>
/// <param name="Events">The events, in read order.</param>
public sealed record RebuildPartition(Guid Row, Guid StoredOn, IReadOnlyList<MessageEnvelope<IEvent>> Events) {
  /// <summary>True when these events were re-keyed off the stream they were read from.</summary>
  public bool IsRekeyed => Row != StoredOn;
}

/// <summary>
/// Events from several streams already in the order one row folds them in (<see cref="RebuildRowRouting.Merge"/>).
/// <see cref="ReplayEventOrder.Order"/> returns it as it is.
/// </summary>
public sealed class MergedReplayBatch : IReadOnlyList<MessageEnvelope<IEvent>> {
  private readonly IReadOnlyList<MessageEnvelope<IEvent>> _events;

  internal MergedReplayBatch(IReadOnlyList<MessageEnvelope<IEvent>> events) => _events = events;

  /// <inheritdoc />
  public MessageEnvelope<IEvent> this[int index] => _events[index];

  /// <inheritdoc />
  public int Count => _events.Count;

  /// <inheritdoc />
  public IEnumerator<MessageEnvelope<IEvent>> GetEnumerator() => _events.GetEnumerator();

  global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
