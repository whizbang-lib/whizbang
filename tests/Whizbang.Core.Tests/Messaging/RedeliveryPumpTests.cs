using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
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
    var pump = new RedeliveryPump(transport, serializer, new Whizbang.Core.Observability.ServiceInstanceProvider());

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
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
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
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
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
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
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
    var pump = new RedeliveryPump(transport, serializer, new Whizbang.Core.Observability.ServiceInstanceProvider());

    var published = await pump.PublishAsync(
      [_evt(TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value, 1) with { EventType = "Totally.Unregistered.Type, Nowhere" }],
      topic: "repair-topic", target: "svc");

    await Assert.That(published).IsEqualTo(1);
    await Assert.That(serializer.Captured[0].Payload.InnerTypeNames).IsEquivalentTo(["Totally.Unregistered.Type, Nowhere"]);
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  // === Scope carry ===
  //
  // The pump reads each event's persisted scope (SelectRedeliveryEventsAsync selects
  // es.scope::text into RedeliveryEvent.Scope) and used to publish without it. Every repaired
  // event then arrived unscoped, CompositeInboxFanout derived each child's scope from the
  // composite's hop and found none, and the children were WRITTEN to the event store with a null
  // scope. From there nothing can recover it: a perspective requiring a security context throws on
  // each one, retries to its attempt limit, and parks it. Repair, whose whole purpose is
  // convergence, manufactured events that can never converge.

  [Test]
  public async Task Publish_CarriesTheStoredScopeOntoTheWireHopAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer, new Whizbang.Core.Observability.ServiceInstanceProvider());

    await pump.PublishAsync(
      [_scopedEvt(stream, TrackedGuid.NewMedo().Value, 1, _scopeJson("tenant-a", "user-a"))],
      topic: "repair-topic", target: "svc-x");

    var hop = serializer.Captured[0].Hops[0];
    await Assert.That(hop.Scope).IsNotNull()
      .Because("the pump HAS the original scope on every RedeliveryEvent it selected; publishing "
             + "without it is what persists the repaired copies unscoped and unprocessable");
    await Assert.That(hop.Scope!.ApplyTo(null).Scope.TenantId).IsEqualTo("tenant-a");
  }

  [Test]
  public async Task Publish_NeverMixesDifferentScopesIntoOneBundleAsync() {
    // Same stream, two scopes. A single composite carries ONE hop scope, so bundling these
    // together would stamp one tenant's scope onto the other tenant's event — a cross-tenant
    // mis-attribution created by the repair path itself. Splitting is the only safe answer.
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer, new Whizbang.Core.Observability.ServiceInstanceProvider());

    var published = await pump.PublishAsync([
        _scopedEvt(stream, TrackedGuid.NewMedo().Value, 1, _scopeJson("tenant-a", "user-a")),
        _scopedEvt(stream, TrackedGuid.NewMedo().Value, 2, _scopeJson("tenant-b", "user-b")),
      ], topic: "repair-topic", target: "svc-x");

    await Assert.That(published).IsEqualTo(2)
      .Because("one bundle cannot represent two scopes; merging them would repair one tenant's "
             + "data under another tenant's authority");

    var tenants = serializer.Captured
      .Select(c => c.Hops[0].Scope?.ApplyTo(null).Scope.TenantId ?? "<unscoped>")
      .OrderBy(t => t, StringComparer.Ordinal)
      .ToList();
    await Assert.That(tenants).IsEquivalentTo(["tenant-a", "tenant-b"]);
  }

  [Test]
  public async Task Publish_LeavesTheHopUnscopedWhenTheEventHadNoScopeAsync() {
    // The other half of the contract: carrying what was stored must never become inventing what
    // was not. An event persisted without a scope stays without one.
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer, new Whizbang.Core.Observability.ServiceInstanceProvider());

    await pump.PublishAsync(
      [_evt(stream, TrackedGuid.NewMedo().Value, 1)],
      topic: "repair-topic", target: "svc-x");

    await Assert.That(serializer.Captured[0].Hops[0].Scope).IsNull()
      .Because("fabricating a scope for an unscoped event would hand it an authority nobody "
             + "granted, which is worse than the exception it would silence");
  }

  [Test]
  public async Task Publish_TreatsAJsonNullScopeAsNoScopeAsync() {
    // The stored column holds the JSON null LITERAL, not SQL NULL, so the string reaching the pump
    // is "null" — four characters, not empty. A null-or-empty guard does not fire on it.
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var serializer = new _captureSerializer();
    var pump = new RedeliveryPump(transport, serializer, new Whizbang.Core.Observability.ServiceInstanceProvider());

    await pump.PublishAsync(
      [_scopedEvt(stream, TrackedGuid.NewMedo().Value, 1, "null")],
      topic: "repair-topic", target: "svc-x");

    await Assert.That(serializer.Captured[0].Hops[0].Scope).IsNull()
      .Because("\"null\" is not an empty string, so it reaches the deserializer; treating the "
             + "result as a scope would attach an empty authority to every repaired event");
  }

  private static string _scopeJson(string tenantId, string userId) =>
    $$"""{"t": "{{tenantId}}", "u": "{{userId}}"}""";

  private static RedeliveryEvent _scopedEvt(Guid streamId, Guid eventId, long version, string? scopeJson) =>
    _evt(streamId, eventId, version) with { Scope = scopeJson };

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

  [Test]
  public async Task Publish_RetriesTransientSendFailures_AndCompletesTheServeAsync() {
    // Observed live: one throttled broker send (a 30s timeout under namespace saturation) threw
    // out of PublishAsync and aborted the WHOLE serve — every later chunk of a 1,400-event
    // repair died with it, the requester's attempt burned, and the deficit stayed frozen. A
    // transient send failure must cost a retry, not the serve.
    var streamA = TrackedGuid.NewMedo().Value;
    var streamB = TrackedGuid.NewMedo().Value;
    var transport = new _flakyTransport { FailFirst = 2 };
    var pump = new RedeliveryPump(transport, new _captureSerializer(), new Whizbang.Core.Observability.ServiceInstanceProvider(), options: new RedeliveryPumpOptions {
      PublishRetryAttempts = 5,
      PublishRetryBaseDelayMs = 0,
    });

    var published = await pump.PublishAsync(
      [_evt(streamA, TrackedGuid.NewMedo().Value, 1), _evt(streamB, TrackedGuid.NewMedo().Value, 1)],
      topic: "repair-topic", target: "svc-x");

    await Assert.That(published).IsEqualTo(2)
      .Because("the first chunk's two transient failures are retried and the serve completes.");
    await Assert.That(transport.Published.Count).IsEqualTo(2)
      .Because("both composites reached the wire despite the flaky sends.");
    await Assert.That(transport.Attempts).IsEqualTo(4)
      .Because("chunk one took three attempts (two failures + success), chunk two took one.");
  }

  [Test]
  public async Task Publish_ExhaustedRetries_RethrowsAfterTheConfiguredAttemptsAsync() {
    var transport = new _flakyTransport { FailFirst = int.MaxValue };
    var pump = new RedeliveryPump(transport, new _captureSerializer(), new Whizbang.Core.Observability.ServiceInstanceProvider(), options: new RedeliveryPumpOptions {
      PublishRetryAttempts = 3,
      PublishRetryBaseDelayMs = 0,
    });

    await Assert.That(async () => await pump.PublishAsync(
        [_evt(TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value, 1)],
        topic: "repair-topic", target: "svc-x"))
      .Throws<TimeoutException>()
      .Because("a chunk that fails every attempt surfaces the failure — the requester's ladder owns what happens next.");
    await Assert.That(transport.Attempts).IsEqualTo(3)
      .Because("the retry budget is bounded — a dead broker must not be hammered forever.");
  }

  [Test]
  public async Task Publish_Cancellation_PropagatesWithoutRetryAsync() {
    var transport = new _flakyTransport { FailFirst = int.MaxValue, ThrowCancellation = true };
    var pump = new RedeliveryPump(transport, new _captureSerializer(), new Whizbang.Core.Observability.ServiceInstanceProvider(), options: new RedeliveryPumpOptions {
      PublishRetryAttempts = 5,
      PublishRetryBaseDelayMs = 0,
    });

    await Assert.That(async () => await pump.PublishAsync(
        [_evt(TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value, 1)],
        topic: "repair-topic", target: "svc-x"))
      .Throws<OperationCanceledException>()
      .Because("cancellation is a shutdown signal, not a transient fault.");
    await Assert.That(transport.Attempts).IsEqualTo(1)
      .Because("a canceled serve stops immediately — retrying it would fight the host's shutdown.");
  }

  private sealed class _flakyTransport : ITransport {
    public int FailFirst { get; set; }
    public bool ThrowCancellation { get; set; }
    public int Attempts { get; private set; }
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Attempts++;
      if (ThrowCancellation) {
        throw new OperationCanceledException("shutdown");
      }
      if (Attempts <= FailFirst) {
        throw new TimeoutException("SendMessageAsync timed out (simulated broker throttle)");
      }
      Published.Add((envelope, destination, envelopeType));
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
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
