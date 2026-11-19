// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2268: an answer to a redelivery request bundles across streams by scope, as the windowed backfill does (#2092),
/// within the count and byte bounds, every bundle of one (origin, requester, scope) on one shared broker session, so a
/// stream that spans bundles stays in order and unrelated tenants never share a session.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryPump.cs</code-under-test>
public class RedeliveryPumpAnswerBundlingTests {
  private const string TOPIC = "inbox.whizbang.service.billing";
  private const string TARGET = "billing";
  private const string TENANT_A = /*lang=json,strict*/ "{\"t\":\"tenant-a\"}";
  private const string TENANT_B = /*lang=json,strict*/ "{\"t\":\"tenant-b\"}";
  private static readonly RedeliveryAnswerWindow _window = new(null, null);
  private static readonly string?[] _tenants = ["tenant-a", "tenant-b"];

  [Test]
  public async Task PublishAnswer_FiftyOneEventStreams_AnswerInOneBundle_EachChildNamingItsStreamAsync() {
    var origin = TrackedGuid.New().Value;
    var streams = Enumerable.Range(0, 50).Select(_ => TrackedGuid.New().Value).Order().ToList();
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = _pump(transport, serializer);

    var published = await pump.PublishAnswerAsync(
      [.. streams.Select((s, i) => _evt(s, 1, i + 1))], _window, TOPIC, TARGET, origin);

    await Assert.That(published).IsEqualTo(1)
      .Because("fifty one-event deficits fit one bundle under the count and byte bounds; one bundle per stream was one broker message per missing event");
    var bundle = serializer.Composites.Single();
    await Assert.That(bundle.InnerStreamIds).IsEquivalentTo(streams, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each child still names the stream the origin stored it on, in (stream, version) order");
    var session = RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, scope: null);
    await Assert.That(bundle.StreamId).IsEqualTo(session)
      .Because("a bundle spanning streams is filed and sessioned under the answer's shared key, never one child's stream");
    await Assert.That(_sessionOf(transport.Published[0].Destination)).IsEqualTo(session);
  }

  [Test]
  public async Task PublishAnswer_AStreamLargerThanTheBound_SpansBundlesInOrder_OnOneSessionAsync() {
    var origin = TrackedGuid.New().Value;
    var (a, b, c) = _threeOrderedStreams();
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = _pump(transport, serializer, new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 4 });

    var published = await pump.PublishAnswerAsync([
      _evt(a, 1, 1), _evt(a, 2, 2), _evt(a, 3, 3),
      _evt(b, 1, 4), _evt(b, 2, 5), _evt(b, 3, 6), _evt(b, 4, 7), _evt(b, 5, 8), _evt(b, 6, 9),
      _evt(c, 1, 10),
    ], _window, TOPIC, TARGET, origin);

    await Assert.That(published).IsEqualTo(3);
    await Assert.That(serializer.Composites.ConvertAll(x => x.InnerStreamIds!.Count)).IsEquivalentTo([4, 4, 2],
      TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("each bundle fills to the bound of four across streams");
    var sequencesOfB = serializer.Composites.SelectMany(x => x.InnerStreamIds!.Zip(x.InnerCommitSequences!))
      .Where(p => p.First == b).Select(p => p.Second).ToList();
    await Assert.That(sequencesOfB).IsEquivalentTo([(long?)4, 5, 6, 7, 8, 9], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the stream that spans three bundles keeps its version order across them, contiguous inside each");
    var sessions = transport.Published.Select(p => _sessionOf(p.Destination)).Distinct().ToList();
    await Assert.That(sessions).IsEquivalentTo([RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, scope: null)])
      .Because("a stream split across bundles is ordered by sharing one session: the broker delivers them first to last");
  }

