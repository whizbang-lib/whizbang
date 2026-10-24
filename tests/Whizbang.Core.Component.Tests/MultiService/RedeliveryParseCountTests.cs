// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.MultiService;

namespace Whizbang.Core.Tests.MultiService;

/// <summary>
/// How many times a redelivered message is parsed between the origin's event store and the damaged consumer's inbox
/// (#1526): the origin parses each stored event once to carry it raw inside the bundle, the bundle is parsed once by
/// each receiving service at the wire, and the dispatch seam's fan-out hands each child the carried payload without
/// parsing it again. These are the regression pins the runtime message cache's redelivery migration must not raise.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryPump.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/MultiService/InMemoryWireTransport.cs</code-under-test>
[Category("MultiService")]
[NotInParallel(["WhizbangBackgroundServiceTests", "MessageParseDiagnostics"])]
public class RedeliveryParseCountTests {

  public sealed record ParseCountProbeEvent : IEvent {
    [StreamId]
    public Guid ProbeStreamId { get; init; }
    public int X { get; init; }
  }

  public sealed class ParseCountCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(ParseCountProbeEvent), TypeNameFormatter.FormatClrTypeName(typeof(ParseCountProbeEvent)), "event", null),
    ];
  }

  private static bool _contextRegistered;
  private static readonly Lock _registerLock = new();

  private static void _ensureJsonContext() {
    lock (_registerLock) {
      if (!_contextRegistered) {
        JsonContextRegistry.RegisterContext(RedeliveryParseCountJsonContext.Default);
        JsonContextRegistry.RegisterDerivedType<IMessage, ParseCountProbeEvent>(TypeNameFormatter.FormatClrTypeName(typeof(ParseCountProbeEvent)));
        _contextRegistered = true;
      }
    }
  }

  [Test]
  public async Task Redelivery_ThreeEventsToOneConsumer_EachEventParsedOnceAtTheOriginAndTheBundleOnceAtTheWireAsync() {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("consumer-svc", svc => svc.WithCatalog<ParseCountCatalog>())
      .StartAsync();
    var consumer = harness.GetService("consumer-svc");

    var streamId = TrackedGuid.New().Value;
    var events = Enumerable.Range(1, 3).Select(i => new RedeliveryEvent {
      EventId = TrackedGuid.New().Value,
      StreamId = streamId,
      Version = i,
      CommitSequence = i,
      EventType = TypeNameFormatter.FormatClrTypeName(typeof(ParseCountProbeEvent)),
      EventData = $$"""{"ProbeStreamId":"{{streamId}}","X":{{i}}}""",
      Metadata = null,
      Scope = null,
      Flags = 0
    }).ToList();
    var eventIds = events.ConvertAll(e => e.EventId);
    using var parses = new MessageParseRecorder([.. eventIds]);

    var pump = new RedeliveryPump(
      transport: harness.Wire,
      envelopeSerializer: new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      compositeFactory: new CompositeFactory());
    var published = await pump.PublishAsync(events, MultiServiceHarnessDefaults.SHARED_TOPIC, target: "consumer-svc");
    await Assert.That(published).IsEqualTo(1);

    // The wire delivers synchronously with publish, so the bundle is in the consumer's inbox now.
    var rows = await consumer.Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10));
    var bundle = rows.Single(r => r.EnvelopeType.Contains("RedeliveryComposite"));
    parses.Track(bundle.MessageId);

    var composite = (RedeliveryComposite)JsonSerializer.Deserialize(
      bundle.Envelope.Payload.GetRawText(), JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RedeliveryComposite)))!;
    using var scope = consumer.Provider.CreateScope();
    var fanout = CompositeInboxFanout.TryExpand(composite, bundle.Envelope, scope.ServiceProvider);
    await Assert.That(fanout.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);

    foreach (var id in eventIds) {
      await Assert.That(parses.CountFor(id)).IsEqualTo(1)
        .Because("the origin parses each stored event once to carry it raw, and the fan-out reuses the carried payload");
      await Assert.That(parses.Summarize(id).Single().Source).IsEqualTo(MessageParseSource.Redelivery);
    }
    await Assert.That(parses.CountFor(bundle.MessageId)).IsEqualTo(0)
      .Because("the bundle was parsed at the wire before it was tracked; it is counted in the test below");
  }

  [Test]
  public async Task Redelivery_TheBundle_IsParsedOnceByEachReceivingServiceAsync() {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("consumer-a", svc => svc.WithCatalog<ParseCountCatalog>())
      .AddService("consumer-b", svc => svc.WithCatalog<ParseCountCatalog>())
      .StartAsync();
    using var parses = new MessageParseRecorder();

    var streamId = TrackedGuid.New().Value;
    var events = new List<RedeliveryEvent> {
      new() {
        EventId = TrackedGuid.New().Value, StreamId = streamId, Version = 1, CommitSequence = 1,
        EventType = TypeNameFormatter.FormatClrTypeName(typeof(ParseCountProbeEvent)),
        EventData = $$"""{"ProbeStreamId":"{{streamId}}","X":1}""", Metadata = null, Scope = null, Flags = 0
      }
    };
    var pump = new RedeliveryPump(
      transport: harness.Wire,
      envelopeSerializer: new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      compositeFactory: new CompositeFactory());
    // Broadcast (no target): both services receive the bundle.
    _ = await pump.PublishAsync(events, MultiServiceHarnessDefaults.SHARED_TOPIC, target: null);

    var bundleRow = (await harness.GetService("consumer-a").Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10)))
      .Single(r => r.EnvelopeType.Contains("RedeliveryComposite"));
    _ = await harness.GetService("consumer-b").Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(parses.CountFor(bundleRow.MessageId, MessageParseKind.Envelope)).IsEqualTo(2)
      .Because("every receiving service parses the bundle it is handed at the wire, once each");
  }
}

/// <summary>JSON context for the redelivery parse-count payloads.</summary>
[JsonSerializable(typeof(RedeliveryParseCountTests.ParseCountProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<RedeliveryParseCountTests.ParseCountProbeEvent>))]
public sealed partial class RedeliveryParseCountJsonContext : JsonSerializerContext;
