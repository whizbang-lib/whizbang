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
/// The last event of a read in the order a full replay folds it, found without keeping the read (#1869). Every batch
/// is checked against <see cref="ReplayEventOrder.Order"/> itself, so the two cannot disagree about which event is last.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
public class LastInReplayOrderTests {
  private sealed record Probe(bool Marked) : IEvent;

  private static readonly Guid _originA = Guid.Parse("0199a000-0000-7000-8000-00000000000a");
  private static readonly Guid _originB = Guid.Parse("0199a000-0000-7000-8000-00000000000b");

  // Ascending ids: _id(1) < _id(2) < _id(3), as text and as Guid.
  private static MessageId _id(int n) => MessageId.From(Guid.Parse($"0199b000-0000-7000-8000-{n:D12}"));

  private static MessageEnvelope<IEvent> _event(int id, long? version = null, long? local = null, Guid origin = default, long source = 0) =>
    new(_id(id), new Probe(Marked: false), []) {
      LocalStreamVersion = version,
      LocalCommitSequence = local,
      SourceServiceId = origin,
      SourceCommitSequence = source,
    };

  /// <summary>A read, named for what it exercises.</summary>
  public sealed record ReplayBatch(string Name, MessageEnvelope<IEvent>[] Batch) {
    public override string ToString() => Name;
  }

  /// <summary>One batch per ordering ReplayEventOrder can choose, each read in an order that disagrees with it.</summary>
  public static IEnumerable<Func<ReplayBatch>> Batches() {
    (string Name, MessageEnvelope<IEvent>[] Batch)[] batches = [
      ("one origin: origin order, the last read is not last",
        [_event(1, version: 1, origin: _originA, source: 20), _event(2, version: 2, origin: _originA, source: 30), _event(3, version: 3, origin: _originA, source: 10)]),
      ("one origin: a tie on the origin sequence breaks on the id text",
        [_event(3, version: 1, origin: _originA, source: 5), _event(1, version: 2, origin: _originA, source: 5)]),
      ("no origin: stream order",
        [_event(1, version: 3, origin: Guid.Empty), _event(2, version: 1), _event(3, version: 2)]),
      ("two origins: stream order",
        [_event(1, version: 2, origin: _originA, source: 9), _event(2, version: 1, origin: _originB, source: 1)]),
      ("an unsequenced event of the origin: stream order",
        [_event(1, version: 2, origin: _originA, source: 9), _event(2, version: 1, origin: _originA, source: 0)]),
      ("a tie on the version breaks on the id text",
        [_event(2, version: 4), _event(1, version: 4), _event(3, version: 3)]),
      ("a tie on the version, the later read the larger id",
        [_event(1, version: 4), _event(2, version: 4)]),
      ("no versions: commit order",
        [_event(1, local: 30), _event(2, local: 10), _event(3, local: 20)]),
      ("no versions, a tie on the commit sequence",
        [_event(3, local: 7), _event(2, local: 7)]),
      ("an unstamped event: id order",
        [_event(3, local: 5), _event(1), _event(2, local: 1)]),
      ("a single event", [_event(1)]),
    ];
    return batches.Select(b => (Func<ReplayBatch>)(() => new ReplayBatch(b.Name, b.Batch)));
  }

  [Test]
  [MethodDataSource(nameof(Batches))]
  public async Task Last_IsTheEventAFullReplayFoldsLastAsync(ReplayBatch row) {
    var selection = new LastInReplayOrder();
    foreach (var envelope in row.Batch) {
      selection.Offer(envelope);
    }

    await Assert.That(selection.Count).IsEqualTo(row.Batch.Length);
    await Assert.That(selection.Last).IsSameReferenceAs(ReplayEventOrder.Order(row.Batch)[^1])
      .Because($"{row.Name}: the last event is the one the full replay of the same read folds last");
  }

  [Test]
  [MethodDataSource(nameof(Batches))]
  public async Task LastMatching_IsTheLastMatchingEventInReplayOrderAsync(ReplayBatch row) {
    // Mark every other event, so the matching subset's last differs from the batch's.
    var marked = row.Batch.Select((e, i) => new MessageEnvelope<IEvent>(e.MessageId, new Probe(Marked: i % 2 == 0), []) {
      LocalStreamVersion = e.LocalStreamVersion,
      LocalCommitSequence = e.LocalCommitSequence,
      SourceServiceId = e.SourceServiceId,
      SourceCommitSequence = e.SourceCommitSequence,
    }).ToArray();
    var selection = new LastInReplayOrder(static e => ((Probe)e.Payload).Marked);
    foreach (var envelope in marked) {
      selection.Offer(envelope);
    }

    var expected = ReplayEventOrder.Order(marked).Last(e => ((Probe)e.Payload).Marked);
    await Assert.That(selection.LastMatching).IsSameReferenceAs(expected)
      .Because($"{row.Name}: the matching events are ordered by the ordering the whole read chose");
  }

  [Test]
  public async Task NothingOffered_HasNoLastEventAsync() {
    var selection = new LastInReplayOrder(static _ => true);

    await Assert.That(selection.Count).IsEqualTo(0);
    await Assert.That(selection.Last).IsNull();
    await Assert.That(selection.LastMatching).IsNull();
  }

  [Test]
  public async Task NothingMatching_HasNoLastMatchingEventAsync() {
    var selection = new LastInReplayOrder(static _ => false);
    selection.Offer(_event(1, version: 1));

    await Assert.That(selection.Last).IsNotNull();
    await Assert.That(selection.LastMatching).IsNull();
  }

  [Test]
  public async Task Offer_RefusesANullEventAsync() {
    var selection = new LastInReplayOrder();

    await Assert.That(() => selection.Offer(null!)).Throws<ArgumentNullException>();
  }
}
