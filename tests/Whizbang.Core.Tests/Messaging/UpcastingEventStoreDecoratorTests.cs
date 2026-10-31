// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>
  /// A rewind's read through the archive (#1738) is upcast exactly as the ordinary read is, the read widened by a type
  /// change included: an archived event is as old as events get, so it is the likeliest to need an upcaster.
  /// </summary>
  [Test]
  public async Task ReadPolymorphicThroughArchive_UpcastsLikeTheOrdinaryReadAsync() {
    var recording = new RecordingStore(yield: new ForeignEvent());
    var sut = new UpcastingEventStoreDecorator(recording, new EventUpcasterPipeline([new ForeignToNewUpcaster()]));
    var plain = new UpcastingEventStoreDecorator(new StubStore(), _makePipeline());

    var widened = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicThroughArchiveAsync(Guid.NewGuid(), null, 3, [typeof(NewEvent)])) {
      widened.Add(env.Payload);
    }
    var upcast = new List<IEvent>();
    await foreach (var env in plain.ReadPolymorphicThroughArchiveAsync(Guid.NewGuid(), null, 3, [typeof(OldEvent)])) {
      upcast.Add(env.Payload);
    }

    await Assert.That(recording.LastEventTypes).Contains(typeof(ForeignEvent));
    await Assert.That(widened.Single()).IsTypeOf<NewEvent>();
    await Assert.That(upcast.Single()).IsTypeOf<NewEvent>();
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

  /// <summary>
  /// An upcaster that moves an event's <c>[StreamId]</c> is a re-key, and the read records where it moved it, so a
  /// rebuild can route that event, and only that event, onto the row it now names (#1798).
  /// </summary>
  [Test]
  public async Task ReadPolymorphicAsync_UpcasterMovesTheStreamId_RecordsTheRekeyAsync() {
    var from = Guid.NewGuid();
    var to = Guid.NewGuid();
    var sut = new UpcastingEventStoreDecorator(
      new RecordingStore(yield: new RekeyableEvent { StreamId = from, MoveTo = to }),
      new EventUpcasterPipeline([new InPlaceRekeyUpcaster()]),
      new RekeyableEventExtractor());

    var payloads = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(from, null, [typeof(RekeyableEvent)])) {
      payloads.Add(env.Payload);
    }

    await Assert.That(EventRekeys.RekeyedTo(payloads.Single())).IsEqualTo(to)
      .Because("the upcaster moved the event's stream, even though it changed the payload in place");
  }

  /// <summary>
  /// An event whose payload names another stream but that no upcaster moved is not a re-key: the read records nothing,
  /// so a rebuild keeps it on the stream it is stored on, as live apply and the rewind do (#1798).
  /// </summary>
  [Test]
  public async Task ReadPolymorphicAsync_PayloadNamesAnotherStreamButNoUpcasterMovedIt_RecordsNoRekeyAsync() {
    var sut = new UpcastingEventStoreDecorator(
      new RecordingStore(yield: new RekeyableEvent { StreamId = Guid.NewGuid() }),
      new EventUpcasterPipeline([new InPlaceRekeyUpcaster()]),
      new RekeyableEventExtractor());

    var events = await sut.GetEventsBetweenPolymorphicAsync(Guid.NewGuid(), null, Guid.NewGuid(), [typeof(RekeyableEvent)]);

    await Assert.That(EventRekeys.RekeyedTo(events.Single().Payload)).IsNull();
  }

  /// <summary>An upcaster that changes an event's shape but keeps its stream is not a re-key.</summary>
  [Test]
  public async Task DeserializeStreamEvents_UpcasterKeepsTheStreamId_RecordsNoRekeyAsync() {
    var sut = new UpcastingEventStoreDecorator(new StubStore(), _makePipeline(), new RekeyableEventExtractor());

    var events = sut.DeserializeStreamEvents([], [typeof(OldEvent)]);

    await Assert.That(events.Single().Payload).IsTypeOf<NewEvent>();
    await Assert.That(EventRekeys.RekeyedTo(events.Single().Payload)).IsNull();
  }

  /// <summary>An upcaster that leaves the event naming no stream at all records nothing: there is nowhere to route it.</summary>
  [Test]
  public async Task ReadPolymorphicAsync_UpcastLeavesNoStream_RecordsNoRekeyAsync() {
    var sut = new UpcastingEventStoreDecorator(
      new RecordingStore(yield: new RekeyableEvent { StreamId = Guid.NewGuid(), MoveTo = Guid.Empty, ClearStream = true }),
      new EventUpcasterPipeline([new InPlaceRekeyUpcaster()]),
      new RekeyableEventExtractor());

    var payloads = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(RekeyableEvent)])) {
      payloads.Add(env.Payload);
    }

    await Assert.That(EventRekeys.RekeyedTo(payloads.Single())).IsNull();
  }

  /// <summary>
  /// The generated extractor reads an unset <c>[StreamId]</c> as <see cref="Guid.Empty"/>, not as null. An upcaster that
  /// clears the stream leaves an empty id, which names no stream: recording it would route the event onto a row keyed by
  /// the empty id on rebuild.
  /// </summary>
  [Test]
  public async Task ReadPolymorphicAsync_UpcastLeavesAnEmptyStreamId_RecordsNoRekeyAsync() {
    var sut = new UpcastingEventStoreDecorator(
      new RecordingStore(yield: new RekeyableEvent { StreamId = Guid.NewGuid(), MoveTo = Guid.Empty, ClearStream = true }),
      new EventUpcasterPipeline([new InPlaceRekeyUpcaster()]),
      new EmptyReadingExtractor());

    var payloads = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(RekeyableEvent)])) {
      payloads.Add(env.Payload);
    }

    await Assert.That(((RekeyableEvent)payloads.Single()).StreamId).IsEqualTo(Guid.Empty)
      .Because("setup: the upcaster cleared the stream, and the extractor reads it back as the empty id");
    await Assert.That(EventRekeys.RekeyedTo(payloads.Single())).IsNull()
      .Because("an empty stream id names no row, so the read records no re-key");
  }

  /// <summary>
  /// An event stored naming no stream that an upcaster gives one is a re-key too: it now names a row the stored event
  /// never did.
  /// </summary>
  [Test]
  public async Task ReadPolymorphicAsync_UpcasterGivesAStreamToAnEventThatNamedNone_RecordsTheRekeyAsync() {
    var to = Guid.NewGuid();
    var sut = new UpcastingEventStoreDecorator(
      new RecordingStore(yield: new RekeyableEvent { StreamId = Guid.Empty, MoveTo = to }),
      new EventUpcasterPipeline([new InPlaceRekeyUpcaster()]),
      new RekeyableEventExtractor());

    var payloads = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(RekeyableEvent)])) {
      payloads.Add(env.Payload);
    }

    await Assert.That(EventRekeys.RekeyedTo(payloads.Single())).IsEqualTo(to)
      .Because("the stored event named no stream, so any stream the upcaster gives it is a move");
  }

  /// <summary>Without an extractor of its own the decorator reads streams through the generated registry.</summary>
  [Test]
  public async Task ReadPolymorphicAsync_WithoutAnExtractor_StillAppliesTheUpcasterAsync() {
    var sut = new UpcastingEventStoreDecorator(
      new RecordingStore(yield: new RekeyableEvent { StreamId = Guid.NewGuid(), MoveTo = Guid.NewGuid() }),
      new EventUpcasterPipeline([new InPlaceRekeyUpcaster()]),
      streamIds: null);

    var payloads = new List<IEvent>();
    await foreach (var env in sut.ReadPolymorphicAsync(Guid.NewGuid(), null, [typeof(RekeyableEvent)])) {
      payloads.Add(env.Payload);
    }

    await Assert.That(((RekeyableEvent)payloads.Single()).StreamId).IsEqualTo(((RekeyableEvent)payloads.Single()).MoveTo);
  }

  /// <summary>Asking about an event that never passed through a read is answered "not re-keyed".</summary>
  [Test]
  public async Task RekeyedTo_ForAnEventNeverRead_IsNullAsync() {
    await Assert.That(EventRekeys.RekeyedTo(new NewEvent())).IsNull();
  }

  private static EventUpcasterPipeline _makePipeline() => new([new OldToNewUpcaster()]);

  /// <summary>An event a re-key upcaster can move: <see cref="MoveTo"/> is where it goes.</summary>
