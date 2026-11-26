// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Regression locks for the ordering inside <see cref="PerspectiveWorker"/>'s drain pipeline (#2551): a stream's events
/// are applied in stream order, the version each one carries, never by id. Every case gives the events versions that
/// run against their ids, so ordering by id would fail it.
/// </summary>
public class PerspectiveWorkerOrderingTests {
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task GroupAndDedupe_GivenShuffledEventsForOneStream_ReturnsTheGroupInVersionOrder_NotByIdAsync() {
    var streamId = _idProvider.NewGuid();
    // Ids ascending e1 < e2 < e3; versions run the other way.
    var e1 = _envelope(_idProvider.NewGuid(), version: 3);
    var e2 = _envelope(_idProvider.NewGuid(), version: 2);
    var e3 = _envelope(_idProvider.NewGuid(), version: 1);

    var typedEvents = new List<MessageEnvelope<IEvent>> { e1, e3, e2 };

    var rawByEventId = new[] {
      _raw(streamId, e1.MessageId.Value),
      _raw(streamId, e2.MessageId.Value),
      _raw(streamId, e3.MessageId.Value),
    }.ToLookup(r => r.EventId);

    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream(typedEvents, rawByEventId);

    await Assert.That(result.Count).IsEqualTo(1);
    var group = result[streamId];
    await Assert.That(group.Select(e => e.MessageId)).IsEquivalentTo([e3.MessageId, e2.MessageId, e1.MessageId],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a stream's events are applied by the version each carries, never by id");
  }

  [Test]
  public async Task GroupAndDedupe_TwoStreamsShuffled_EachStreamInItsOwnVersionOrderAsync() {
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();

    var a1 = _envelope(_idProvider.NewGuid(), version: 2);
    var b1 = _envelope(_idProvider.NewGuid(), version: 2);
    var a2 = _envelope(_idProvider.NewGuid(), version: 1);
    var b2 = _envelope(_idProvider.NewGuid(), version: 1);

    var typedEvents = new List<MessageEnvelope<IEvent>> { a1, b1, a2, b2 };

    var rawByEventId = new[] {
      _raw(streamA, a1.MessageId.Value),
      _raw(streamA, a2.MessageId.Value),
      _raw(streamB, b1.MessageId.Value),
      _raw(streamB, b2.MessageId.Value),
    }.ToLookup(r => r.EventId);

    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream(typedEvents, rawByEventId);

    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result[streamA].Select(e => e.MessageId)).IsEquivalentTo([a2.MessageId, a1.MessageId],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(result[streamB].Select(e => e.MessageId)).IsEquivalentTo([b2.MessageId, b1.MessageId],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task GroupAndDedupe_EventsWithoutAVersion_KeepTheOrderTheReadReturnedAsync() {
    var streamId = _idProvider.NewGuid();
    var e1 = _envelope(_idProvider.NewGuid(), version: null);
    var e2 = _envelope(_idProvider.NewGuid(), version: null);

    var rawByEventId = new[] { _raw(streamId, e1.MessageId.Value), _raw(streamId, e2.MessageId.Value) }.ToLookup(r => r.EventId);

    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream([e2, e1], rawByEventId);

    await Assert.That(result[streamId].Select(e => e.MessageId)).IsEquivalentTo([e2.MessageId, e1.MessageId],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("with no version to order by, the read's own order (the stream order the SQL returned) stands; the ids decide nothing");
  }

  [Test]
  public async Task GroupAndDedupe_EmptyInput_ReturnsEmptyDictionaryAsync() {
    var rawByEventId = Array.Empty<StreamEventData>().ToLookup(r => r.EventId);
    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream(
      [], rawByEventId);

    await Assert.That(result).IsEmpty();
  }

  // ===== helpers =====

  private static MessageEnvelope<IEvent> _envelope(Guid messageId, long? version)
    => new(MessageId.From(messageId), new TestEvent(), []) { LocalStreamVersion = version };

  private static StreamEventData _raw(Guid streamId, Guid eventId) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = Guid.NewGuid(),
  };

  private sealed record TestEvent : IEvent;
}
