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
/// The order a replay folds a stream in (#1331). Each test builds a batch whose ids disagree with the order it
/// should come back in, so a comparator that fell back to id order would fail it.
/// </summary>
public class ReplayEventOrderTests {
  private sealed record ProbeEvent : IEvent;

  private static readonly Guid _originA = Guid.Parse("0199a000-0000-7000-8000-00000000000a");
  private static readonly Guid _originB = Guid.Parse("0199a000-0000-7000-8000-00000000000b");

  // Ascending ids: _id(1) < _id(2) < _id(3).
  private static MessageId _id(int n) => MessageId.From(Guid.Parse($"0199b000-0000-7000-8000-{n:D12}"));

  private static MessageEnvelope<IEvent> _event(int id, long? local = null, Guid origin = default, long source = 0) =>
    new(_id(id), new ProbeEvent(), []) {
      LocalCommitSequence = local,
      SourceServiceId = origin,
      SourceCommitSequence = source
    };

  private static async Task _assertOrderAsync(IReadOnlyList<MessageEnvelope<IEvent>> ordered, params int[] ids) {
    await Assert.That(ordered.Select(e => e.MessageId).ToArray()).IsEquivalentTo(ids.Select(_id).ToArray(), TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  private static MessageEnvelope<IEvent> _versioned(int id, long version, long? local) =>
    new(_id(id), new ProbeEvent(), []) { LocalStreamVersion = version, LocalCommitSequence = local };

  [Test]
  public async Task Order_WhenCommitSequencesRunBackwardAgainstVersions_FollowsTheStreamsVersionsAsync() {
    // #1352: rows stamped after the fact carry commit sequences unrelated to the order the stream was written in.
    // The live apply folded them by version; a replay by commit sequence applies the stream's last event first.
    var ordered = ReplayEventOrder.Order([
      _versioned(1, version: 43, local: 352_375),
      _versioned(2, version: 59, local: 331_052),
      _versioned(3, version: 118, local: 48_585),
      _versioned(4, version: 120, local: 41_817)]);

    await _assertOrderAsync(ordered, 1, 2, 3, 4);
  }

  [Test]
  public async Task Order_WhenOneOriginStampsEveryEvent_FollowsOriginOrderOverVersionOrderAsync() {
    // A reconciled straggler is appended last here (the highest version) but belongs in its origin slot.
    var ordered = ReplayEventOrder.Order([
      new(_id(1), new ProbeEvent(), []) { LocalStreamVersion = 1, SourceServiceId = _originA, SourceCommitSequence = 5 },
      new(_id(2), new ProbeEvent(), []) { LocalStreamVersion = 2, SourceServiceId = _originA, SourceCommitSequence = 6 },
      new(_id(3), new ProbeEvent(), []) { LocalStreamVersion = 3, SourceServiceId = _originA, SourceCommitSequence = 4 }]);

    await _assertOrderAsync(ordered, 3, 1, 2);
  }

  [Test]
  public async Task Order_WhenAnEventHasNoVersion_FallsBackToCommitOrderAsync() {
    // A partial mix would interleave two orderings, as with commit sequences below.
    var ordered = ReplayEventOrder.Order([
      _versioned(1, version: 2, local: 11),
      new(_id(2), new ProbeEvent(), []) { LocalCommitSequence = 10 }]);

    await _assertOrderAsync(ordered, 2, 1);
  }

  [Test]
  public async Task Order_WhenEveryEventIsStamped_FollowsCommitOrderNotIdsAsync() {
    // The #1331 shape: the event committed first carries the highest id.
    var ordered = ReplayEventOrder.Order([_event(3, local: 10), _event(1, local: 11), _event(2, local: 12)]);

    await _assertOrderAsync(ordered, 3, 1, 2);
  }

  [Test]
  public async Task Order_WhenOneOriginStampsEveryEvent_FollowsOriginOrderOverLocalOrderAsync() {
    // A reconciled straggler: first at its origin, last to arrive here.
    var ordered = ReplayEventOrder.Order([
      _event(1, local: 20, origin: _originA, source: 5),
      _event(2, local: 21, origin: _originA, source: 6),
      _event(3, local: 22, origin: _originA, source: 4)]);

    await _assertOrderAsync(ordered, 3, 1, 2);
  }

  [Test]
  public async Task Order_WhenOriginsDiffer_FollowsCommitOrderAsync() {
    var ordered = ReplayEventOrder.Order([
      _event(1, local: 31, origin: _originA, source: 1),
      _event(2, local: 30, origin: _originB, source: 9)]);

    await _assertOrderAsync(ordered, 2, 1);
  }

  [Test]
  public async Task Order_WhenAnEventHasNoOriginSequence_FollowsCommitOrderAsync() {
    var ordered = ReplayEventOrder.Order([
      _event(1, local: 41, origin: _originA, source: 1),
      _event(2, local: 40, origin: _originA, source: 0)]);

    await _assertOrderAsync(ordered, 2, 1);
  }

  [Test]
  public async Task Order_WhenAnyEventIsUnstamped_FallsBackToIdOrderAsync() {
    // A partial mix would interleave two orderings, so the whole batch takes id order.
    var ordered = ReplayEventOrder.Order([_event(3, local: 1), _event(2), _event(1, local: 2)]);

    await _assertOrderAsync(ordered, 1, 2, 3);
  }

  [Test]
  public async Task Order_WhenCommitSequencesTie_BreaksOnTheIdAsync() {
    var ordered = ReplayEventOrder.Order([_event(2, local: 7), _event(1, local: 7)]);

    await _assertOrderAsync(ordered, 1, 2);
  }

  [Test]
  public async Task Order_WhenOriginSequencesTie_BreaksOnTheIdAsync() {
    var ordered = ReplayEventOrder.Order([
      _event(2, local: 1, origin: _originA, source: 3),
      _event(1, local: 2, origin: _originA, source: 3)]);

    await _assertOrderAsync(ordered, 1, 2);
  }

  [Test]
  public async Task Order_WithOneEvent_ReturnsTheSameBatchAsync() {
    IReadOnlyList<MessageEnvelope<IEvent>> batch = [_event(1)];

    await Assert.That(ReplayEventOrder.Order(batch)).IsSameReferenceAs(batch);
  }

  [Test]
  public async Task Order_WithNoBatch_ThrowsAsync() {
    await Assert.That(() => ReplayEventOrder.Order(null!)).Throws<ArgumentNullException>();
  }
}
