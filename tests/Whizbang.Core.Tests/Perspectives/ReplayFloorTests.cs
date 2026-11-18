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
/// The rewind's snapshot floor follows the replay's order (#2379): a snapshot qualifies only when the events it holds come
/// first in that order and the trigger comes after them. The guard below fails if the floor and the replay ever use
/// different orders.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#snapshot-selection</docs>
public class ReplayFloorTests {
  private sealed record Probe : IEvent;

  private static readonly Guid _origin = Guid.Parse("0199a000-0000-7000-8000-00000000000a");

  // Ascending ids: _id(1) < _id(2) < ..., as text and as Guid.
  private static Guid _id(int n) => Guid.Parse($"0199b000-0000-7000-8000-{n:D12}");

  private static MessageEnvelope<IEvent> _event(int id, long? version, Guid origin = default, long source = 0) =>
    new(MessageId.From(_id(id)), new Probe(), []) {
      LocalStreamVersion = version,
      LocalCommitSequence = version,
      SourceServiceId = origin,
      SourceCommitSequence = source,
    };

  // The deployed shape: the producer emitted three Started events (ids 1 to 3), then Completed (id 4). The consumer
  // stored Completed first (version 1), then the Started events, two of them without their origin position.
  private static MessageEnvelope<IEvent>[] _deployedShape() => [
    _event(4, version: 1, _origin, source: 50),
    _event(1, version: 2, _origin, source: 20),
    _event(2, version: 3, _origin),
    _event(3, version: 4, _origin),
  ];

  [Test]
  public async Task Qualifies_WhenTheOriginOrderPutsAnEventAboveTheSnapshotBeforeOneItHolds_IsFalseAsync() {
    var stream = _deployedShape();

    await Assert.That(ReplayFloor.Qualifies(stream, snapshotVersion: 1, triggeringEventId: _id(1))).IsFalse()
      .Because("the producer's Started events come before the Completed event the snapshot at version 1 holds");
  }

  [Test]
  public async Task Qualifies_WhenTheHeldEventsComeFirstInReplayOrder_IsTrueAsync() {
    MessageEnvelope<IEvent>[] stream = [_event(3, version: 1), _event(1, version: 2), _event(2, version: 3), _event(4, version: 4)];

    await Assert.That(ReplayFloor.Qualifies(stream, snapshotVersion: 2, triggeringEventId: _id(2))).IsTrue();
  }

  [Test]
  public async Task Qualifies_ATriggerTheSnapshotHolds_OrOneTheStreamDoesNotHold_IsFalseAsync() {
    MessageEnvelope<IEvent>[] stream = [_event(1, version: 1), _event(2, version: 2), _event(3, version: 3)];

    await Assert.That(ReplayFloor.Qualifies(stream, snapshotVersion: 2, triggeringEventId: _id(2))).IsFalse()
      .Because("the snapshot holds the trigger, so restoring it would not fold the trigger again");
    await Assert.That(ReplayFloor.Qualifies(stream, snapshotVersion: 1, triggeringEventId: _id(9))).IsFalse()
      .Because("a trigger the stream does not hold cannot be placed");
  }

  [Test]
  public async Task Qualifies_ASnapshotOrAnEventWithNoVersion_IsFalseAsync() {
    MessageEnvelope<IEvent>[] stream = [_event(1, version: 1), _event(2, version: 2)];
    MessageEnvelope<IEvent>[] unversioned = [_event(1, version: 1), _event(2, version: null)];

    await Assert.That(ReplayFloor.Qualifies(stream, snapshotVersion: null, triggeringEventId: _id(2))).IsFalse();
    await Assert.That(ReplayFloor.Qualifies(unversioned, snapshotVersion: 1, triggeringEventId: _id(2))).IsFalse();
    await Assert.That(() => ReplayFloor.Qualifies(null!, 1, _id(1))).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// The guard: over many streams (local events, one origin's positioned and unpositioned events, ids in every order),
  /// whenever a snapshot qualifies, the events it holds followed by the replay above it are exactly the full replay's
  /// fold. A floor judged in any order other than the replay's fails this.
  /// </summary>
  [Test]
  public async Task AQualifyingFloor_AndTheReplayAboveIt_FoldTheStreamInTheOrderAFullReplayFoldsItAsync() {
    var random = new Random(2379);
    var qualified = 0;
    var refused = 0;
    for (var round = 0; round < 2000; round++) {
      var count = random.Next(2, 7);
      var ids = Enumerable.Range(1, count).OrderBy(_ => random.Next()).ToArray();
      var stream = new MessageEnvelope<IEvent>[count];
      for (var v = 0; v < count; v++) {
        var kind = random.Next(3);
        stream[v] = kind switch {
          0 => _event(ids[v], version: v + 1),
          1 => _event(ids[v], version: v + 1, _origin, source: random.Next(1, 50)),
          _ => _event(ids[v], version: v + 1, _origin),
        };
      }
      var snapshotVersion = random.Next(1, count);
      var trigger = stream[random.Next(count)].MessageId.Value;
      if (!ReplayFloor.Qualifies(stream, snapshotVersion, trigger)) {
        refused++;
        continue;
      }
      qualified++;
      var full = ReplayEventOrder.Order(stream).Select(e => e.MessageId.Value).ToArray();
      var held = stream.Where(e => e.LocalStreamVersion <= snapshotVersion).Select(e => e.MessageId.Value).ToHashSet();
      var above = ReplayEventOrder.Order([.. stream.Where(e => e.LocalStreamVersion > snapshotVersion)])
        .Select(e => e.MessageId.Value).ToArray();
      await Assert.That(full.Take(held.Count).ToHashSet().SetEquals(held)).IsTrue()
        .Because($"round {round}: the events the snapshot holds come first in the full replay");
      await Assert.That(full.Skip(held.Count).ToArray()).IsEquivalentTo(above, TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because($"round {round}: the replay above the floor folds what the full replay folds after it, in the same order");
    }
    await Assert.That(qualified).IsGreaterThan(100).Because("positive control: many floors qualify");
    await Assert.That(refused).IsGreaterThan(100).Because("positive control: many floors are refused");
  }
}