  [Test]
  public async Task PublishAnswer_MixedScopes_NeverShareABundle_AndEachScopeHasItsOwnSessionAsync() {
    var origin = TrackedGuid.New().Value;
    var (a, b, c) = _threeOrderedStreams();
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = _pump(transport, serializer);

    var published = await pump.PublishAnswerAsync([
      _evt(a, 1, 1) with { Scope = TENANT_A },
      _evt(b, 1, 2) with { Scope = TENANT_B },
      _evt(c, 1, 3) with { Scope = TENANT_A },
    ], _window, TOPIC, TARGET, origin);

    await Assert.That(published).IsEqualTo(2).Because("a bundle carries one hop scope, so two tenants never share one");
    await Assert.That(serializer.Composites[0].InnerStreamIds).IsEquivalentTo([a, c], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(serializer.Composites[1].InnerStreamIds).IsEquivalentTo([b]);
    await Assert.That(serializer.Scopes.ConvertAll(s => s!.ApplyTo(null).Scope.TenantId))
      .IsEquivalentTo(_tenants, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each bundle repairs under its own tenant's authority");
    await Assert.That(_sessionOf(transport.Published[0].Destination))
      .IsEqualTo(RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, TENANT_A));
    await Assert.That(_sessionOf(transport.Published[1].Destination))
      .IsEqualTo(RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, TENANT_B))
      .Because("tenants are not serialized behind each other: each scope has its own session");
  }

  [Test]
  public async Task PublishAnswer_TwoPagesOfOneServe_ShareTheSession_SoAStreamSplitAcrossPagesStaysInOrderAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var pump = _pump(transport, new CaptureSerializer());

    _ = await pump.PublishAnswerAsync([_evt(stream, 1, 1), _evt(stream, 2, 2)], _window, TOPIC, TARGET, origin);
    _ = await pump.PublishAnswerAsync([_evt(stream, 3, 3)], _window, TOPIC, TARGET, origin);

