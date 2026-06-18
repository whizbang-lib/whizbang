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
#pragma warning restore WHIZ009

  private sealed class OldToNewUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is OldEvent;
    public IEvent Upcast(IEvent storedEvent) => new NewEvent { StreamId = ((OldEvent)storedEvent).StreamId };
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
