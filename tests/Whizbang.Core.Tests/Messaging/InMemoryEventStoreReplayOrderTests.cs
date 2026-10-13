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

    await Assert.That(read.Select(e => e.LocalStreamVersion)).IsEquivalentTo(new long?[] { 0, 1, 2, 3 }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the replay order is decided from the envelope, so the read must carry each event's version");
  }

  [Test]
  public async Task ReadPolymorphicAsync_FromAnEvent_KeepsTheIdFilterAndTheStreamOrderAsync() {
    var (store, streamId) = await _seedAsync();

    // The anchor filters by id, as the SQL stores do: everything whose id sorts above it, in stream order.
    var read = await _readAsync(store, streamId, _appendOrder[2]);

    await Assert.That(read.Select(e => e.MessageId.Value)).IsEquivalentTo(new[] { _appendOrder[0], _appendOrder[1] }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the anchor keeps its id semantics and the read keeps the stream's order");
  }
}
