// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The stored order a received stream should have (#1629): each origin's events in that origin's order, dealt into the
/// versions they already hold; local events and the interleaving of origins stay. Every case is built so the stored
/// order disagrees with the origin's, and the last proves the rule is the replay's own over every arrangement.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/ReceivedStreamOrder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/ReplayEventOrder.cs</code-under-test>
public class ReceivedStreamOrderTests {
  private static readonly Guid _originA = Guid.Parse("0199a000-0000-7000-8000-00000000000a");
  private static readonly Guid _originB = Guid.Parse("0199a000-0000-7000-8000-00000000000b");

  // Ascending ids: _id(1) < _id(2) < _id(3).
  private static Guid _id(int n) => Guid.Parse($"0199b000-0000-7000-8000-{n:D12}");

  private static StoredStreamEvent _local(int id, long version) => new(_id(id), version, Guid.Empty, 0);

  private static StoredStreamEvent _from(Guid origin, int id, long version, long sequence = 0) => new(_id(id), version, origin, sequence);

  /// <summary>The event ids by version after the moves are applied.</summary>
  private static Guid[] _storedOrderAfter(IReadOnlyList<StoredStreamEvent> stream, IReadOnlyList<ResequencedEvent> moves) {
    var to = moves.ToDictionary(m => m.EventId, m => m.ToVersion);
    return [.. stream.OrderBy(e => to.TryGetValue(e.EventId, out var v) ? v : e.Version).Select(e => e.EventId)];
  }

