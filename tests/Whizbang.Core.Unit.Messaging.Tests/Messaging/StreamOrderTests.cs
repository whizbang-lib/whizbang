// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="StreamOrder"/> orders a stream by the position each item carries, never by id (#2551). Every case gives the
/// items positions that run against their ids, so ordering by id fails it.
/// </summary>
public class StreamOrderTests {
  private static readonly Guid[] _ids = [.. Enumerable.Range(0, 3).Select(_ => (Guid)TrackedGuid.New()).Order()];

  [Test]
  public async Task InboxRows_AreOrderedByTheirStreamPosition_NotByIdAsync() {
    InboxBatchRow[] rows = [_row(_ids[0], 3), _row(_ids[1], 1), _row(_ids[2], 2)];

    var ordered = StreamOrder.InStreamOrder(rows);

    await Assert.That(ordered.Select(r => r.MessageId)).IsEquivalentTo([_ids[1], _ids[2], _ids[0]],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task OutboxRows_AreOrderedByTheirEmissionPosition_NotByIdAsync() {
    OutboxBatchRow[] rows = [_outbox(_ids[0], 3), _outbox(_ids[1], 1), _outbox(_ids[2], 0), _outbox(_ids[0], 2)];

    var ordered = StreamOrder.InStreamOrder(rows);

    await Assert.That(ordered.Select(r => r.StreamPosition)).IsEquivalentTo([1L, 2L, 3L, 0L],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a stream is published in the order it was emitted; a row without a position keeps its place after them");
    await Assert.That(() => StreamOrder.InStreamOrder((IEnumerable<OutboxBatchRow>)null!)).Throws<ArgumentNullException>();
  }

  private static OutboxBatchRow _outbox(Guid id, long position) =>
    new() { MessageId = id, MessageType = "T", EventData = "{}", Metadata = "{}", StreamPosition = position };

  [Test]
  public async Task InboxRows_WithoutAPosition_KeepTheFetchsOrder_AfterThePositionedOnesAsync() {
    InboxBatchRow[] rows = [_row(_ids[2], 0), _row(_ids[0], 0), _row(_ids[1], 5)];

    var ordered = StreamOrder.InStreamOrder(rows);

    await Assert.That(ordered.Select(r => r.MessageId)).IsEquivalentTo([_ids[1], _ids[2], _ids[0]],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a fetch that predates the column returns 0; those rows keep the order the fetch returned them in");
  }

  [Test]
  public async Task Events_AreOrderedByTheirVersion_NotByIdAsync() {
    MessageEnvelope<IEvent>[] events = [_event(_ids[0], 2), _event(_ids[1], null), _event(_ids[2], 1)];

    var ordered = StreamOrder.InStreamOrder(events);

    await Assert.That(ordered.Select(e => e.MessageId.Value)).IsEquivalentTo([_ids[2], _ids[0], _ids[1]],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("by version, and an event the read gave no version keeps its place after the versioned ones");
  }

  [Test]
  public async Task NullInput_ThrowsAsync() {
    await Assert.That(() => StreamOrder.InStreamOrder((IEnumerable<InboxBatchRow>)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => StreamOrder.InStreamOrder((IEnumerable<MessageEnvelope<IEvent>>)null!)).Throws<ArgumentNullException>();
  }

  private static InboxBatchRow _row(Guid id, long position) => new() {
    MessageId = id,
    HandlerName = "H",
    MessageType = "T",
    EventData = "{}",
    Metadata = "{}",
    StreamPosition = position,
  };

  private static MessageEnvelope<IEvent> _event(Guid id, long? version) =>
    new(MessageId.From(id), new Probe(), []) { LocalStreamVersion = version };

  private sealed record Probe : IEvent;
}
