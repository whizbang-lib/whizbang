// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A redelivery bundle reconciled with the events the receiver already holds (#1790): an event stored before the
/// store recorded origins adopts the origin the bundle carries, and a bundle that changes nothing marks its buckets
/// as ones repair cannot change, reported once.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryReconciliation.cs</code-under-test>
/// <docs>resilience/stream-integrity#pre-stamp-history</docs>
[Category("Messaging")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S6966:Awaitable method should be used", Justification = "The in-process ledger's synchronous API sets up and reads the state under test.")]
public class RedeliveryReconcilerTests {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

  private sealed class AdoptingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IReadOnlyList<RedeliveredBucketPresence>? Answer { get; set; } = [];
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public Guid? Origin { get; private set; }
    public string? OriginName { get; private set; }
    public IReadOnlyList<Guid> EventIds { get; private set; } = [];
    public IReadOnlyList<long?> Sequences { get; private set; } = [];

    public Task<IReadOnlyList<RedeliveredBucketPresence>?> AdoptRedeliveredOriginAsync(
        Guid originServiceId, string? originServiceName, IReadOnlyList<Guid> eventIds, IReadOnlyList<long?> commitSequences,
        CancellationToken cancellationToken = default) {
      Calls++;
      Origin = originServiceId;
      OriginName = originServiceName;
      EventIds = eventIds;
      Sequences = commitSequences;
      return Failure is not null ? Task.FromException<IReadOnlyList<RedeliveredBucketPresence>?>(Failure) : Task.FromResult(Answer);
    }
  }

  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static RedeliveryComposite _bundle(Guid origin, Guid stream, int events) => new() {
    StreamId = stream,
    OriginServiceId = origin,
    InnerEventIds = [.. Enumerable.Range(0, events).Select(_ => TrackedGuid.New().Value)],
    InnerTypeNames = [.. Enumerable.Range(0, events).Select(_ => EVENT_TYPE)],
    InnerPayloads = [.. Enumerable.Range(0, events).Select(_ => JsonElement.Parse("{}"))],
    InnerCommitSequences = [.. Enumerable.Range(0, events).Select(i => (long?)(i + 1))],
  };

  private static List<InboxMessage> _children(RedeliveryComposite bundle, Guid origin) =>
    [.. bundle.InnerEventIds.Select((id, i) => new InboxMessage {
      MessageId = id,
      HandlerName = "OrderPlacedHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = new MessageId(id),
        Payload = bundle.InnerPayloads[i],
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Inbox },
      },
      EnvelopeType = "MessageEnvelope",
      StreamId = bundle.StreamId,
      IsEvent = true,
      MessageType = EVENT_TYPE,
      SourceServiceId = origin,
      // The second child arrives without an origin sequence: the origin had not stamped it.
      SourceCommitSequence = i == 1 ? 0 : bundle.InnerCommitSequences![i]!.Value,
    })];

  private static ServiceProvider _services(AdoptingCoordinator? coordinator, IIntegrityRepairLedger? ledger,
      StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    if (ledger is not null) {
      services.AddSingleton(ledger);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Reconcile_EventsStoredWithoutTheirOrigin_AdoptTheOriginTheBundleCarriesAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 3);
    var children = _children(bundle, origin);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, Present: 3, Adopted: 3)] };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, logger, _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(Children: 3, Present: 3, Adopted: 3, MarkedUnhealable: 0));
    await Assert.That(coordinator.Origin).IsEqualTo(origin);
    await Assert.That(coordinator.EventIds).IsEquivalentTo(bundle.InnerEventIds);
    await Assert.That(coordinator.Sequences).IsEquivalentTo(new long?[] { 1, null, 3 })
      .Because("a zero sequence is the origin's 'not stamped yet', which must never fill a column as a real position");
    await Assert.That(ledger.TryBeginRepair(key, _now, TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("a redelivery that adopted origins changed the bucket, so repair is not given up on");
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Information)).IsEqualTo(1);
  }

  [Test]
  public async Task Reconcile_EveryEventAlreadyPresentAndNothingAdopted_MarksTheBucketUnhealableOnceAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 2);
    var children = _children(bundle, origin);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, "tenant-a", EVENT_TYPE, Present: 2, Adopted: 0)] };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);
    var logger = new CapturingLogger();

    var first = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, logger, _now);
    var second = await RedeliveryReconciler.ReconcileAsync(bundle, children, services, logger, _now.AddMinutes(1));

    await Assert.That(first).IsEqualTo(new RedeliveryReconciliation(2, 2, 0, MarkedUnhealable: 1));
    await Assert.That(second.MarkedUnhealable).IsEqualTo(0)
      .Because("a bucket already marked is not reported again");
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1)
      .Because("an operator hears about a bucket repair cannot change exactly once");
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsFalse()
      .Because("asking again would ship the same events again");
  }

  [Test]
  public async Task Reconcile_SomeEventsNotYetStored_MarksNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 3);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, Present: 2, Adopted: 0)] };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(3, 2, 0, 0))
      .Because("the event that was missing is stored by this redelivery, which is the repair working");
    await Assert.That(ledger.TryBeginRepair(key, _now, TimeSpan.FromSeconds(1), 3)).IsTrue();
  }

  [Test]
  public async Task Reconcile_WithMetrics_CountsAdoptedEventsAndUnhealableBucketsAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<(string Name, long Value)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.OriginsAdopted.Instrument) || ReferenceEquals(instrument, metrics.RepairsUnhealable.Instrument)
          || ReferenceEquals(instrument, metrics.OriginsRefused.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (readings) { readings.Add((instrument.Name, value)); }
    });
    listener.Start();
    var origin = TrackedGuid.New().Value;
    var adoptedStream = TrackedGuid.New().Value;
    var unchangedStream = TrackedGuid.New().Value;
    var ledger = new IntegrityRepairLedger();
    _ = ledger.TryBeginReport(new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, unchangedStream), 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    var coordinator = new AdoptingCoordinator();
    await using var services = _services(coordinator, ledger, metrics);
    var adoptedBundle = _bundle(origin, adoptedStream, 2);
    var unchangedBundle = _bundle(origin, unchangedStream, 1);

    coordinator.Answer = [new(adoptedStream, null, EVENT_TYPE, 2, 2)];
    _ = await RedeliveryReconciler.ReconcileAsync(adoptedBundle, _children(adoptedBundle, origin), services, new CapturingLogger(), _now);
    coordinator.Answer = [new(unchangedStream, null, EVENT_TYPE, 1, 0)];
    _ = await RedeliveryReconciler.ReconcileAsync(unchangedBundle, _children(unchangedBundle, origin), services, new CapturingLogger(), _now);
    listener.RecordObservableInstruments();

    await Assert.That(readings).Contains(("whizbang.stream_integrity.origins_adopted", 2L));
    await Assert.That(readings).Contains(("whizbang.stream_integrity.repairs_unhealable", 1L));
    await Assert.That(readings.Any(r => r.Name == "whizbang.stream_integrity.origins_refused" && r.Value > 0)).IsFalse()
      .Because("neither bundle had an event whose first hop names another service");
  }

  // ---- refused restores (#1843) ------------------------------------------------------------------

  [Test]
  public async Task Reconcile_ARestoreTheStoreRefused_MarksTheBucketUnhealableOnce_NamingTheReasonAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 1);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, Present: 0, Adopted: 0, RefusedTruncated: 1)] };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);
    var logger = new CapturingLogger();

    var first = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now);
    var second = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now.AddMinutes(1));

    await Assert.That(first).IsEqualTo(new RedeliveryReconciliation(1, 0, 0, MarkedUnhealable: 1, RefusedTruncated: 1));
    await Assert.That(second.MarkedUnhealable).IsEqualTo(0).Because("a bucket already marked is not reported again");
    await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains("truncated")
      .Because("the report names why repair cannot restore the bucket");
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsFalse()
      .Because("asking again would be refused again");
  }

  [Test]
  public async Task Reconcile_ARefusalBesideAdoptedHistory_MarksOnlyTheRefusedBucketAsync() {
    var origin = TrackedGuid.New().Value;
    var adoptedStream = TrackedGuid.New().Value;
    var refusedStream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, adoptedStream, 3);
    var coordinator = new AdoptingCoordinator {
      Answer = [new(adoptedStream, null, EVENT_TYPE, 2, 2), new(refusedStream, null, EVENT_TYPE, 0, 0, RefusedTruncated: 1)],
    };
    var ledger = new IntegrityRepairLedger();
    var adoptedKey = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, adoptedStream);
    var refusedKey = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, refusedStream);
    _ = ledger.TryBeginReport(adoptedKey, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    _ = ledger.TryBeginReport(refusedKey, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(3, 2, 2, MarkedUnhealable: 1, RefusedTruncated: 1));
    await Assert.That(ledger.TryBeginRepair(adoptedKey, _now, TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("the bucket whose history adopted its origin changed, so repair is not given up on");
    await Assert.That(ledger.TryBeginRepair(refusedKey, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsFalse();
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Information)).IsEqualTo(1);
  }

  [Test]
  public async Task Reconcile_EveryChildPresentOrRefused_MarksBothBuckets_EachWithItsOwnReasonAsync() {
    var origin = TrackedGuid.New().Value;
    var presentStream = TrackedGuid.New().Value;
    var refusedStream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, presentStream, 2);
    var coordinator = new AdoptingCoordinator {
      Answer = [new(presentStream, null, EVENT_TYPE, 1, 0), new(refusedStream, null, EVENT_TYPE, 0, 0, RefusedTruncated: 1)],
    };
    var ledger = new IntegrityRepairLedger();
    _ = ledger.TryBeginReport(new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, presentStream), 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    _ = ledger.TryBeginReport(new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, refusedStream), 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(2, 1, 0, MarkedUnhealable: 2, RefusedTruncated: 1))
      .Because("nothing was stored and nothing adopted, so neither bucket can change");
    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
    await Assert.That(warnings.Count).IsEqualTo(2);
    await Assert.That(warnings.Count(w => w.Contains("truncated"))).IsEqualTo(1)
      .Because("only the refused bucket is reported with the truncation reason");
  }

  [Test]
  public async Task Reconcile_ARefusalBesideEventsBeingStored_MarksOnlyTheRefusedBucketAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var refusedStream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 3);
    var coordinator = new AdoptingCoordinator {
      Answer = [new(stream, null, EVENT_TYPE, 1, 0), new(refusedStream, null, EVENT_TYPE, 0, 0, RefusedTruncated: 1)],
    };
    var ledger = new IntegrityRepairLedger();
    var storingKey = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(storingKey, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    _ = ledger.TryBeginReport(new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, refusedStream), 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(3, 1, 0, MarkedUnhealable: 1, RefusedTruncated: 1));
    await Assert.That(ledger.TryBeginRepair(storingKey, _now, TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("a child of this bundle is being stored, which is the repair working");
  }

  [Test]
  public async Task Reconcile_ARefusalWithoutALedger_ReportsTheRefusalWithoutMarkingAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 1);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, 0, 0, RefusedTruncated: 1)] };
    await using var services = _services(coordinator, null);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(1, 0, 0, 0, RefusedTruncated: 1));
  }

  [Test]
  public async Task Reconcile_WithMetrics_CountsARefusedBucketAsUnhealableAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<long>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.RepairsUnhealable.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => {
      lock (readings) { readings.Add(value); }
    });
    listener.Start();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var ledger = new IntegrityRepairLedger();
    _ = ledger.TryBeginReport(new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream), 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, 0, 0, RefusedTruncated: 1)] };
    await using var services = _services(coordinator, ledger, metrics);
    var bundle = _bundle(origin, stream, 1);

    _ = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);
    listener.RecordObservableInstruments();

    await Assert.That(readings).IsEquivalentTo([1L]);
  }

  [Test]
  public async Task Reconcile_PassesTheOriginsServiceName_ToTheStoreAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 1);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, 1, 1)] };
    await using var services = _services(coordinator, new IntegrityRepairLedger());

    _ = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now, "orders-service");

    await Assert.That(coordinator.OriginName).IsEqualTo("orders-service")
      .Because("the store adopts only events whose first hop names the bundle's origin");
  }

  [Test]
  public async Task Reconcile_EventsWhoseFirstHopNamesAnotherService_AreCountedAndLoggedOnceAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<(string Name, long Value)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.OriginsRefused.Instrument) || ReferenceEquals(instrument, metrics.OriginsAdopted.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (readings) { readings.Add((instrument.Name, value)); }
    });
    listener.Start();
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var otherStream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 3);
    var coordinator = new AdoptingCoordinator {
      Answer = [new(stream, null, EVENT_TYPE, Present: 2, Adopted: 1, RefusedForeignOrigin: 1), new(otherStream, null, EVENT_TYPE, 1, 0, RefusedForeignOrigin: 1)],
    };
    await using var services = _services(coordinator, new IntegrityRepairLedger(), metrics);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now, "orders-service");
    listener.RecordObservableInstruments();

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(3, 3, 1, 0, RefusedForeignOrigin: 2));
    await Assert.That(readings).Contains(("whizbang.stream_integrity.origins_refused", 2L));
    await Assert.That(readings).Contains(("whizbang.stream_integrity.origins_adopted", 1L));
    var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning).Message;
    await Assert.That(warning).Contains(origin.ToString()).And.Contains("orders-service").And.Contains("2")
      .Because("the refusal is reported once per bundle, naming the origin and how many events it did not adopt");
  }

  [Test]
  public async Task Reconcile_EveryPresentEventRefused_MarksTheBucketUnhealable_AndReportsTheRefusalAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 1);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, Present: 1, Adopted: 0, RefusedForeignOrigin: 1)] };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    await using var services = _services(coordinator, ledger);
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now, null);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(1, 1, 0, MarkedUnhealable: 1, RefusedForeignOrigin: 1))
      .Because("a redelivery the first-hop check refuses will be refused again, so repair cannot change the bucket");
    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(2)
      .Because("the refusal and the bucket repair cannot change are each reported once");
  }

  [Test]
  public async Task OriginServiceNameOf_TheBundlesFirstHop_NamesTheOriginAsync() {
    var envelope = _envelope(
      new MessageHop { ServiceInstance = _instance("orders-service"), Timestamp = _now },
      new MessageHop { ServiceInstance = _instance("billing-service"), Timestamp = _now });

    await Assert.That(RedeliveryReconciler.OriginServiceNameOf(envelope)).IsEqualTo("orders-service");
  }

  [Test]
  public async Task OriginServiceNameOf_NoHops_IsNullAsync() {
    await Assert.That(RedeliveryReconciler.OriginServiceNameOf(_envelope())).IsNull();
  }

  [Test]
  public async Task OriginServiceNameOf_AFirstHopWithoutAServiceIdentity_IsNullAsync() {
    var envelope = _envelope(new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = _now });

    await Assert.That(RedeliveryReconciler.OriginServiceNameOf(envelope)).IsNull()
      .Because("the unknown sentinel is no identity to compare a stored hop with");
  }

  [Test]
  public async Task OriginServiceNameOf_AnEnvelopeReadWithoutHops_IsNullAsync() {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(TrackedGuid.New().Value),
      Payload = JsonElement.Parse("{}"),
      Hops = null!,
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Inbox },
    };

    await Assert.That(RedeliveryReconciler.OriginServiceNameOf(envelope)).IsNull()
      .Because("an envelope deserialized without a hop list carries no origin name");
  }

  [Test]
  public async Task OriginServiceNameOf_AFirstHopWithABlankServiceName_IsNullAsync() {
    var envelope = _envelope(new MessageHop { ServiceInstance = _instance(" "), Timestamp = _now });

    await Assert.That(RedeliveryReconciler.OriginServiceNameOf(envelope)).IsNull();
  }

  [Test]
  public async Task OriginServiceNameOf_NullEnvelope_ThrowsAsync() {
    await Assert.That(() => RedeliveryReconciler.OriginServiceNameOf(null!)).Throws<ArgumentNullException>();
  }

  private static ServiceInstanceInfo _instance(string service) =>
    new() { ServiceName = service, InstanceId = TrackedGuid.New().Value, HostName = "host", ProcessId = 1 };

  private static MessageEnvelope<JsonElement> _envelope(params MessageHop[] hops) => new() {
    MessageId = new MessageId(TrackedGuid.New().Value),
    Payload = JsonElement.Parse("{}"),
    Hops = [.. hops],
    DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Inbox },
  };

  [Test]
  public async Task Reconcile_BundleWithoutAnOrigin_AsksTheStoreNothingAsync() {
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(Guid.Empty, stream, 1);
    var coordinator = new AdoptingCoordinator();
    await using var services = _services(coordinator, new IntegrityRepairLedger());

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, Guid.Empty), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(RedeliveryReconciliation.None);
    await Assert.That(coordinator.Calls).IsEqualTo(0)
      .Because("without an origin there is no identity to adopt and no bucket to key");
  }

  [Test]
  public async Task Reconcile_NoChildrenKept_AsksTheStoreNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var bundle = _bundle(origin, TrackedGuid.New().Value, 1);
    var coordinator = new AdoptingCoordinator();
    await using var services = _services(coordinator, new IntegrityRepairLedger());

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, [], services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(RedeliveryReconciliation.None);
    await Assert.That(coordinator.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task Reconcile_NoCoordinatorInTheScope_DoesNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var bundle = _bundle(origin, TrackedGuid.New().Value, 1);
    await using var services = _services(null, new IntegrityRepairLedger());

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(RedeliveryReconciliation.None);
  }

  [Test]
  public async Task Reconcile_StoreCannotAnswer_DoesNothingAsync() {
    var origin = TrackedGuid.New().Value;
    var bundle = _bundle(origin, TrackedGuid.New().Value, 1);
    var coordinator = new AdoptingCoordinator { Answer = null };
    await using var services = _services(coordinator, new IntegrityRepairLedger());

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(RedeliveryReconciliation.None);
    await Assert.That(coordinator.Calls).IsEqualTo(1);
  }

  [Test]
  public async Task Reconcile_NoLedgerRegistered_ReportsPresenceWithoutMarkingAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 1);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, 1, 0)] };
    await using var services = _services(coordinator, null);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, new CapturingLogger(), _now);

    await Assert.That(result).IsEqualTo(new RedeliveryReconciliation(1, 1, 0, 0))
      .Because("a fresh ledger would remember nothing, so there is nothing to mark");
  }

  [Test]
  public async Task Reconcile_InProcessLedgerRegisteredByItsConcreteType_IsTheOneMarkedAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var bundle = _bundle(origin, stream, 1);
    var coordinator = new AdoptingCoordinator { Answer = [new(stream, null, EVENT_TYPE, 1, 0)] };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, TimeSpan.FromMinutes(5));
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(ledger);
    await using var provider = services.BuildServiceProvider();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), provider, new CapturingLogger(), _now);

    await Assert.That(result.MarkedUnhealable).IsEqualTo(1)
      .Because("the in-process ledger the host registers by its own type is the one repair consults");
  }

  [Test]
  public async Task Reconcile_StoreFails_LogsWhatFailedAndReturnsNoneAsync() {
    var origin = TrackedGuid.New().Value;
    var bundle = _bundle(origin, TrackedGuid.New().Value, 1);
    var coordinator = new AdoptingCoordinator { Failure = new InvalidOperationException("store down") };
    await using var services = _services(coordinator, new IntegrityRepairLedger());
    var logger = new CapturingLogger();

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle, origin), services, logger, _now);

    await Assert.That(result).IsEqualTo(RedeliveryReconciliation.None);
    await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains(origin.ToString())
      .Because("a failed reconciliation names the origin it was for; repair asks again on its own schedule");
  }

  [Test]
  public async Task Reconcile_Canceled_PropagatesAsync() {
    var origin = TrackedGuid.New().Value;
    var bundle = _bundle(origin, TrackedGuid.New().Value, 1);
    var coordinator = new AdoptingCoordinator { Failure = new OperationCanceledException() };
    await using var services = _services(coordinator, new IntegrityRepairLedger());

    await Assert.That(async () => await RedeliveryReconciler.ReconcileAsync(
      bundle, _children(bundle, origin), services, new CapturingLogger(), _now)).Throws<OperationCanceledException>();
  }
}
