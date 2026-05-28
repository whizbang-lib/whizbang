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
/// Regression locks for the ordering invariant inside <see cref="PerspectiveWorker"/>'s
/// drain pipeline. Feeds shuffled events into the static helpers and asserts they emerge
/// in MessageId-ascending order. See plans/ordered-stream-invariant.md (touchpoints 4 + 5).
/// </summary>
public class PerspectiveWorkerOrderingTests {
  private readonly Uuid7IdProvider _idProvider = new();

  [Test]
  public async Task GroupAndDedupe_GivenShuffledEventsForOneStream_ReturnsGroupSortedByMessageIdAscAsync() {
    var streamId = _idProvider.NewGuid();
    var e1 = _envelope(_idProvider.NewGuid());
    var e2 = _envelope(_idProvider.NewGuid());
    var e3 = _envelope(_idProvider.NewGuid());

    // Shuffled input
    var typedEvents = new List<MessageEnvelope<IEvent>> { e3, e1, e2 };

    var rawByEventId = new[] {
      _raw(streamId, e1.MessageId.Value),
      _raw(streamId, e2.MessageId.Value),
      _raw(streamId, e3.MessageId.Value),
    }.ToLookup(r => r.EventId);

    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream(typedEvents, rawByEventId);

    await Assert.That(result.Count).IsEqualTo(1);
    var group = result[streamId];
    await Assert.That(group.Count).IsEqualTo(3);
    await Assert.That(group[0].MessageId).IsEqualTo(e1.MessageId);
    await Assert.That(group[1].MessageId).IsEqualTo(e2.MessageId);
    await Assert.That(group[2].MessageId).IsEqualTo(e3.MessageId);
  }

  [Test]
  public async Task GroupAndDedupe_TwoStreamsShuffled_EachStreamSortedIndependentlyAsync() {
    var streamA = _idProvider.NewGuid();
    var streamB = _idProvider.NewGuid();

    var a1 = _envelope(_idProvider.NewGuid());
    var b1 = _envelope(_idProvider.NewGuid());
    var a2 = _envelope(_idProvider.NewGuid());
    var b2 = _envelope(_idProvider.NewGuid());

    var typedEvents = new List<MessageEnvelope<IEvent>> { b2, a2, b1, a1 };

    var rawByEventId = new[] {
      _raw(streamA, a1.MessageId.Value),
      _raw(streamA, a2.MessageId.Value),
      _raw(streamB, b1.MessageId.Value),
      _raw(streamB, b2.MessageId.Value),
    }.ToLookup(r => r.EventId);

    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream(typedEvents, rawByEventId);

    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result[streamA][0].MessageId).IsEqualTo(a1.MessageId);
    await Assert.That(result[streamA][1].MessageId).IsEqualTo(a2.MessageId);
    await Assert.That(result[streamB][0].MessageId).IsEqualTo(b1.MessageId);
    await Assert.That(result[streamB][1].MessageId).IsEqualTo(b2.MessageId);
  }

  [Test]
  public async Task GroupAndDedupe_EmptyInput_ReturnsEmptyDictionaryAsync() {
    var rawByEventId = Array.Empty<StreamEventData>().ToLookup(r => r.EventId);
    var result = PerspectiveWorker._groupAndDedupeDrainModeEventsByStream(
      [], rawByEventId);

    await Assert.That(result).IsEmpty();
  }

  // ===== helpers =====

  private static MessageEnvelope<IEvent> _envelope(Guid messageId)
    => new(MessageId.From(messageId), new TestEvent(), []);

  private static StreamEventData _raw(Guid streamId, Guid eventId) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = Guid.NewGuid(),
  };

  private sealed record TestEvent : IEvent;
}
