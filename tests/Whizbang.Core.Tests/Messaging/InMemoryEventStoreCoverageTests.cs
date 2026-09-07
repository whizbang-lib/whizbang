using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Contracts;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage-round-23 targeted tests for two <see cref="InMemoryEventStore"/> paths the main
/// suite (InMemoryEventStoreTests) never exercises: <c>ReadPolymorphicAsync</c>'s guard
/// against a stored payload type outside the caller's declared event-types list, and the
/// always-empty <c>DeserializeStreamEvents</c> override (the in-memory store never has raw
/// <c>StreamEventData</c> rows to deserialize - that path only matters for a real durable
/// store's drain mode).
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/InMemoryEventStore.cs</tests>
public class InMemoryEventStoreCoverageTests {

  // A perspective's polymorphic replay only knows how to handle the event types it
  // registered; if the stream also holds a payload type outside that set (e.g. a newer
  // producer already emits an event type this reader predates), silently returning it
  // would hand an unrecognized payload to replay logic that assumes every yielded event is
  // one of its own known types. Failing loudly here is what keeps that assumption safe
  // instead of letting an unexpected shape corrupt a read model.
  [Test]
  public async Task ReadPolymorphicAsync_PayloadTypeNotInProvidedEventTypesList_ThrowsInvalidOperationExceptionAsync() {
    var eventStore = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, _createTestEnvelope(streamId, "event-1"));

    var eventTypes = new List<Type> { typeof(UnregisteredCoverageEvent) };

    await Assert.That(async () => {
      await foreach (var _ in eventStore.ReadPolymorphicAsync(streamId, null, eventTypes)) {
        // Should throw before a single event is yielded.
      }
    }).ThrowsExactly<InvalidOperationException>()
      .Because("the stored TestEvent payload type is not in the caller's declared event-types list");
  }

  // In-memory streams already hold fully-materialized envelopes in memory; the raw
  // StreamEventData batch-fetch path this method services only exists for a real durable
  // store's drain mode. If this override ever started returning non-empty data, drain
  // mode would try to re-deserialize events the in-memory store never actually persisted
  // in that raw form, duplicating or corrupting the replay.
  [Test]
  public async Task DeserializeStreamEvents_AnyInput_ReturnsEmptyListAsync() {
    var eventStore = new InMemoryEventStore();

    var result = eventStore.DeserializeStreamEvents([], [typeof(TestEvent)]);

    await Assert.That(result).IsEmpty()
      .Because("InMemoryEventStore has no raw StreamEventData rows to deserialize - the override is intentionally a no-op");
  }

  private static MessageEnvelope<TestEvent> _createTestEnvelope(Guid streamId, string payload) {
    return new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent {
        StreamId = streamId,
        Payload = payload
      },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private sealed class UnregisteredCoverageEvent : IEvent { }
}