    await Assert.That(transport.Published.Select(p => _sessionOf(p.Destination)).Distinct().Count()).IsEqualTo(1)
      .Because("the origin selects in pages and publishes each page; the key is derived, not minted, so every page agrees");
  }

  [Test]
  public async Task AnswerSessionFor_IsDerived_AndSeparatesOrigins_Requesters_Topics_AndScopesAsync() {
    var origin = TrackedGuid.New().Value;
    var key = RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, TENANT_A);

    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, TENANT_A)).IsEqualTo(key)
      .Because("every instance of the origin derives the same key without coordination");
    await Assert.That(RedeliveryPump.AnswerSessionFor(TrackedGuid.New().Value, TOPIC, TARGET, TENANT_A)).IsNotEqualTo(key)
      .Because("two origins' answers are unrelated and are not serialized behind each other");
    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, TOPIC, "shipping", TENANT_A)).IsNotEqualTo(key);
    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, TOPIC, target: null, TENANT_A)).IsNotEqualTo(key);
    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, "inbox.other", TARGET, TENANT_A)).IsNotEqualTo(key);
    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, TENANT_B)).IsNotEqualTo(key);
    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, scope: null)).IsNotEqualTo(key);
    await Assert.That(RedeliveryPump.AnswerSessionFor(origin, TOPIC, TARGET, TENANT_A)).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task AnswerSessionFor_WithoutATopic_ThrowsAsync() {
    await Assert.That(() => RedeliveryPump.AnswerSessionFor(TrackedGuid.New().Value, " ", TARGET, null))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task PublishAnswer_ForARequesterThatFilesOneStreamPerBundle_KeepsOneStreamPerBundleAsync() {
    // A requester older than this answer shape files every child under the bundle's own stream, so it is answered as
    // before: one stream per bundle, on that stream's session.
    var (a, b, _) = _threeOrderedStreams();
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport, serializer, _instance(), new CompositeFactory());

    var published = await pump.PublishAnswerAsync([_evt(a, 1, 1), _evt(b, 1, 2)], _window, TOPIC, TARGET, TrackedGuid.New().Value);

    await Assert.That(published).IsEqualTo(2);
    await Assert.That(serializer.Composites.ConvertAll(x => x.StreamId)).IsEquivalentTo([a, b],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(transport.Published.ConvertAll(p => _sessionOf(p.Destination))).IsEquivalentTo([a, b],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task PublishAnswer_ForABackfill_TheBackfillSessionWinsOverTheAnswerKeyAsync() {
    var backfill = TrackedGuid.New().Value;
    var (a, b, _) = _threeOrderedStreams();
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport, serializer, _instance(), new CompositeFactory()) {
      BundleAcrossStreams = true,
      BundleStreamId = backfill,
    };

    _ = await pump.PublishAnswerAsync([_evt(a, 1, 1), _evt(b, 1, 2)], _window, TOPIC, TARGET, TrackedGuid.New().Value);

    await Assert.That(serializer.Composites.Single().StreamId).IsEqualTo(backfill)
      .Because("a backfill's bundles and its window answer share the backfill's session (#2092)");
  }

  /// <summary>
  /// The #2268 measurement: broker messages per answered bucket (one bucket = one stream of one tenant and type here),
  /// one stream per bundle before and bundled by scope after, for the deficit shapes repair actually sees.
  /// </summary>
  [Test]
  [Arguments(50, 1, 1, 1.0, 0.02)]       // the drain batch of 50 streams, one missing event each, one tenant
  [Arguments(50, 2, 1, 1.0, 0.02)]       // two missing events each: 100 events still fit one bundle
  [Arguments(50, 1, 5, 1.0, 0.1)]        // the same 50 streams spread over five tenants: one bundle per tenant
  [Arguments(500, 1, 1, 1.0, 0.002)]     // one full selection page of 500 one-event streams
  [Arguments(10, 120, 1, 1.0, 0.3)]      // large deficits: 1,200 events fill three bundles instead of ten
  public async Task Measurement_MessagesPerAnsweredBucket_BeforeAndAfterAsync(
      int streamCount, int eventsPerStream, int tenants, double before, double after) {
    var origin = TrackedGuid.New().Value;
    var streams = Enumerable.Range(0, streamCount).Select(_ => TrackedGuid.New().Value).Order().ToList();
    List<RedeliveryEvent> events = [.. streams.SelectMany((s, i) => Enumerable.Range(1, eventsPerStream)
      .Select(v => _evt(s, v, (i * eventsPerStream) + v) with { Scope = $"{{\"t\":\"tenant-{i % tenants}\"}}" }))];

    var perStream = await new RedeliveryPump(new CaptureTransport(), new CaptureSerializer(), _instance(), new CompositeFactory())
      .PublishAnswerAsync(events, _window, TOPIC, TARGET, origin);
    var bundled = await _pump(new CaptureTransport(), new CaptureSerializer())
      .PublishAnswerAsync(events, _window, TOPIC, TARGET, origin);

    await Assert.That((double)perStream / streamCount).IsEqualTo(before);
    await Assert.That((double)bundled / streamCount).IsEqualTo(after);
  }

  private static RedeliveryPump _pump(CaptureTransport transport, CaptureSerializer serializer, RedeliveryPumpOptions? options = null) =>
    new(transport, serializer, _instance(), new CompositeFactory(), options) { BundleAcrossStreams = true };

  private static (Guid A, Guid B, Guid C) _threeOrderedStreams() {
    var ordered = Enumerable.Range(0, 3).Select(_ => TrackedGuid.New().Value).Order().ToList();
    return (ordered[0], ordered[1], ordered[2]);
  }

  private static Guid _sessionOf(TransportDestination destination) =>
    Guid.Parse(destination.Metadata!["StreamId"].GetString()!);

  private static ServiceInstanceProvider _instance() => new(configuration: new ConfigurationBuilder().Build());

  private static RedeliveryEvent _evt(Guid streamId, long version, long commitSequence) => new() {
    EventId = TrackedGuid.New().Value,
    StreamId = streamId,
    Version = version,
    CommitSequence = commitSequence,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
  };

  private sealed class CaptureSerializer : IEnvelopeSerializer {
    public List<RedeliveryComposite> Composites { get; } = [];
    public List<ScopeDelta?> Scopes { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Composites.Add((RedeliveryComposite)(object)envelope.Payload!);
      Scopes.Add(envelope.Hops[0].Scope);
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target,
        },
        $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core",
        payloadType.AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Published.Add((envelope, destination, envelopeType));
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
