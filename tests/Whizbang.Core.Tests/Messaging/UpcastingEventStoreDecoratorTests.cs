using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Seam contract test (load-bearing): proves the upcaster pipeline is applied on EVERY
/// polymorphic materialization entrypoint of the decorated <see cref="IEventStore"/> —
/// <c>ReadPolymorphicAsync</c>, <c>GetEventsBetweenPolymorphicAsync</c>, and
/// <c>DeserializeStreamEvents</c>. If any path skipped the pipeline, projected state would
/// diverge by read path. A passthrough pipeline (no upcasters) leaves payloads untouched.
/// </summary>
public class UpcastingEventStoreDecoratorTests {
  [Test]
  public async Task ReadPolymorphicAsync_AppliesUpcasterAsync() {
    var sut = new UpcastingEventStoreDecorator(new StubStore(), _makePipeline());

    var results = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(OldEvent)])) {
      results.Add(env.Payload);
    }

    await Assert.That(results.Single()).IsTypeOf<NewEvent>();
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_AppliesUpcasterAsync() {
    var sut = new UpcastingEventStoreDecorator(new StubStore(), _makePipeline());

    var events = await sut.GetEventsBetweenPolymorphicAsync(Guid.NewGuid(), null, Guid.NewGuid(), [typeof(OldEvent)]);

    await Assert.That(events.Single().Payload).IsTypeOf<NewEvent>();
  }

  [Test]
  public async Task DeserializeStreamEvents_AppliesUpcasterAsync() {
    var sut = new UpcastingEventStoreDecorator(new StubStore(), _makePipeline());

    var events = sut.DeserializeStreamEvents([], [typeof(OldEvent)]);

    await Assert.That(events.Single().Payload).IsTypeOf<NewEvent>();
  }

  [Test]
  public async Task PolymorphicReads_WithNoUpcasters_LeavePayloadUntouchedAsync() {
    var sut = new UpcastingEventStoreDecorator(new StubStore(), new EventUpcasterPipeline([]));

    var events = sut.DeserializeStreamEvents([], [typeof(OldEvent)]);

    await Assert.That(events.Single().Payload).IsTypeOf<OldEvent>();
  }

  [Test]
  public async Task ReadPolymorphicAsync_DeclaringTypeChange_WidensReadAndYieldsTargetAsync() {
    // A declaring upcaster (Foreign → New) whose target is requested: the decorator must widen the
    // inner read with the Foreign source type, then yield the upcast New event.
    var recording = new RecordingStore(yield: new ForeignEvent());
    var sut = new UpcastingEventStoreDecorator(recording, new EventUpcasterPipeline([new ForeignToNewUpcaster()]));

    var results = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(NewEvent)])) {
      results.Add(env.Payload);
    }

    await Assert.That(recording.LastEventTypes).Contains(typeof(ForeignEvent));   // read widened
    await Assert.That(results.Single()).IsTypeOf<NewEvent>();                      // input upcast + kept
  }

  [Test]
  public async Task ReadPolymorphicAsync_TypeChangeProducesUnrequestedTarget_FiltersResultOutAsync() {
    // A multi-target upcaster: requesting NewEvent widens the read with BOTH of its source types,
    // but the source actually present (OtherForeignEvent) upcasts to OldEvent — which the caller
    // did NOT request — so the decorator must drop it (no cross-projection contamination).
    var recording = new RecordingStore(yield: new OtherForeignEvent());
    var sut = new UpcastingEventStoreDecorator(recording, new EventUpcasterPipeline([new TwoTargetUpcaster()]));

    var results = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(NewEvent)])) {
      results.Add(env.Payload);
    }

    await Assert.That(recording.LastEventTypes).Contains(typeof(OtherForeignEvent));   // read widened
    await Assert.That(results.Count).IsEqualTo(0);                                      // but upcast target dropped
  }

  private static EventUpcasterPipeline _makePipeline() => new([new OldToNewUpcaster()]);

  private static MessageEnvelope<IEvent> _makeEnvelope(IEvent payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
  };

#pragma warning disable WHIZ009
  public record OldEvent : IEvent { [StreamId] public Guid StreamId { get; set; } }
  public record NewEvent : IEvent { [StreamId] public Guid StreamId { get; set; } }
  public record ForeignEvent : IEvent { [StreamId] public Guid StreamId { get; set; } }
  public record OtherForeignEvent : IEvent { [StreamId] public Guid StreamId { get; set; } }
#pragma warning restore WHIZ009

  private sealed class OldToNewUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is OldEvent;
    public IEvent Upcast(IEvent storedEvent) => new NewEvent { StreamId = ((OldEvent)storedEvent).StreamId };
  }

  // Declaring type-change upcaster: ForeignEvent (a type the perspective does NOT subscribe to) → NewEvent.
  private sealed class ForeignToNewUpcaster : IEventUpcaster {
    public IReadOnlyList<Type> SourceTypes => [typeof(ForeignEvent)];
    public IReadOnlyList<Type> TargetTypes => [typeof(NewEvent)];
    public bool CanUpcast(IEvent storedEvent) => storedEvent is ForeignEvent;
    public IEvent Upcast(IEvent storedEvent) => new NewEvent { StreamId = ((ForeignEvent)storedEvent).StreamId };
  }

  // Multi-target type-change upcaster: ForeignEvent → NewEvent, OtherForeignEvent → OldEvent.
  // Requesting NewEvent widens the read with BOTH source types, but an OtherForeignEvent upcasts to
  // OldEvent, exercising the decorator's filter-back of upcast results not in the requested set.
  private sealed class TwoTargetUpcaster : IEventUpcaster {
    public IReadOnlyList<Type> SourceTypes => [typeof(ForeignEvent), typeof(OtherForeignEvent)];
    public IReadOnlyList<Type> TargetTypes => [typeof(NewEvent), typeof(OldEvent)];
    public bool CanUpcast(IEvent storedEvent) => storedEvent is ForeignEvent or OtherForeignEvent;
    public IEvent Upcast(IEvent storedEvent) => storedEvent switch {
      ForeignEvent f => new NewEvent { StreamId = f.StreamId },
      OtherForeignEvent o => new OldEvent { StreamId = o.StreamId },
      _ => storedEvent,
    };
  }

  /// <summary>Inner store that records the requested event types and yields one fixed payload.</summary>
  private sealed class RecordingStore(IEvent yield) : IEventStore {
    public IReadOnlyList<Type> LastEventTypes { get; private set; } = [];

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      LastEventTypes = eventTypes;
      await Task.CompletedTask;
      yield return _makeEnvelope(yield);
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) {
      LastEventTypes = eventTypes;
      return Task.FromResult(new List<MessageEnvelope<IEvent>> { _makeEnvelope(yield) });
    }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
        IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      LastEventTypes = eventTypes;
      return [_makeEnvelope(yield)];
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  /// <summary>Minimal inner store: every polymorphic read returns one OldEvent envelope.</summary>
  private sealed class StubStore : IEventStore {
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield return _makeEnvelope(new OldEvent { StreamId = streamId });
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>> { _makeEnvelope(new OldEvent { StreamId = streamId }) });

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
        IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      [_makeEnvelope(new OldEvent())];

    // Unused by these tests.
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }
}
