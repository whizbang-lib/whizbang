using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
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
    var missingIds = missing.Select(m => m.MessageId).ToList();
    var streamId = TrackedGuid.NewMedo().Value;
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
      harness.Wire, new _originStore(), new _typeProvider(),
      new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    var originServiceId = TrackedGuid.NewMedo().Value;
    var published = await pump.PublishAsync(
      events, MultiServiceHarnessDefaults.SHARED_TOPIC, target: "damaged-svc", originServiceId: originServiceId);
    await Assert.That(published).IsEqualTo(1);

    // Sentinel broadcast AFTER the repair: per-subscriber delivery is ordered, so once a service
    // holds the sentinel its verdict on the earlier repair bundle is final — no timing.
    await harness.PublishAsync(new RepairProbeEvent { X = 5 });
    var healthyFinal = await healthy.Inbox.WaitForInboxAsync(5, TimeSpan.FromSeconds(10));
    var damagedFinal = await damaged.Inbox.WaitForInboxAsync(4, TimeSpan.FromSeconds(10));

    await Assert.That(healthyFinal.Any(r => r.EnvelopeType.Contains("RedeliveryComposite"))).IsFalse()
      .Because("the bundle is targeted: an undamaged consumer discards it at the receive seam (R0), " +
               "so repair traffic can never double-apply where nothing was lost.");

    var bundle = damagedFinal.Single(r => r.EnvelopeType.Contains("RedeliveryComposite"));
    var wireOptions = JsonContextRegistry.CreateCombinedOptions();
    var composite = (RedeliveryComposite)JsonSerializer.Deserialize(
      bundle.Envelope.Payload.GetRawText(), wireOptions.GetTypeInfo(typeof(RedeliveryComposite)))!;
    await Assert.That(composite.InnerEventIds).IsEquivalentTo(missingIds)
      .Because("the ORIGINAL event ids crossed the real wire inside the bundle.");
    await Assert.That(composite.Inner.Cast<RepairProbeEvent>().Select(p => p.X).ToList()).IsEquivalentTo([2, 3])
      .Because("the repaired bodies are the original bodies, round-tripped through real JSON bytes.");

    // The dispatch seam's fan-out (the path InboxDispatchWorker runs) expands the wire-delivered
    // bundle against the damaged service's REAL container — children carry the original ids.
    using var scope = damaged.Provider.CreateScope();
    var fanout = CompositeInboxFanout.TryExpand(composite, bundle.Envelope, scope.ServiceProvider);
    await Assert.That(fanout.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(fanout.Children.Select(c => c.MessageId).ToList()).IsEquivalentTo(missingIds)
      .Because("children enter the inbox under the ORIGINAL event ids, so dedup and the event-store " +
               "conflict skip treat re-delivery exactly like the live delivery it replaces — " +
               "convergence is idempotent by identity, not by coincidence.");
    await Assert.That(fanout.Children.Select(c => c.SourceServiceId).ToList())
      .IsEquivalentTo([originServiceId, originServiceId])
      .Because("children carry the ORIGIN's identity across the wire (Phase B windowed accounting).");
    await Assert.That(fanout.Children.Select(c => c.SourceCommitSequence).ToList()).IsEquivalentTo([1L, 2L])
      .Because("each child's ORIGINAL commit sequence survives the wire — a repaired window recounts as filled.");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static int _x(InboxMessage row) {
    var payload = row.Envelope.Payload;
    return payload.TryGetProperty("x", out var lower) ? lower.GetInt32() : payload.GetProperty("X").GetInt32();
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(RepairProbeEvent)];
  }

  /// <summary>The origin's store: rehydrates each selected row's stored JSON body back into the
  /// typed payload envelope, MessageId = the original EventId — the real store's AOT path shape.</summary>
  private sealed class _originStore : IEventStore {
    private static readonly JsonSerializerOptions _options = JsonContextRegistry.CreateCombinedOptions();

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      [.. streamEvents.Select(raw => new MessageEnvelope<IEvent> {
        MessageId = new MessageId(raw.EventId),
        Payload = (RepairProbeEvent)JsonSerializer.Deserialize(raw.EventData, _options.GetTypeInfo(typeof(RepairProbeEvent)))!,
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
}

/// <summary>JSON context for the R1c convergence-proof payloads (production-parity registration).</summary>
[JsonSerializable(typeof(StreamIntegrityRedeliveryE2ETests.RepairProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<StreamIntegrityRedeliveryE2ETests.RepairProbeEvent>))]
public sealed partial class StreamIntegrityE2EJsonContext : JsonSerializerContext;