#pragma warning disable WHIZ009
  public sealed record RekeyableEvent : IEvent {
    [StreamId] public Guid StreamId { get; set; }
    public Guid MoveTo { get; init; }
    public bool ClearStream { get; init; }
  }
#pragma warning restore WHIZ009

  /// <summary>Moves the event's stream in place, as a re-key upcaster that mutates its input does.</summary>
  private sealed class InPlaceRekeyUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is RekeyableEvent { MoveTo: var to, ClearStream: var clear } && (to != Guid.Empty || clear);
    public IEvent Upcast(IEvent storedEvent) {
      var e = (RekeyableEvent)storedEvent;
      e.StreamId = e.MoveTo;
      return e;
    }
  }

  /// <summary>Reads the stream of the events these tests use, as the generated extractor reads <c>[StreamId]</c>.</summary>
  private sealed class RekeyableEventExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => message switch {
      RekeyableEvent { StreamId: var id } => id == Guid.Empty ? null : id,
      OldEvent { StreamId: var id } => id,
      NewEvent { StreamId: var id } => id,
      _ => null,
    };
  }

  /// <summary>Reads <c>[StreamId]</c> as the generated extractor does: an unset id comes back as <see cref="Guid.Empty"/>.</summary>
  private sealed class EmptyReadingExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => message is RekeyableEvent e ? e.StreamId : null;
  }

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