  private static async Task _assertStoredOrderAsync(IReadOnlyList<StoredStreamEvent> stream, params int[] ids) {
    var moves = ReceivedStreamOrder.Resequence(stream);
    await Assert.That(_storedOrderAfter(stream, moves))
      .IsEquivalentTo(ids.Select(_id).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Resequence_ASingleOriginStoredAgainstItsSequences_IsStoredInTheOriginsOrderAsync() {
    // The origin committed 1, 2, 3; the receiver stored them 3, 1, 2.
    StoredStreamEvent[] stream = [_from(_originA, 3, version: 1, sequence: 30), _from(_originA, 1, version: 2, sequence: 10), _from(_originA, 2, version: 3, sequence: 20)];

    await _assertStoredOrderAsync(stream, 1, 2, 3);
  }

  [Test]
  public async Task Resequence_FanOutChildrenWithoutSequences_AreStoredInTheirIdOrder_TheInitializerFirstAsync() {
    // #1607's shape: the stream's initializer (the lowest id, minted first) was stored after the field events.
    StoredStreamEvent[] stream = [_from(_originA, 2, version: 1), _from(_originA, 3, version: 2), _from(_originA, 1, version: 3)];

    var moves = ReceivedStreamOrder.Resequence(stream);

    await Assert.That(moves).IsEquivalentTo([
      new ResequencedEvent(_id(1), 3, 1), new ResequencedEvent(_id(2), 1, 2), new ResequencedEvent(_id(3), 2, 3)],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Resequence_AMixedStream_ReordersTheOriginsEventsInTheirOwnSlots_AndLeavesLocalEventsInPlaceAsync() {
    // Versions: 1 local, 2 origin(seq 20), 3 local, 4 origin(seq 10). The origin's slots are 2 and 4.
    StoredStreamEvent[] stream = [_local(5, 1), _from(_originA, 2, version: 2, sequence: 20), _local(6, 3), _from(_originA, 1, version: 4, sequence: 10)];

    var moves = ReceivedStreamOrder.Resequence(stream);

    await Assert.That(moves.Select(m => m.EventId)).DoesNotContain(_id(5)).And.DoesNotContain(_id(6));
    await _assertStoredOrderAsync(stream, 5, 1, 6, 2);
  }

  [Test]
  public async Task Resequence_SeveralOrigins_FixesEachOnesOrder_AndKeepsTheirInterleavingAsync() {
    // A holds slots 1 and 3, B holds 2 and 4: both stored backward. Slot ownership (A, B, A, B) must not change.
    StoredStreamEvent[] stream = [
      _from(_originA, 2, version: 1, sequence: 2), _from(_originB, 4, version: 2, sequence: 9),
      _from(_originA, 1, version: 3, sequence: 1), _from(_originB, 3, version: 4, sequence: 8)];

    await _assertStoredOrderAsync(stream, 1, 3, 2, 4);
  }

  [Test]
  public async Task Resequence_AStreamAlreadyInItsOriginsOrder_MovesNothingAsync() {
    StoredStreamEvent[] stream = [_local(9, 1), _from(_originA, 1, version: 2, sequence: 1), _from(_originB, 2, version: 3), _from(_originA, 3, version: 4, sequence: 2)];

    await Assert.That(ReceivedStreamOrder.Resequence(stream)).IsEmpty();
  }

  [Test]
  public async Task Resequence_KeepsTheStreamsVersionSet_GapsIncluded_AndReadsTheStreamInAnyOrderAsync() {
    // Versions 3, 7, 9 (a stream whose earlier events were removed), handed over unsorted.
    StoredStreamEvent[] stream = [_from(_originA, 1, version: 9, sequence: 1), _from(_originA, 3, version: 3, sequence: 3), _from(_originA, 2, version: 7, sequence: 2)];

    var moves = ReceivedStreamOrder.Resequence(stream);

    await Assert.That(moves.Select(m => m.ToVersion).Order()).IsEquivalentTo(moves.Select(m => m.FromVersion).Order(),
      TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("versions are only permuted among the events that move");
    await _assertStoredOrderAsync(stream, 1, 2, 3);
    await Assert.That(moves.Single(m => m.EventId == _id(1)).ToVersion).IsEqualTo(3L);
  }

  [Test]
  public async Task Resequence_AnUnpositionedEvent_IsPlacedAmongThePositionedOnesByItsIdAsync() {
    // Positioned 1 (seq 1) and 3 (seq 2), unpositioned 2: its id puts it between them.
    StoredStreamEvent[] stream = [_from(_originA, 3, version: 1, sequence: 2), _from(_originA, 2, version: 2), _from(_originA, 1, version: 3, sequence: 1)];

    await _assertStoredOrderAsync(stream, 1, 2, 3);
  }

  [Test]
  public async Task Resequence_ANullStream_IsRefusedAsync() {
    await Assert.That(() => ReceivedStreamOrder.Resequence(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Resequence_OverEveryArrangement_StoresExactlyTheOrderAReplayFoldsAsync() {
    // The design choice this locks: the stored order after the re-sequence is the replay's order (ReplayEventOrder), so
    // folding by version and folding in replay order are one fold. Every arrangement of six events (two origins with and
    // without sequences, and a local one) is stored at every permutation of its versions.
    StoredStreamEvent[] events = [
      _from(_originA, 1, 0, sequence: 5), _from(_originA, 4, 0, sequence: 2), _from(_originA, 2, 0),
      _from(_originB, 6, 0, sequence: 1), _from(_originB, 3, 0), _local(5, 0)];
    var checkedArrangements = 0;
    foreach (var versions in _permutations([1, 2, 3, 4, 5, 6])) {
      var stream = events.Select((e, i) => e with { Version = versions[i] }).ToArray();
      var moves = ReceivedStreamOrder.Resequence(stream);
      var to = moves.ToDictionary(m => m.EventId, m => m.ToVersion);
      var stored = stream.Select(e => e with { Version = to.TryGetValue(e.EventId, out var v) ? v : e.Version }).ToArray();

      var replayed = ReplayEventOrder.Order([.. stored.Select(_envelope)]).Select(e => e.MessageId.Value).ToArray();
      var byVersion = stored.OrderBy(e => e.Version).Select(e => e.EventId).ToArray();
      if (!replayed.SequenceEqual(byVersion)) {
        Assert.Fail($"arrangement {string.Join(",", versions)}: version order differs from replay order");
      }
      if (ReceivedStreamOrder.Resequence(stored).Count != 0) {
        Assert.Fail($"arrangement {string.Join(",", versions)}: a second re-sequence moved events");
      }
      checkedArrangements++;
    }
    await Assert.That(checkedArrangements).IsEqualTo(720);
  }

  private sealed record ProbeEvent : IEvent;

  private static MessageEnvelope<IEvent> _envelope(StoredStreamEvent e) =>
    new(MessageId.From(e.EventId), new ProbeEvent(), []) {
      LocalStreamVersion = e.Version,
      SourceServiceId = e.OriginServiceId,
      SourceCommitSequence = e.OriginCommitSequence
    };

  private static IEnumerable<long[]> _permutations(long[] items) {
    if (items.Length <= 1) {
      yield return items;
      yield break;
    }
    for (var i = 0; i < items.Length; i++) {
      var rest = items.Where((_, j) => j != i).ToArray();
      foreach (var tail in _permutations(rest)) {
        yield return [items[i], .. tail];
      }
    }
  }

  // ----- #2544: whether filling missing origin positions changes the replay order -----

  [Test]
  public async Task FillChangesTheOrder_AFillThatLandsWhereTheIdPlacedTheEvent_ChangesNothingAsync() {
    IReadOnlyList<StoredStreamEvent> stream = [_from(_originA, 1, 1, 10), _from(_originA, 2, 2), _from(_originA, 3, 3, 30)];

    var changed = ReceivedStreamOrder.FillChangesTheOrder(stream, new Dictionary<Guid, long> { [_id(2)] = 20 });

    await Assert.That(changed).IsFalse().Because("placed by its id between 10 and 30, and its position 20 puts it there too");
  }

  [Test]
  public async Task FillChangesTheOrder_AFillThatMovesTheEventPastAnother_ChangesTheOrderAsync() {
    IReadOnlyList<StoredStreamEvent> stream = [_from(_originA, 1, 1, 10), _from(_originA, 2, 2), _from(_originA, 3, 3, 30)];

    var changed = ReceivedStreamOrder.FillChangesTheOrder(stream, new Dictionary<Guid, long> { [_id(2)] = 40 });

    await Assert.That(changed).IsTrue().Because("its position 40 is after the event at 30, where its id had placed it before");
  }

  [Test]
  public async Task FillChangesTheOrder_NeverOverwritesAPosition_AndIgnoresNonPositiveFillsAsync() {
    IReadOnlyList<StoredStreamEvent> stream = [_from(_originA, 1, 1, 10), _from(_originA, 2, 2), _from(_originA, 3, 3, 30)];

    var changed = ReceivedStreamOrder.FillChangesTheOrder(stream,
      new Dictionary<Guid, long> { [_id(1)] = 99, [_id(2)] = 0, [_id(4)] = 5 });

    await Assert.That(changed).IsFalse().Because("a position held is kept, a zero is no position, and an absent event is ignored");
  }

  [Test]
  public async Task FillChangesTheOrder_ReadsTheStreamByVersion_AndRefusesNullAsync() {
    IReadOnlyList<StoredStreamEvent> stream = [_from(_originA, 3, 3, 30), _from(_originA, 2, 2), _from(_originA, 1, 1, 10)];

    await Assert.That(ReceivedStreamOrder.FillChangesTheOrder(stream, new Dictionary<Guid, long> { [_id(2)] = 40 })).IsTrue();
    await Assert.That(() => ReceivedStreamOrder.FillChangesTheOrder(null!, new Dictionary<Guid, long>())).Throws<ArgumentNullException>();
    await Assert.That(() => ReceivedStreamOrder.FillChangesTheOrder(stream, null!)).Throws<ArgumentNullException>();
  }

  // ----- #2571: where a fill first changes one perspective's replay order -----

  private static readonly Dictionary<Guid, long> _noFills = [];

  [Test]
  public async Task FirstSlotTheFillChanges_AFillThatReordersTheStream_ButNotThePerspectivesEvents_IsNullForThePerspectiveAsync() {
    // P1 (position 1, id 9), P2 (position 2, id 2), U (no position, id 5): the stream goes from U, P1, P2 to P1, P2, U.
    var p1 = _from(_originA, 9, 1, 1);
    var p2 = _from(_originA, 2, 2, 2);
    var u = _from(_originA, 5, 3);
    var fills = new Dictionary<Guid, long> { [u.EventId] = 3 };

    var wholeStream = ReceivedStreamOrder.FirstSlotTheFillChanges([p1, p2, u], fills);
    var perspective = ReceivedStreamOrder.FirstSlotTheFillChanges([p2, u], fills);

    await Assert.That(wholeStream).IsEqualTo(p1).Because("the whole stream's first slot folds U before the fill and P1 after it");
    await Assert.That(perspective).IsNull().Because("a perspective reading only P2 and U folds P2, U both before and after");
  }

  [Test]
  public async Task FirstSlotTheFillChanges_AFillThatKeepsTheStreamsOrder_ButReordersThePerspectivesEvents_NamesItsFirstChangedSlotAsync() {
    // P1 (position 2, id 9), U (no position, id 5), P2 (position 3, id 2), U filled with 1: the stream is U, P1, P2 both
    // before and after, while a perspective reading only U and P2 goes from P2, U to U, P2.
    var p1 = _from(_originA, 9, 1, 2);
    var u = _from(_originA, 5, 2);
    var p2 = _from(_originA, 2, 3, 3);
    var fills = new Dictionary<Guid, long> { [u.EventId] = 1 };

    var wholeStream = ReceivedStreamOrder.FirstSlotTheFillChanges([p1, u, p2], fills);
    var perspective = ReceivedStreamOrder.FirstSlotTheFillChanges([p2, u], fills);

    await Assert.That(wholeStream).IsNull().Because("the whole stream's replay order is the same before and after");
    await Assert.That(perspective).IsEqualTo(u)
      .Because("the perspective's first slot (version 2, where U is stored) folds P2 before the fill and U after it");
  }

  [Test]
  public async Task FirstSlotTheFillChanges_TheRewindFromIt_KeepsTheEarlierSnapshot_AndFoldsWhatARebuildFoldsAsync() {
    StoredStreamEvent[] before = [_from(_originA, 1, 1, 10), _from(_originA, 2, 2, 20), _from(_originA, 3, 3), _from(_originA, 4, 4, 30)];
    var fills = new Dictionary<Guid, long> { [_id(3)] = 40 };
    StoredStreamEvent[] after = [.. before.Select(e => fills.TryGetValue(e.EventId, out var position) ? e with { OriginCommitSequence = position } : e)];

    var first = ReceivedStreamOrder.FirstSlotTheFillChanges(before, fills);

    await Assert.That(first).IsEqualTo(before[2]).Because("versions 1 and 2 fold the same events either way; version 3 does not");
    var replayedAfter = ReplayEventOrder.Order([.. after.Select(_envelope)]);
    await Assert.That(ReplayFloor.Qualifies(replayedAfter, 2, first!.Value.EventId)).IsTrue()
      .Because("the snapshot at version 2 is kept, and it is still a floor for the rewind from version 3");
    var snapshotHolds = ReplayEventOrder.Order([.. before.Select(_envelope)]).Take(2).Select(e => e.MessageId.Value);
    var replayedAboveIt = ReplayEventOrder.Order([.. after.Where(e => e.Version > 2).Select(_envelope)]).Select(e => e.MessageId.Value);
    await Assert.That(snapshotHolds.Concat(replayedAboveIt).ToArray())
      .IsEquivalentTo(replayedAfter.Select(e => e.MessageId.Value).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("restoring the kept snapshot and replaying above it folds what a rebuild in the new order folds");
  }

  [Test]
  public async Task FirstSlotTheFillChanges_IgnoresPositionsHeld_ZeroFills_AndAbsentEvents_AndRefusesNullAsync() {
    StoredStreamEvent[] stream = [_from(_originA, 1, 1, 10), _from(_originA, 2, 2), _from(_originA, 3, 3, 30)];

    await Assert.That(ReceivedStreamOrder.FirstSlotTheFillChanges(stream,
      new Dictionary<Guid, long> { [_id(1)] = 99, [_id(2)] = 0, [_id(4)] = 5 })).IsNull();
    await Assert.That(ReceivedStreamOrder.FirstSlotTheFillChanges(stream, _noFills)).IsNull();
    await Assert.That(ReceivedStreamOrder.FirstSlotTheFillChanges([], _noFills)).IsNull();
    await Assert.That(() => ReceivedStreamOrder.FirstSlotTheFillChanges(null!, _noFills)).Throws<ArgumentNullException>();
    await Assert.That(() => ReceivedStreamOrder.FirstSlotTheFillChanges(stream, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task FirstSlotTheFillChanges_OverEveryArrangement_FillAndTypeSubset_IsWhereThePerspectivesReplayFirstDiffersAsync() {
    // The design choice this locks (#2571): the rewind decision is made per perspective, over the events it reads, by the
    // replay's own rule. The oracle is the replay itself (ReplayEventOrder.Order over the perspective's events, every
    // origin and the local event included), compared before and after the fill: null exactly when the two agree, and
    // otherwise the version of the first slot where they differ, every slot below it folding the same event.
    StoredStreamEvent[] events = [
      _from(_originA, 1, 0), _from(_originA, 2, 0, sequence: 3), _from(_originA, 3, 0), _from(_originA, 4, 0, sequence: 1),
      _from(_originB, 6, 0, sequence: 1), _local(5, 0)];
    var typeX = new HashSet<Guid> { _id(1), _id(3), _id(6) };
    bool[][] typeSubsets = [[true, false], [false, true], [true, true]];
    long[] fillValues = [0, 2, 4];
    int checkedCases = 0, rewound = 0, wholeStreamRewoundOnlyPerspectiveKept = 0, wholeStreamKeptPerspectiveRewound = 0;
    foreach (var versions in _permutations([1, 2, 3, 4, 5, 6])) {
      var stream = events.Select((e, i) => e with { Version = versions[i] }).ToArray();
      foreach (var fillFirst in fillValues) {
        foreach (var fillThird in fillValues) {
          var fills = new Dictionary<Guid, long> { [_id(1)] = fillFirst, [_id(3)] = fillThird };
          var wholeStream = ReceivedStreamOrder.FirstSlotTheFillChanges([.. stream.Where(e => e.OriginServiceId == _originA)], fills);
          foreach (var subset in typeSubsets) {
            var reads = stream.Where(e => typeX.Contains(e.EventId) ? subset[0] : subset[1]).ToArray();
            var first = ReceivedStreamOrder.FirstSlotTheFillChanges([.. reads.Where(e => e.OriginServiceId == _originA)], fills);
            var failure = _replayDisagrees(reads, fills, first);
            if (failure is not null) {
              Assert.Fail($"versions {string.Join(",", versions)}, fills {fillFirst}/{fillThird}, types {subset[0]}/{subset[1]}: {failure}");
            }
            checkedCases++;
            rewound += first is null ? 0 : 1;
            wholeStreamRewoundOnlyPerspectiveKept += wholeStream is not null && first is null ? 1 : 0;
            wholeStreamKeptPerspectiveRewound += wholeStream is null && first is not null ? 1 : 0;
          }
        }
      }
    }
    await Assert.That(checkedCases).IsEqualTo(720 * 9 * 3);
    await Assert.That(rewound).IsGreaterThan(0).Because("a positive control: some fills do reorder a perspective's events");
    await Assert.That(rewound).IsLessThan(checkedCases).Because("and some leave them as they were");
    await Assert.That(wholeStreamRewoundOnlyPerspectiveKept).IsGreaterThan(0)
      .Because("the stream-wide answer rewinds perspectives whose own order the fill left alone");
    await Assert.That(wholeStreamKeptPerspectiveRewound).IsGreaterThan(0)
      .Because("and misses perspectives whose own order the fill changed");
  }

  /// <summary>Null when <paramref name="first"/> is where the replay of <paramref name="reads"/> first differs; else why not.</summary>
  private static string? _replayDisagrees(StoredStreamEvent[] reads, Dictionary<Guid, long> fills, StoredStreamEvent? first) {
    var filled = reads.Select(e => e.OriginCommitSequence <= 0 && fills.TryGetValue(e.EventId, out var position) && position > 0
      ? e with { OriginCommitSequence = position }
      : e).ToArray();
    var before = ReplayEventOrder.Order([.. reads.Select(_envelope)]).Select(e => e.MessageId.Value).ToArray();
    var after = ReplayEventOrder.Order([.. filled.Select(_envelope)]).Select(e => e.MessageId.Value).ToArray();
    if (first is not StoredStreamEvent slot) {
      return before.SequenceEqual(after) ? null : "the replay order changed and no rewind was named";
    }
    var slots = reads.Select(e => e.Version).Order().ToArray();
    var k = Array.IndexOf(slots, slot.Version);
    if (k < 0 || reads.Single(e => e.Version == slot.Version) != slot || slot.OriginServiceId != _originA) {
      return "the named event is not the origin's event the perspective reads at that version";
    }
    if (!before.Take(k).SequenceEqual(after.Take(k))) {
      return "a slot below the named version folds a different event";
    }
    return before[k] == after[k] ? "the named version folds the same event before and after" : null;
  }
}
