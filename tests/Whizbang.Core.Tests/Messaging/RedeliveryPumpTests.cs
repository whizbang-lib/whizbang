using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Stream-integrity R1a2: the re-delivery pump bundles a (stream, version)-ordered selection into
/// per-stream <see cref="RedeliveryComposite"/>s — original payloads rehydrated via the event
/// store's AOT path, original ids in <see cref="RedeliveryComposite.InnerEventIds"/>, the directed
/// <see cref="IMessageEnvelope.Target"/> stamped — and publishes them wire-only via
/// <see cref="ITransport"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryPump.cs</code-under-test>
public class RedeliveryPumpTests {

  [Test]
  public async Task Publish_BundlesPerStream_VersionOrdered_WithOriginalIdsAndTargetAsync() {
    var streamA = TrackedGuid.NewMedo().Value;
    var streamB = TrackedGuid.NewMedo().Value;
    var a1 = TrackedGuid.NewMedo().Value;
    var a2 = TrackedGuid.NewMedo().Value;
    var b1 = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, new _mapEventStore(), new _typeProvider(), serializer);

    var published = await pump.PublishAsync(
      [_evt(streamA, a1, 1), _evt(streamA, a2, 2), _evt(streamB, b1, 1)],
      topic: "repair-topic", target: "svc-x");

    await Assert.That(published).IsEqualTo(2)
      .Because("two streams → two composites (one repair bundle per stream).");
    await Assert.That(transport.Published.Count).IsEqualTo(2);

    var (envA, destA, typeA) = transport.Published[0];
    await Assert.That(destA.Address).IsEqualTo("repair-topic");
    await Assert.That(typeA!).Contains("RedeliveryComposite")
      .Because("the wire envelope-type is derived from the composite's runtime type by the serializer seam.");
    await Assert.That(envA.Target).IsEqualTo("svc-x")
      .Because("the repair bundle is directed at the damaged consumer — everyone else discards.");
    var compositeA = serializer.Captured[0].Payload;
    await Assert.That(compositeA.StreamId).IsEqualTo(streamA);
    await Assert.That(compositeA.InnerEventIds).IsEquivalentTo([a1, a2])
      .Because("original ids, in version order — identity is what makes convergence idempotent.");
    await Assert.That(compositeA.Inner.Count).IsEqualTo(2);

    var compositeB = serializer.Captured[1].Payload;
    await Assert.That(compositeB.StreamId).IsEqualTo(streamB);
    await Assert.That(compositeB.InnerEventIds).IsEquivalentTo([b1]);
  }

  [Test]
  public async Task Publish_ChunksStreamsByMaxInnerEventsAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var ids = Enumerable.Range(0, 5).Select(_ => TrackedGuid.NewMedo().Value).ToArray();
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, new _mapEventStore(), new _typeProvider(), serializer,
      options: new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 2 });

    var published = await pump.PublishAsync(
      [.. ids.Select((id, i) => _evt(stream, id, i + 1))],
      topic: "repair-topic", target: null);

    await Assert.That(published).IsEqualTo(3)
      .Because("five events at a chunk bound of two → composites of 2 + 2 + 1.");
    var chunkIds = serializer.Captured
      .Select(env => env.Payload.InnerEventIds)
      .ToList();
    await Assert.That(chunkIds[0]).IsEquivalentTo([ids[0], ids[1]]);
    await Assert.That(chunkIds[1]).IsEquivalentTo([ids[2], ids[3]]);
    await Assert.That(chunkIds[2]).IsEquivalentTo([ids[4]]);
    await Assert.That(transport.Published.All(p => p.Envelope.Target is null)).IsTrue()
      .Because("a null target is an operator broadcast repair — everyone considers it.");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static RedeliveryEvent _evt(Guid streamId, Guid eventId, long version) => new() {
    EventId = eventId,
    StreamId = streamId,
    Version = version,
    CommitSequence = version,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
    Scope = null,
    Flags = 0
  };

  internal sealed record _probeEvent(Guid Id) : IEvent;

  /// <summary>Maps each raw row to an envelope whose MessageId is the row's EventId — the shape the
  /// real store's AOT deserialization path produces from stored envelopes.</summary>
  private sealed class _mapEventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      [.. streamEvents.Select(raw => new MessageEnvelope<IEvent> {
        MessageId = new MessageId(raw.EventId),
        Payload = new _probeEvent(raw.EventId),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
      })];

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(_probeEvent)];
  }

  /// <summary>Captures the typed composite envelope at the serializer seam (the outbox's composite
  /// path) and returns a field-copied JsonElement envelope, as the real serializer does.</summary>
  private sealed class _captureSerializer : IEnvelopeSerializer {
    public List<IMessageEnvelope<RedeliveryComposite>> Captured { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Captured.Add((IMessageEnvelope<RedeliveryComposite>)envelope);
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target
        },
        $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core",
        payloadType.AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
