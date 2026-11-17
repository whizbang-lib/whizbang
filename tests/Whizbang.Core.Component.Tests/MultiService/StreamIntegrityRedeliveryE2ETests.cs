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
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.MultiService;

namespace Whizbang.Core.Tests.MultiService;

/// <summary>
/// Stream-integrity R1c — the drop-injection convergence proof over the multi-service harness's
/// REAL wire: an outage window drops deliveries at ONE consumer's receive seam (the wire still
/// delivers to everyone else), then the origin-side re-delivery pump publishes an
/// identity-preserving <see cref="RedeliveryComposite"/> DIRECTED at the damaged service. The
/// healthy consumer discards the bundle at the receive seam (R0 directed targeting); the damaged
/// consumer stores it, and the dispatch seam's fan-out expands it into children carrying the
/// ORIGINAL event ids and the original payload bodies — so the inbox-dedup / event-store conflict
/// machinery treats re-delivery exactly like the live delivery it replaces.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryPump.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/MultiService/WireFaultInjection.cs</code-under-test>
[Category("MultiService")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class StreamIntegrityRedeliveryE2ETests {

  public sealed record RepairProbeEvent : IEvent {
    [StreamId]
    public Guid ProbeStreamId { get; init; }
    public int X { get; init; }
  }

  public sealed class ProbeCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(RepairProbeEvent), TypeNameFormatter.FormatClrTypeName(typeof(RepairProbeEvent)), "event", null),
    ];
  }

  private static bool _contextRegistered;
  private static readonly Lock _registerLock = new();

  private static void _ensureJsonContext() {
    lock (_registerLock) {
      if (!_contextRegistered) {
        JsonContextRegistry.RegisterContext(StreamIntegrityE2EJsonContext.Default);
        // What a consumer's generated module initializer does: derived-type registrations make the
        // probe wire-polymorphic, so it round-trips inside the composite's inner IMessage list.
        JsonContextRegistry.RegisterDerivedType<IMessage, RepairProbeEvent>(TypeNameFormatter.FormatClrTypeName(typeof(RepairProbeEvent)));
        JsonContextRegistry.RegisterDerivedType<IEvent, RepairProbeEvent>(TypeNameFormatter.FormatClrTypeName(typeof(RepairProbeEvent)));
        _contextRegistered = true;
      }
    }
  }

  [Test]
  public async Task DroppedDeliveries_RepairViaTargetedComposite_PreservesOriginalIdentityAsync() {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("healthy-svc", svc => svc.WithCatalog<ProbeCatalog>())
      .AddService("damaged-svc", svc => svc.WithCatalog<ProbeCatalog>())
      .StartAsync();
    var healthy = harness.GetService("healthy-svc");
    var damaged = harness.GetService("damaged-svc");

    // Live traffic with an outage window at ONE consumer: X=2/X=3 reach the healthy service but are
    // dropped at the damaged service's receive seam — a per-consumer delivery loss, not a publish
    // failure. Harness delivery is synchronous with publish, so the window is exact.
    await harness.PublishAsync(new RepairProbeEvent { X = 1 });
    using (harness.SuppressDeliveries("damaged-svc")) {
      await harness.PublishAsync(new RepairProbeEvent { X = 2 });
      await harness.PublishAsync(new RepairProbeEvent { X = 3 });
    }
    await harness.PublishAsync(new RepairProbeEvent { X = 4 });

    var healthyRows = await healthy.Inbox.WaitForInboxAsync(4, TimeSpan.FromSeconds(10));
    var damagedRows = await damaged.Inbox.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));
    await Assert.That(damagedRows.Count).IsEqualTo(2)
      .Because("the outage dropped X=2/X=3 for the damaged service only; X=4 arriving proves the fault lifted on dispose.");

    // The origin's knowledge of the lost events — original ids + stored bodies. The healthy
    // service's captured rows stand in for the origin's event store: same ids, same payload JSON.
    var missing = healthyRows.Where(r => _x(r) is 2 or 3).OrderBy(_x).ToList();
    await Assert.That(missing.Count).IsEqualTo(2);
    var missingIds = missing.ConvertAll(m => m.MessageId);
    var streamId = TrackedGuid.New().Value;
    var events = missing.Select((row, i) => new RedeliveryEvent {
      EventId = row.MessageId,
      StreamId = streamId,
      Version = i + 1,
      CommitSequence = i + 1,
      EventType = TypeNameFormatter.FormatClrTypeName(typeof(RepairProbeEvent)),
      EventData = row.Envelope.Payload.GetRawText(),
      Metadata = null,
      Scope = null,
      Flags = 0
    }).ToList();

    // R1 repair: the origin-side pump publishes ONE identity-preserving composite, wire-only,
    // DIRECTED at the damaged service, on the same topic the originals used. The REAL envelope
    // serializer converts the typed bundle exactly as the outbox's composite seam does.
    var pump = new RedeliveryPump(
      transport: harness.Wire,
      envelopeSerializer: new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()),
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      compositeFactory: new CompositeFactory());
    var originServiceId = TrackedGuid.New().Value;
    var published = await pump.PublishAsync(
      events, MultiServiceHarnessDefaults.SHARED_TOPIC, target: "damaged-svc", originServiceId: originServiceId);
    await Assert.That(published).IsEqualTo(1);

    // Sentinel broadcast AFTER the repair: per-subscriber delivery is ordered, so once a service
    // holds the sentinel its verdict on the earlier repair bundle is final — no timing.
    await harness.PublishAsync(new RepairProbeEvent { X = 5 });
    var healthyFinal = await healthy.Inbox.WaitForInboxAsync(5, TimeSpan.FromSeconds(10));
    var damagedFinal = await damaged.Inbox.WaitForInboxAsync(4, TimeSpan.FromSeconds(10));

    // #2091: the undamaged consumer discards the targeted bundle on receipt with no row at all: framework traffic
    // addressed to another service costs it no storage, no dispatch and no retry. Repair traffic can still never
    // double-apply where nothing was lost.
    await Assert.That(healthyFinal.Any(r => r.EnvelopeType.Contains("RedeliveryComposite"))).IsFalse()
      .Because("the bundle is targeted at another service, so the healthy consumer discards it on receipt.");
    await Assert.That(healthyFinal.Count(r => _x(r) == 5)).IsEqualTo(1)
      .Because("the sentinel published after the repair arrived, so the verdict on the bundle is final.");

    var bundle = damagedFinal.Single(r => r.EnvelopeType.Contains("RedeliveryComposite"));
    await Assert.That(bundle.DiscardedOnReceive).IsNull()
      .Because("the damaged service is the target, so the bundle is its work to fan out.");
    var wireOptions = JsonContextRegistry.CreateCombinedOptions();
    var composite = (RedeliveryComposite)JsonSerializer.Deserialize(
      bundle.Envelope.Payload.GetRawText(), wireOptions.GetTypeInfo(typeof(RedeliveryComposite)))!;
    await Assert.That(composite.InnerEventIds).IsEquivalentTo(missingIds)
      .Because("the ORIGINAL event ids crossed the real wire inside the bundle.");
    await Assert.That(composite.InnerPayloads
        .ConvertAll(pd => pd.TryGetProperty("x", out var lower) ? lower.GetInt32() : pd.GetProperty("X").GetInt32())
).IsEquivalentTo([2, 3])
      .Because("the repaired bodies are the original stored bytes, carried RAW through real JSON — " +
               "the origin never rehydrated them.");

    // The dispatch seam's fan-out (the path InboxDispatchWorker runs) expands the wire-delivered
    // bundle against the damaged service's REAL container — children carry the original ids.
    using var scope = damaged.Provider.CreateScope();
    var fanout = CompositeInboxFanout.TryExpand(composite, bundle.Envelope, scope.ServiceProvider);
    await Assert.That(fanout.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(fanout.Children.Select(c => c.MessageId).ToList()).IsEquivalentTo(missingIds)
      .Because("children enter the inbox under the ORIGINAL event ids, so dedup and the event-store " +
               "conflict skip treat re-delivery exactly like the live delivery it replaces — " +
               "convergence is idempotent by identity, not by coincidence.");
    await Assert.That(fanout.Children.Select(c => c.StreamId).ToList()).IsEquivalentTo(new Guid?[] { streamId, streamId })
      .Because("a repaired event is filed under the stream the origin stored it on, so the row it is about receives it.");
    await Assert.That(fanout.Children.Select(c => c.SourceServiceId).ToList())
      .IsEquivalentTo([originServiceId, originServiceId])
      .Because("children carry the ORIGIN's identity across the wire (Phase B windowed accounting).");
    await Assert.That(fanout.Children.Select(c => c.SourceCommitSequence).ToList()).IsEquivalentTo([1L, 2L])
      .Because("each child's ORIGINAL commit sequence survives the wire — a repaired window recounts as filled.");
    await Assert.That(fanout.Children.All(c => c.RestoreIfMissing)).IsTrue()
      .Because("the children are repair traffic: an event deduplication remembers but the event store lost is stored again (#1843).");
  }

  [Test]
  public async Task DroppedDeliveriesOnTwoStreams_RepairInOneBundleAcrossStreams_EachChildFiledUnderItsOwnStreamAsync() {
    // #2268: an answer spans streams. Over the real wire and the real fan-out, the one bundle's children are filed under
    // the streams the origin stored them on, not under the bundle's shared session key.
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("healthy-svc", svc => svc.WithCatalog<ProbeCatalog>())
      .AddService("damaged-svc", svc => svc.WithCatalog<ProbeCatalog>())
      .StartAsync();
    var healthy = harness.GetService("healthy-svc");
    var damaged = harness.GetService("damaged-svc");

    await harness.PublishAsync(new RepairProbeEvent { X = 1 });
    using (harness.SuppressDeliveries("damaged-svc")) {
      await harness.PublishAsync(new RepairProbeEvent { X = 2 });
      await harness.PublishAsync(new RepairProbeEvent { X = 3 });
    }
    await harness.PublishAsync(new RepairProbeEvent { X = 4 });
    var healthyRows = await healthy.Inbox.WaitForInboxAsync(4, TimeSpan.FromSeconds(10));
    _ = await damaged.Inbox.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));

    var missing = healthyRows.Where(r => _x(r) is 2 or 3).OrderBy(_x).ToList();
    var missingIds = missing.ConvertAll(m => m.MessageId);
    Guid[] streams = [.. new[] { TrackedGuid.New().Value, TrackedGuid.New().Value }.Order()];
    var events = missing.Select((row, i) => new RedeliveryEvent {
      EventId = row.MessageId,
      StreamId = streams[i],
      Version = 1,
      CommitSequence = i + 1,
      EventType = TypeNameFormatter.FormatClrTypeName(typeof(RepairProbeEvent)),
      EventData = row.Envelope.Payload.GetRawText(),
    }).ToList();

    var pump = new RedeliveryPump(
      transport: harness.Wire,
      envelopeSerializer: new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()),
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      compositeFactory: new CompositeFactory()) { BundleAcrossStreams = true };
    var originServiceId = TrackedGuid.New().Value;
    var published = await pump.PublishAnswerAsync(
      events, new RedeliveryAnswerWindow(null, null), MultiServiceHarnessDefaults.SHARED_TOPIC, target: "damaged-svc",
      originServiceId: originServiceId);
    await Assert.That(published).IsEqualTo(1).Because("two streams' deficits ride one bundle");

    // Sentinel after the repair: per-subscriber delivery is ordered, so once it lands the bundle has too.
    await harness.PublishAsync(new RepairProbeEvent { X = 5 });
    var damagedFinal = await damaged.Inbox.WaitForInboxAsync(4, TimeSpan.FromSeconds(10));
    var bundle = damagedFinal.Single(r => r.EnvelopeType.Contains("RedeliveryComposite"));
    var composite = (RedeliveryComposite)JsonSerializer.Deserialize(
      bundle.Envelope.Payload.GetRawText(), JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RedeliveryComposite)))!;
    await Assert.That(composite.StreamId).IsEqualTo(
      RedeliveryPump.AnswerSessionFor(originServiceId, MultiServiceHarnessDefaults.SHARED_TOPIC, "damaged-svc", scope: null));

    using var scope = damaged.Provider.CreateScope();
    var fanout = CompositeInboxFanout.TryExpand(composite, bundle.Envelope, scope.ServiceProvider);
    await Assert.That(fanout.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(fanout.Children.Select(c => c.MessageId).ToList()).IsEquivalentTo(missingIds,
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(fanout.Children.Select(c => c.StreamId).ToList()).IsEquivalentTo(streams.Select(s => (Guid?)s).ToList(),
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each child is filed under the stream the origin stored it on, never under the bundle's session key");
    await Assert.That(fanout.Children.Select(c => c.SourceCommitSequence).ToList()).IsEquivalentTo([1L, 2L],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static int _x(InboxMessage row) {
    var payload = row.Envelope.Payload;
    return payload.TryGetProperty("x", out var lower) ? lower.GetInt32() : payload.GetProperty("X").GetInt32();
  }

}

/// <summary>JSON context for the R1c convergence-proof payloads (production-parity registration).</summary>
[JsonSerializable(typeof(StreamIntegrityRedeliveryE2ETests.RepairProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<StreamIntegrityRedeliveryE2ETests.RepairProbeEvent>))]
public sealed partial class StreamIntegrityE2EJsonContext : JsonSerializerContext;
