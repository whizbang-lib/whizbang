// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The in-memory store's replay read (<see cref="InMemoryEventStore.ReadPolymorphicAsync"/>) follows the stream's
/// own order and carries each event's version, as the SQL stores do (#1352). A replay decides its fold order from
/// the envelope; a store that dropped the version would leave a host on it replaying in a different order from one
/// on Postgres, and a test written against it would check the wrong order.
/// </summary>
/// <docs>fundamentals/events/event-store#read-order</docs>
public class InMemoryEventStoreReplayOrderTests {
  private sealed record TestEvent(int Tag) : IEvent;

  private static MessageEnvelope<TestEvent> _envelope(Guid id, int tag) => new() {
    MessageId = MessageId.From(id),
    Payload = new TestEvent(tag),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = []
  };

  // Appended in this order; the ids sort the other way round, so an id-ordered read reverses them.
  private static readonly Guid[] _appendOrder = [
    Guid.Parse("019df109-0005-7000-8000-000000000000"),
    Guid.Parse("019df109-0004-7000-8000-000000000000"),
    Guid.Parse("019df109-0003-7000-8000-000000000000"),
    Guid.Parse("019df109-0002-7000-8000-000000000000"),
  ];

  private static async Task<(InMemoryEventStore Store, Guid StreamId)> _seedAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    for (var i = 0; i < _appendOrder.Length; i++) {
      await store.AppendAsync(streamId, _envelope(_appendOrder[i], i));
    }
    return (store, streamId);
  }

  private static async Task<List<MessageEnvelope<IEvent>>> _readAsync(InMemoryEventStore store, Guid streamId, Guid? from) {
    var read = new List<MessageEnvelope<IEvent>>();
    await foreach (var envelope in store.ReadPolymorphicAsync(streamId, from, [typeof(TestEvent)])) {
      read.Add(envelope);
    }
    return read;
  }

  [Test]
  public async Task ReadPolymorphicAsync_IdsRunBackwardToAppends_ReturnsTheStreamInAppendOrderAsync() {
    var (store, streamId) = await _seedAsync();

    var read = await _readAsync(store, streamId, null);

    await Assert.That(read.Select(e => e.MessageId.Value)).IsEquivalentTo(_appendOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a replay folds the stream in the order it was written, not the order its ids sort in");
  }

  [Test]
  public async Task ReadPolymorphicAsync_CarriesEachEventsStreamVersionAsync() {
    var (store, streamId) = await _seedAsync();

    var read = await _readAsync(store, streamId, null);

    long?[] expected = [1, 2, 3, 4];  // a new stream starts at version 1 (#1913)
    await Assert.That(read.Select(e => e.LocalStreamVersion)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the replay order is decided from the envelope, so the read must carry each event's version");
  }

  /// <summary>
  /// "After" an event is after its position in the stream, its version, as the SQL stores read it (#2379). The id filter
  /// this replaced read the events whose ids sort above the anchor, which in this stream are the ones written before it.
  /// </summary>
  [Test]
  public async Task ReadPolymorphicAsync_FromAnEvent_ReadsTheEventsAfterItsVersionAsync() {
    var (store, streamId) = await _seedAsync();

    var read = await _readAsync(store, streamId, _appendOrder[2]);

    Guid[] expected = [_appendOrder[3]];
    await Assert.That(read.Select(e => e.MessageId.Value)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the event after version 3 is version 4, whatever the ids say");
  }

  /// <summary>An anchor the stream does not hold has no position in it, so the read is after its id (#2379).</summary>
  [Test]
  public async Task ReadPolymorphicAsync_FromAnEventTheStreamDoesNotHold_ReadsAfterItsIdAsync() {
    var (store, streamId) = await _seedAsync();

    var read = await _readAsync(store, streamId, Guid.Parse("019df109-0004-7000-8000-000000000001"));

    Guid[] expected = [_appendOrder[0]];
    await Assert.That(read.Select(e => e.MessageId.Value)).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }
}
