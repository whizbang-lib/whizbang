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
}
