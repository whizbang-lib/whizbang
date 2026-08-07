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
    var origin = TrackedGuid.NewMedo().Value;
    var pump = new RedeliveryPump(transport, serializer);

    var published = await pump.PublishAsync(
      [_evt(streamA, a1, 1), _evt(streamA, a2, 2), _evt(streamB, b1, 1)],
      topic: "repair-topic", target: "svc-x", originServiceId: origin);

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
    await Assert.That(compositeA.InnerPayloads.Count).IsEqualTo(2);
    await Assert.That(compositeA.InnerPayloads[0].GetRawText()).IsEqualTo("{\"seeded\":true}")
      .Because("children ride as the stored wire JSON VERBATIM — the origin never rehydrates typed payloads.");
    await Assert.That(compositeA.InnerTypeNames).IsEquivalentTo(["Contracts.ProbeHappened", "Contracts.ProbeHappened"])
      .Because("each child's stored wire type name rides beside its raw payload.");
    await Assert.That(compositeA.OriginServiceId).IsEqualTo(origin)
      .Because("Phase B accounting keys on the origin — the bundle names the service the events came from.");
    await Assert.That(compositeA.InnerCommitSequences!).IsEquivalentTo([(long?)1, 2])
      .Because("original commit sequences ride the bundle so repaired events recount in their window.");

    var compositeB = serializer.Captured[1].Payload;
    await Assert.That(compositeB.StreamId).IsEqualTo(streamB);
    await Assert.That(compositeB.InnerEventIds).IsEquivalentTo([b1]);
    await Assert.That(compositeB.InnerCommitSequences!).IsEquivalentTo([(long?)1]);
  }

  [Test]
  public async Task Publish_ChunksStreamsByMaxInnerEventsAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var ids = Enumerable.Range(0, 5).Select(_ => TrackedGuid.NewMedo().Value).ToArray();
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer,
      options: new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 2 });

    var published = await pump.PublishAsync(
      [.. ids.Select((id, i) => _evt(stream, id, i + 1))],
      topic: "repair-topic", target: null, stateOnly: true);

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
    await Assert.That(transport.Published.All(p => p.Envelope.StateOnly)).IsTrue()
      .Because("a state-only bundle carries the marker on every chunk's envelope (Phase S backfill).");
  }

  [Test]
  public async Task Publish_ChunksByRawBodyBytes_BelowTheCountBoundAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var ids = Enumerable.Range(0, 3).Select(_ => TrackedGuid.NewMedo().Value).ToArray();
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    // Each event carries ~102 raw bytes (100-byte body + "{}" metadata); a 100-byte budget is
    // crossed by every single event, so each flushes alone — a count-only bound would build one
    // 3-event composite here.
    var pump = new RedeliveryPump(transport, serializer,
      options: new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 500, MaxBytesPerComposite = 100 });
    var bigBody = "{\"pad\":\"" + new string('x', 90) + "\"}";

    var published = await pump.PublishAsync(
      [.. ids.Select((id, i) => _evt(stream, id, i + 1) with { EventData = bigBody })],
      topic: "repair-topic", target: "svc");

    await Assert.That(published).IsEqualTo(3)
      .Because("large-bodied histories must flush by BYTES below the count bound — a count-only " +
               "chunk exhausts memory during serialization or exceeds the broker message-size " +
               "limit and the repair never delivers.");
    foreach (var envelope in serializer.Captured) {
      await Assert.That(envelope.Payload.InnerEventIds!.Count).IsEqualTo(1);
    }
  }

  [Test]
  public async Task Publish_SingleEventLargerThanByteBudget_StillShipsAloneAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer,
      options: new RedeliveryPumpOptions { MaxBytesPerComposite = 10 });

    var published = await pump.PublishAsync(
      [_evt(stream, TrackedGuid.NewMedo().Value, 1) with { EventData = "{\"pad\":\"oversized-body\"}" }],
      topic: "repair-topic", target: "svc");

    await Assert.That(published).IsEqualTo(1)
      .Because("an event bigger than the budget still ships, alone — the budget bounds grouping, " +
               "it never silently drops a repair.");
  }

  [Test]
  public async Task Publish_UnknownEventType_StillPublishesAsync() {
    // The origin needs NO type knowledge to repair: a stored event whose type is not registered
    // anywhere on this host still ships verbatim. The typed design threw here and the repair
    // never left the origin.
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer);

    var published = await pump.PublishAsync(
      [_evt(TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value, 1) with { EventType = "Totally.Unregistered.Type, Nowhere" }],
      topic: "repair-topic", target: "svc");

    await Assert.That(published).IsEqualTo(1);
    await Assert.That(serializer.Captured[0].Payload.InnerTypeNames).IsEquivalentTo(["Totally.Unregistered.Type, Nowhere"]);
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
          Target = envelope.Target,
          StateOnly = envelope.StateOnly
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
