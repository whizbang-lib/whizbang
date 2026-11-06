// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests (no Postgres) for the stream-integrity Phase B consumer bridge:
/// <see cref="IntegrityCheckpointReceptor"/> verifies a received checkpoint against local receipt
/// counts. Deficits are PENDING on first sight; a deficit persisting past the origin's NEXT
/// checkpoint CONFIRMS (report event; ladder-gated wire-only targeted repair request); healed
/// deficits clear silently; own checkpoints and unsubscribed types are ignored.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityGapTracker.cs</code-under-test>
[Category("Shard4")]
public class IntegrityCheckpointReceptorTests {
  // A fixed instant: these tests pass "now" explicitly, so it never needs the real clock.
  private static readonly DateTimeOffset _fixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);


  public sealed record VerifiedEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  // The wire form ("Type, Assembly") — checkpoint buckets are built from wh_event_store.event_type,
  // so the subscribed-type filter must match THAT form or fresh-window verification silently skips
  // every bucket.
  private static readonly string _verifiedType = TypeNameFormatter.Format(typeof(VerifiedEvent));

  [Test]
  public async Task FirstDeficit_IsPendingOnly_NoReportAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [];   // consumer has NOTHING in the window

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a first-sight deficit is PENDING — two-cycle confirmation absorbs in-flight stragglers.");
    await Assert.That(fx.Transport.Published).IsEmpty();
  }

  [Test]
  public async Task ManyConfirmedGaps_ReportsAreCapped_WithOneSummaryAsync() {
    // Found by scripts/Lint-UnboundedFanOut.ps1, not by review: MaxAutoRepairRequestsPerCheckpoint
    // caps the REPAIRS in this loop while the IntegrityGapDetected publish next to it runs free.
    // That is the same asymmetry that took the fleet down through the manifest comparator -- each
    // report is a durable outbox write, so one per confirmed gap is unbounded sequential I/O
    // inside a single handler, and pendings grow with (tenant x event type), not with a batch size.
    const int cap = 5;
    var fx = _fixture(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.ReportOnly,
      MaxGapReportsPerCheckpoint = cap,
      PublishReportEvents = true,
    });
    // Every bucket stays short on the recount, so every pending confirms as a real gap.
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 0 }];

    var tenants = Enumerable.Range(1, 60).Select(i => $"tenant-{i}").ToList();
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3, tenantScopes: tenants));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    var reports = fx.Dispatcher.Published.OfType<IntegrityGapDetected>().ToList();
    await Assert.That(reports.Count).IsLessThanOrEqualTo(cap)
      .Because("each gap report is a durable write; an unbounded fan-out starves the pipeline.");
    await Assert.That(reports.Count).IsGreaterThan(0)
      .Because("capping must not silence gaps entirely — the first ones still have to be named.");
  }

  [Test]
  public async Task DeficitPersistingPastNextCheckpoint_ConfirmsAndReportsAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));   // deficit → pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));   // next cycle

    var report = (IntegrityGapDetected)fx.Dispatcher.Published.Single();
    await Assert.That(report.EventType).IsEqualTo(_verifiedType);
    await Assert.That(report.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(report.ExpectedCount).IsEqualTo(3);
    await Assert.That(report.ActualCount).IsEqualTo(1)
      .Because("the report carries the recount at confirmation time, not the original sighting.");
    await Assert.That(report.OriginServiceId).IsEqualTo(fx.OriginId);
    await Assert.That(report.AutoRepairRequested).IsFalse()
      .Because("the ReportOnly rung reports without repairing — the operator's explicit opt-down.");
    await Assert.That(fx.Transport.Published).IsEmpty();
  }

  [Test]
  public async Task DeficitHealedBeforeNextCheckpoint_ClearsSilentlyAsync() {
    var fx = _fixture();
    var calls = 0;
    fx.Coordinator.Counts = _ => ++calls == 1
      ? []   // first sight: nothing received yet
      : [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 3 }];   // straggler landed

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a deficit that heals before the next checkpoint was an in-flight straggler, not a gap.");
  }

  /// <summary>
  /// Sibling of ManifestReceptor_ByDefault_DetectsAndRepairsButPublishesNoReports, for the
  /// checkpoint path: a confirmed gap is still detected and still repaired, but publishes no
  /// durable <see cref="IntegrityGapDetected"/> unless the operator opts in.
  /// </summary>
  [Test]
  public async Task ByDefault_ConfirmedGap_RepairsWithoutPublishingAReportAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Dispatcher.Published.OfType<IntegrityGapDetected>().Any()).IsFalse()
      .Because("the gap is real and recorded; publishing an event nobody consumes is not how it "
               + "gets recorded");
    await Assert.That(fx.Transport.Published.Count).IsEqualTo(1)
      .Because("the repair request is the half of this that actually fixes the deficit");
  }

  [Test]
  public async Task AutoRepairCapped_SendsTargetedWireOnlyRepairRequestAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    var report = (IntegrityGapDetected)fx.Dispatcher.Published.Single();
    await Assert.That(report.AutoRepairRequested).IsTrue();

    var (envelope, destination, _) = fx.Transport.Published.Single();
    await Assert.That(destination.Address).IsEqualTo("origin.requests")
      .Because("the request publishes to the ORIGIN-carried address — never to the requester's own topic.");
    await Assert.That(ControlMessageTtl.FromMetadata(destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60))
      .Because("#2078: a directed integrity request expires at the broker instead of piling up");
    await Assert.That(envelope.Target).IsEqualTo("origin-svc")
      .Because("the request is DIRECTED at the origin — only it should run the selection.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var command = (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
    await Assert.That(command.FromCommitSequence).IsEqualTo(10L);
    await Assert.That(command.ToCommitSequence).IsEqualTo(20L)
      .Because("the repair is scoped to EXACTLY the confirmed window.");
    await Assert.That(command.EventTypes).IsEquivalentTo([_verifiedType]);
    await Assert.That(command.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(command.RequesterService).IsEqualTo("consumer-svc")
      .Because("the requester names itself — it becomes the returned bundles' Target.");
    await Assert.That(command.Topic).IsEqualTo("inbox");
  }

  [Test]
  public async Task ConfirmedGap_NoOriginRequestTopic_WithholdsRepairRequestAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true));

    await Assert.That(fx.Dispatcher.Published.Cast<IntegrityGapDetected>().Single().ExpectedCount).IsEqualTo(4)
      .Because("the confirmed gap still reports — only the misroutable request is withheld.");
    await Assert.That(fx.Transport.Published).IsEmpty()
      .Because("an origin that never announced a request address cannot be asked without " +
               "broadcasting off the requester's own topic — the observed all-to-all flood. " +
               "The origin's next checkpoint carries the address; the gap re-confirms then.");
  }

  [Test]
  public async Task OwnCheckpoint_IsIgnoredAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [];

    var own = new IntegrityCheckpoint {
      CheckpointStreamId = fx.Coordinator.LocalServiceId,
      OriginServiceId = fx.Coordinator.LocalServiceId,
      OriginServiceName = "consumer-svc",
      FromCommitSequence = 0,
      ToCommitSequence = 5,
      Buckets = [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 3 }],
    };
    await fx.Receptor.HandleAsync(own);
    await fx.Receptor.HandleAsync(own with { FromCommitSequence = 5 });

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("locally-originated events persist no origin stamp — a self-count would always " +
               "read zero and every bucket would false-alarm.");
  }

  [Test]
  public async Task UnsubscribedType_IsNotVerifiedAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [];

    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = fx.OriginId,
      OriginServiceId = fx.OriginId,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 0,
      ToCommitSequence = 5,
      Buckets = [new CheckpointBucket { TenantScope = "tenant-a", EventType = "Contracts.NotMine", Count = 7 }],
    };
    await fx.Receptor.HandleAsync(checkpoint);
    await fx.Receptor.HandleAsync(checkpoint with { FromCommitSequence = 5, Buckets = [] });

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a type this consumer does not subscribe to is someone else's contract with the origin.");
  }

  [Test]
  public async Task Registrar_RegistersReceptorAtThreeDefaultStagesAsync() {
    var registry = new RecordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityCheckpointReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Count).IsEqualTo(3);
    await Assert.That(registry.Registered.All(r => r.Msg == typeof(IntegrityCheckpoint))).IsTrue();
  }

  private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<IntegrityCheckpointReceptor> {
    public List<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) { Entries.Add((logLevel, eventId.Id, formatter(state, exception))); }
    }
  }

  [Test]
  public async Task DeficitWhileConsumerBehind_IsDeferredNotConfirmedAsync() {
    // #667 half 1: during a bulk ingest the producer runs ahead by design — the deficit is
    // in-flight lag, not loss. Confirming it (and warning, per type, per cycle) misreads
    // ordinary back-pressure as data loss. While the service is measurably unsettled the
    // pending DEFERS: no confirmation, no warning, carried to a later cycle. Once settled,
    // a deficit that persists is real and confirms exactly as before.
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var meter = metrics.GapsDetected.Meter;
    long gaps = 0;
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "whizbang.stream_integrity.gaps_detected") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref gaps, value));
    listener.Start();
    // Passive counters (#711) report every series' cumulative value at collection, so a read is a
    // fresh collection, not an accumulation across reads.
    long readGaps() {
      Interlocked.Exchange(ref gaps, 0);
      listener.RecordObservableInstruments();
      return Interlocked.Read(ref gaps);
    }

    var fx = _fixture(metrics: metrics);
    fx.Coordinator.Counts = _ => [];
    fx.Coordinator.Backlog = new ServiceBacklog {
      UnprocessedInboxRows = 500,
      ActiveLeasedRows = 12,
      OldestUnprocessedAge = TimeSpan.FromSeconds(30),
    };

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));                     // deficit -> pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true)); // would confirm today

    await Assert.That(readGaps()).IsEqualTo(0L)
      .Because("a deficit measured while the consumer is visibly behind is expected back-"
             + "pressure — CONFIRMED must mean the pipeline is drained and the events are "
             + "genuinely absent");

    fx.Coordinator.Backlog = new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 };
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true)); // settled -> confirms

    await Assert.That(readGaps()).IsEqualTo(1L)
      .Because("the deferral carries the pending forward — a deficit that survives the "
             + "drain is a real gap and must still confirm");
  }

  [Test]
  public async Task SameWindowReconfirmed_WarnsOnceThenLogsQuietlyAsync() {
    // #667 half 2: an origin that keeps checkpointing the same watermark re-registers the
    // same deficit every cycle, and each re-confirmation logged a fresh WARNING — hundreds
    // of identical lines for one condition. The first confirmation of a window warns —
    // re-confirmations of the SAME window log at Debug (the condition is already surfaced
    // and stays countable on the meter).
    var logger = new CaptureLogger();
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings {
      RecountBackoffAfterUnchanged = 99,   // keep recounts flowing — the governor is not under test
    });
    var fx = _fixture(policy: policy, logger: logger);
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));  // pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));  // confirms + re-registers
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));  // re-confirms same window

    List<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> entries;
    lock (logger.Entries) { entries = [.. logger.Entries]; }
    var confirmedWarnings = entries.Count(e =>
      e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("CONFIRMED integrity gap:"));
    await Assert.That(confirmedWarnings).IsEqualTo(1)
      .Because("one condition earns one warning — per-cycle repeats of the identical line "
             + "bury the log precisely when there is most to read");
    await Assert.That(entries.Any(e =>
        e.Level == Microsoft.Extensions.Logging.LogLevel.Debug && e.Message.Contains("re-confirmed"))).IsTrue()
      .Because("the re-confirmation is still visible at Debug for forensic timelines");
  }

  // ── fixture ─────────────────────────────────────────────────────────────

  private sealed class FixtureState {
    public required VerifyCoordinator Coordinator { get; init; }
    public required CaptureDispatcher Dispatcher { get; init; }
    public required CaptureTransport Transport { get; init; }
    public required IntegrityCheckpointReceptor Receptor { get; init; }
    public required Whizbang.Core.Messaging.IntegrityRepairLedger Ledger { get; init; }
    public required RecordingIntegrityRepairLedger Recorder { get; init; }
    public Guid OriginId { get; } = TrackedGuid.New().Value;
  }

  [Test]
  public async Task AConfirmedGapsRepairRequest_MarksTheTypeLaneAnswerPending_OverTheWindowItAskedForAsync() {
    // #2034: the checkpoint's whole-window request answers the per-stream drain rows inside that window too.
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairAnswerTimeoutMinutes = 33 });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    var mark = fx.Recorder.PendingMarks.Single();
    await Assert.That(mark.Keys.Single()).IsEqualTo(
      new Whizbang.Core.Messaging.IntegrityRepairLedger.DivergenceKey(fx.OriginId, "tenant-a", _verifiedType, Guid.Empty));
    await Assert.That(mark.WindowFrom).IsEqualTo(11L)
      .Because("the request's exclusive floor 10 is the ledger window's first sequence 11, as the drain converts it");
    await Assert.That(mark.WindowUntil).IsEqualTo(21L);
    await Assert.That(mark.Timeout).IsEqualTo(TimeSpan.FromMinutes(33));
  }

  [Test]
  public async Task AConfirmedGapWithoutTheOriginsAddress_MarksNothingPendingAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true));

    await Assert.That(fx.Transport.Published).IsEmpty();
    await Assert.That(fx.Recorder.PendingMarks).IsEmpty().Because("no request left, so no answer is on its way");
  }

  [Test]
  public async Task AGapThatHealsAfterARepair_ForgetsItsTypeLaneLedgerEntryAsync() {
    // #2034: the checkpoint path's ledger row was never healed, so a later gap of the same type inherited its spent
    // attempts and backoff.
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    var secondWindowCounts = 0;
    fx.Coordinator.Counts = w => w.From == 20 && ++secondWindowCounts > 1
      ? [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 4 }]
      : [];
    var key = new Whizbang.Core.Messaging.IntegrityRepairLedger.DivergenceKey(fx.OriginId, "tenant-a", _verifiedType, Guid.Empty);

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 30, count: 4, requestTopic: "origin.requests"));
    await Assert.That(fx.Transport.Published.Count).IsEqualTo(1).Because("precondition: the first window was confirmed and repaired");
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 30, to: 30, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(await fx.Ledger.TryBeginRepairAsync(key, _fixedNow, TimeSpan.FromHours(1), 8)).IsTrue()
      .Because("the type lane healed, so its entry is forgotten and a later gap starts a fresh ladder");
  }

  [Test]
  public async Task DeficitWhileConsumerBehind_DeferralLineSaysRepairIsWithheldAsync() {
    // Issue #708: the deferral guard returns before the confirmation, so the "auto-repair
    // WITHHELD" line that explained a confirmed gap with autoRepair=false could never fire. The
    // distinction it carried (a service deliberately withholding repair while it drains reads the
    // same as one with repair disabled) now lives on the deferral line itself, so the operator
    // reading the deferral knows both facts from the one line that does fire.
    var logger = new CaptureLogger();
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }, logger: logger);
    fx.Coordinator.Counts = _ => [];
    fx.Coordinator.Backlog = new ServiceBacklog {
      UnprocessedInboxRows = 500,
      ActiveLeasedRows = 12,
      OldestUnprocessedAge = TimeSpan.FromSeconds(30),
    };

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    var deferrals = logger.Entries.Where(e => e.EventId == 62).ToList();
    await Assert.That(deferrals).IsNotEmpty().Because("the unsettled service defers the deficit");
    await Assert.That(deferrals.All(d => d.Message.Contains("repair", StringComparison.OrdinalIgnoreCase)
                                       && d.Message.Contains("withheld", StringComparison.OrdinalIgnoreCase))).IsTrue()
      .Because("a deferral while repair is enabled must say repair is withheld until the service settles, or it reads like repair is off");
    await Assert.That(logger.Entries.Any(e => e.EventId == 59)).IsFalse()
      .Because("the separate withheld line sat behind the deferral's continue and could never fire; it is folded, not kept");
  }

  private static FixtureState _fixture(
      StreamIntegrityOptions? options = null,
      Whizbang.Core.Observability.StreamIntegrityMetrics? metrics = null,
      IntegrityRepairPolicy? policy = null,
      Microsoft.Extensions.Logging.ILogger<IntegrityCheckpointReceptor>? logger = null,
      bool withMetrics = true,
      VerifyCoordinator? coordinator = null) {
    coordinator ??= new VerifyCoordinator();
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<ITransport>(transport);
    if (withMetrics) {
      services.AddSingleton(metrics
        ?? new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>())));
    }
    services.AddSingleton(new IntegrityGapTracker());
    // A fresh warn-once memory per fixture: the receptor's process-wide fallback would leak between tests.
    services.AddSingleton(new IntegrityOwnershipConflicts());
    var ledger = new RecordingIntegrityRepairLedger();
    services.AddSingleton<IIntegrityRepairLedger>(ledger);
    // A fresh policy per fixture: its window state is the subject under test, and the receptor's
    // static fallback is process-wide, which would leak state between parallel tests.
    services.AddSingleton(policy ?? new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IConsumerRegistry>(new TypeProvider());
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("consumer-svc"));
    // See IntegrityManifestReceptorTests: publishing is opt-in; these exercise that path.
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions { PublishReportEvents = true }));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new FixtureState {
      Coordinator = coordinator,
      Dispatcher = dispatcher,
      Transport = transport,
      Ledger = ledger.Inner,
      Recorder = ledger,
      Receptor = new IntegrityCheckpointReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(), logger ?? NullLogger<IntegrityCheckpointReceptor>.Instance),
    };
  }

  [Test]
  public async Task ConfirmedGap_WithDefaultOptions_EmitsGapCounterAndRequestsNoRepairAsync() {
    // Filter on THIS test's meter INSTANCE (not the name) — parallel tests share the meter name.
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var meter = metrics.GapsDetected.Meter;
    var measurements = new Dictionary<string, long>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (measurements) {
        measurements[instrument.Name] = measurements.GetValueOrDefault(instrument.Name) + value;
      }
    });
    listener.Start();

    var fx = _fixture(metrics: metrics);   // DEFAULT options: report-only
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));   // deficit → pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));   // confirms
    listener.RecordObservableInstruments();   // passive counters (#711) report at collection

    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.checkpoints_received")).IsEqualTo(2L);
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.gaps_detected")).IsEqualTo(1L)
      .Because("the confirmed gap must be countable — sustained non-zero is the operator's alarm.");
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.repairs_requested")).IsEqualTo(0L)
      .Because("the DEFAULT posture is report-only: the gap is counted and reported, and nothing mutates data until an operator opts in to repair.");
  }
  [Test]
  public async Task ConfirmedGap_WithAutoRepairCapped_EmitsGapAndRepairCountersAsync() {
    // Filter on THIS test's meter INSTANCE (not the name) — parallel tests share the meter name.
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var meter = metrics.GapsDetected.Meter;
    var measurements = new Dictionary<string, long>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (measurements) {
        measurements[instrument.Name] = measurements.GetValueOrDefault(instrument.Name) + value;
      }
    });
    listener.Start();

    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true }, metrics: metrics);
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));   // deficit → pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));   // confirms
    listener.RecordObservableInstruments();   // passive counters (#711) report at collection

    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.checkpoints_received")).IsEqualTo(2L);
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.gaps_detected")).IsEqualTo(1L)
      .Because("the confirmed gap must be countable — sustained non-zero is the operator's alarm.");
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.repairs_requested")).IsEqualTo(1L)
      .Because("with AutoRepairCapped opted in, the counter proves the healer acted, not just detected.");
  }

  private static IntegrityCheckpoint _checkpoint(
      FixtureState fx, long from, long to, int count, bool emptyBuckets = false, string? requestTopic = null,
      IReadOnlyList<string>? tenantScopes = null) {
    List<CheckpointBucket> buckets = [];
    if (!emptyBuckets && tenantScopes is not null) {
      // Pendings are keyed by (tenant, event type), so a multi-tenant window is how the confirmed-gap
      // count grows past anything a batch size bounds — the shape that has to stay capped.
      buckets = [.. tenantScopes.Select(t => new CheckpointBucket { TenantScope = t, EventType = _verifiedType, Count = count })];
    } else if (!emptyBuckets) {
      buckets = [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = count }];
    }
    return new() {
      CheckpointStreamId = fx.OriginId,
      OriginServiceId = fx.OriginId,
      OriginServiceName = "origin-svc",
      RequestTopic = requestTopic,
      FromCommitSequence = from,
      ToCommitSequence = to,
      Buckets = buckets,
    };
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  /// <summary>The consumer registry: this service consumes these events.</summary>
  private sealed class TypeProvider : IConsumerRegistry {
    private static readonly Type[] _events = [typeof(VerifiedEvent)];
    public bool HasAnyConsumer(string messageType) => _events.Any(e => TypeNameFormatter.Format(e) == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() =>
      [.. _events.Select(e => new ConsumedMessageInfo(TypeNameFormatter.Format(e), IsEvent: true))];
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class VerifyCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;
    public Func<(Guid Origin, long From, long To), IReadOnlyList<CheckpointBucket>> Counts { get; set; } = _ => [];
    public ServiceBacklog? Backlog { get; set; }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(Backlog);

    public List<(Guid Owner, string Name, string? Topic, IReadOnlyList<string> Domains)> RecordedClaims { get; } = [];
    public IReadOnlyList<IntegrityDomainClaim> Conflicts { get; set; } = [];
    public IReadOnlyList<IntegrityDomainClaim> Claims { get; set; } = [];

    public Task<IReadOnlyList<IntegrityDomainClaim>> RecordIntegrityDomainClaimsAsync(
        Guid ownerServiceId, string ownerServiceName, string? requestTopic, IReadOnlyList<string> domains,
        CancellationToken cancellationToken = default) {
      RecordedClaims.Add((ownerServiceId, ownerServiceName, requestTopic, domains));
      return Task.FromResult(Conflicts);
    }

    public Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Claims);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
      Guid originServiceId, long fromCommitSequence, long toCommitSequence, CancellationToken cancellationToken = default) =>
      Task.FromResult(Counts((originServiceId, fromCommitSequence, toCommitSequence)));

    /// <summary>#2104: what the store keeps of the gap tracker, shared by the fixtures of a simulated restart.</summary>
    public Dictionary<Guid, IntegrityKnownOrigin> KeptOrigins { get; } = [];
    public Dictionary<Guid, IReadOnlyList<IntegrityGapTracker.PendingGap>> KeptPending { get; } = [];
    public int PendingWrites { get; private set; }

    public Task<IntegrityGapTrackerState?> IntegrityLoadGapTrackerStateAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IntegrityGapTrackerState?>(new IntegrityGapTrackerState(
        [.. KeptOrigins.Values], [.. KeptPending.Values.SelectMany(p => p)]));

    public Task IntegrityRecordOriginCheckpointAsync(
        Guid originServiceId, string originServiceName, string? requestTopic, DateTimeOffset checkpointAt,
        CancellationToken cancellationToken = default) {
      KeptOrigins[originServiceId] = new IntegrityKnownOrigin(
        originServiceId, originServiceName,
        requestTopic ?? (KeptOrigins.TryGetValue(originServiceId, out var prior) ? prior.RequestTopic : null), checkpointAt);
      return Task.CompletedTask;
    }

    public Task IntegrityReplacePendingGapsAsync(
        Guid originServiceId, IReadOnlyList<IntegrityGapTracker.PendingGap> pending, CancellationToken cancellationToken = default) {
      PendingWrites++;
      KeptPending[originServiceId] = pending;
      return Task.CompletedTask;
    }

    /// <summary>#2102: the (tenant, type) folds of events held from the origin without an origin sequence.</summary>
    public IReadOnlyList<StreamDigest> Unsequenced { get; set; } = [];
    public List<(IReadOnlyList<string> Types, IReadOnlyList<Guid>? Streams)> UnsequencedAsks { get; } = [];

    public Task<IReadOnlyList<StreamDigest>> ComputeUnsequencedReceivedDigestsAsync(
        Guid originServiceId, IReadOnlyList<string> eventTypes, IReadOnlyList<Guid>? streamIds, TimeSpan settleWindow,
        CancellationToken cancellationToken = default) {
      UnsequencedAsks.Add((eventTypes, streamIds));
      return Task.FromResult<IReadOnlyList<StreamDigest>>([.. Unsequenced.Where(d => eventTypes.Contains(d.EventType))]);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  private sealed class CaptureTransport : ITransport {
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
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class RecordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>Captures PublishAsync payloads; every other dispatcher member is unused here.</summary>
  private sealed class CaptureDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new Receipt());
    }

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => PublishAsync(eventData);
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

    private sealed class Receipt : IDeliveryReceipt {
      public MessageId MessageId => MessageId.New();
      public CorrelationId? CorrelationId => null;
      public MessageId? CausationId => null;
      public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
      public string Destination => "test";
      public DeliveryStatus Status => DeliveryStatus.Delivered;
      public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
      public Guid? StreamId => null;
    }
  }

  [Test]
  public async Task UnhealableGap_IsNotRepairRequestedForeverAcrossCheckpointsAsync() {
    // IntegrityRepairMode.AutoRepairCapped documents itself as "hard-capped at every rung so a mass
    // divergence can never storm". MaxAutoRepairRequestsPerCheckpoint caps ONE checkpoint's batch,
    // and checkpoints fire on CheckpointIntervalSeconds (60 by default), so a gap whose events are
    // genuinely gone is re-requested on every checkpoint for as long as the service runs. Capping
    // the batch does not cap the repeat.
    //
    // The manifest path already guards this with the repair ledger's backoff and
    // MaxRepairAttemptsPerBucket. This is the same guard on the checkpoint path.
    var fx = _fixture(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      MaxRepairAttemptsPerBucket = 2,
      RepairRequestBackoffSeconds = 300,
    });
    // Never heals: the events are gone, so every recount reports the same deficit.
    fx.Coordinator.Counts = _ => [];

    // Six confirmation cycles, which at the default interval is six minutes of a service running.
    for (var cycle = 0; cycle < 6; cycle++) {
      await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
      await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));
    }

    await Assert.That(fx.Transport.Published.Count).IsLessThanOrEqualTo(2)
      .Because("the same unrepairable bucket must stop being re-requested once it has burned its "
               + "MaxRepairAttemptsPerBucket; otherwise every checkpoint re-asks forever and the "
               + "per-checkpoint cap only sets the storm's rate, not its size");
  }


  [Test]
  public async Task ConfirmedGap_OnLaggingService_IsNotRepairedAsync() {
    // The third settledness signal, and the one the depth gate cannot provide: an operator who
    // raises the settled-depth threshold to tolerate a small queue still must not repair while
    // something in that queue has been sitting far beyond the checkpoint cadence. The events being
    // counted as missing may be exactly what the service is stuck behind. IntegrityRepairPolicy
    // vetoes this as ConsumerBehind; before it was wired, the receptor only asked depth and leases.
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    fx.Coordinator.Counts = _ => [];
    fx.Coordinator.Backlog = new ServiceBacklog {
      UnprocessedInboxRows = 0,
      ActiveLeasedRows = 0,
      OldestUnprocessedAge = TimeSpan.FromMinutes(30),
    };

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Transport.Published).IsEmpty()
      .Because("a service running half an hour behind must not repair: the missing events are late, "
             + "not lost, and repairing re-delivers into the very queue that is behind");
  }

  [Test]
  public async Task ConfirmedGaps_BeyondTheGlobalWindowBudget_StopAtTheBudgetAsync() {
    // The per-checkpoint budget bounds ONE checkpoint; the policy's MaxConcurrentWindowsUnderRepair
    // bounds how many windows may be under repair AT ONCE, which is the only cap that limits the
    // total rate at which repair adds load. Twelve tenants confirm gaps in the same window with the
    // per-checkpoint budget out of the way; only the global budget should hold the line.
    var fx = _fixture(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      MaxAutoRepairRequestsPerCheckpoint = 20,
    });
    fx.Coordinator.Counts = _ => [];
    var tenants = Enumerable.Range(0, 12).Select(i => $"tenant-{i}").ToList();

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests", tenantScopes: tenants));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Transport.Published.Count).IsEqualTo(8)
      .Because("windows under repair are globally budgeted at 8 by default; past that the deficit "
             + "is still detected and reported, but repair stops adding load");
  }


  [Test]
  public async Task UnhealableGap_StopsBeingRescannedOnEveryCheckpointAsync() {
    // #634: repair was bounded per window, but the recount that CONFIRMS the gap was not, so a gap
    // whose events are genuinely gone paid a full event-store scan on every checkpoint for the life
    // of the service. With the governor, an answer that stops changing stops being re-asked: after
    // the unchanged threshold the window cools down, and one recount per cooldown keeps it honest.
    var policy = new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings {
      RecountBackoffAfterUnchanged = 2,
      UnchangedRecountCooldown = TimeSpan.FromHours(1),
    });
    var fx = _fixture(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped },
      policy: policy);
    var scans = 0;
    fx.Coordinator.Counts = _ => { scans++; return []; };

    for (var cycle = 0; cycle < 6; cycle++) {
      await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
      await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));
    }
    var total = scans;

    // Every checkpoint that names a bucket this service keeps pays a first-sight count of its OWN window (one per
    // pair here, so 6 across six cycles: an empty checkpoint names nothing to verify, and since #2058 it counts
    // nothing). Those windows advance in production and are per-checkpoint cost, not per-gap cost. The governor
    // bounds the repeated CONFIRMATION recount of the same stale window: two (the unchanged threshold), then the
    // cooldown holds.
    await Assert.That(total).IsEqualTo(8)
      .Because("detection cost on an unchanged answer must be bounded: 6 first-sight counts + 2 "
             + "confirmation recounts, then the cooldown holds. Unbounded is 12");
  }


  // ── #2008 / #2032: ownership exchange ──────────────────────────────────

  private const string VERIFIED_DOMAIN = "whizbang.data.efcore.postgres.tests";

  [Test]
  public async Task Checkpoint_RecordsTheOriginsAnnouncedDomainsAsync() {
    var fx = _fixture();

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 0, emptyBuckets: true, requestTopic: "origin.requests")
      with { OwnedDomains = ["app.contracts.orders", "app.contracts.shipping"] });

    var (owner, name, topic, domains) = fx.Coordinator.RecordedClaims.Single();
    await Assert.That(owner).IsEqualTo(fx.OriginId);
    await Assert.That(name).IsEqualTo("origin-svc");
    await Assert.That(topic).IsEqualTo("origin.requests")
      .Because("the owner's request address is kept with its claim, so backfill can be directed at it after a restart");
    await Assert.That(domains).IsEquivalentTo(["app.contracts.orders", "app.contracts.shipping"]);
  }

  [Test]
  public async Task Checkpoint_FromAnOlderOrigin_RecordsItsEmptyClaimSetAsync() {
    var fx = _fixture();

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(fx.Coordinator.RecordedClaims.Single().Domains).IsEmpty()
      .Because("an origin that announces nothing holds no claim, and one that stopped claiming a domain gives it up");
  }

  [Test]
  public async Task Checkpoint_AContestedDomain_WarnsAndCountsOncePerPairAsync() {
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var meter = metrics.GapsDetected.Meter;
    long conflicts = 0;
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "whizbang.stream_integrity.ownership_conflicts") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Exchange(ref conflicts, value));
    listener.Start();
    var logger = new CaptureLogger();
    var fx = _fixture(metrics: metrics, logger: logger);
    var rival = Guid.Parse("0f000000-0000-0000-0000-00000000000f");
    fx.Coordinator.Conflicts = [new IntegrityDomainClaim {
      Domain = "app.contracts.orders",
      OwnerServiceId = rival,
      OwnerServiceName = "rival-svc",
    }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 0, emptyBuckets: true) with { OwnedDomains = ["app.contracts.orders"] });
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 6, count: 0, emptyBuckets: true) with { OwnedDomains = ["app.contracts.orders"] });
    listener.RecordObservableInstruments();

    var warnings = logger.Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                         && e.Message.Contains("app.contracts.orders", StringComparison.Ordinal)).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("the same pair contesting the same domain on every checkpoint is one condition, warned once");
    await Assert.That(warnings[0].Message).Contains("rival-svc");
    await Assert.That(warnings[0].Message).Contains("origin-svc");
    await Assert.That(Interlocked.Read(ref conflicts)).IsEqualTo(1L);
  }

  [Test]
  public async Task Checkpoint_ABucketOfATypeAnotherServiceOwns_IsNotVerifiedAgainstThisOriginAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];
    fx.Coordinator.Claims = [new IntegrityDomainClaim {
      Domain = VERIFIED_DOMAIN,
      OwnerServiceId = Guid.Parse("0f000000-0000-0000-0000-00000000000f"),
      OwnerServiceName = "owner-svc",
    }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("an origin's count of a type another service owns is not authoritative, so no gap is confirmed against it");
    await Assert.That(fx.Transport.Published).IsEmpty()
      .Because("repair is asked of the owner, never of a service that merely holds a copy (#2032)");
  }

  [Test]
  public async Task Checkpoint_ABucketOfATypeThisOriginOwns_IsVerifiedAsBeforeAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];
    fx.Coordinator.Claims = [new IntegrityDomainClaim {
      Domain = VERIFIED_DOMAIN,
      OwnerServiceId = fx.OriginId,
      OwnerServiceName = "origin-svc",
    }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(fx.Dispatcher.Published.OfType<IntegrityGapDetected>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task Checkpoint_APendingGapOfATypeLearnedLaterToBeAnothersOwn_IsDroppedAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3, requestTopic: "origin.requests"));
    fx.Coordinator.Claims = [new IntegrityDomainClaim {
      Domain = VERIFIED_DOMAIN,
      OwnerServiceId = Guid.Parse("0f000000-0000-0000-0000-00000000000f"),
      OwnerServiceName = "owner-svc",
    }];
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("the deficit was recorded before the owner was known; once it is, the pending gap is not confirmed against this origin");
    await Assert.That(fx.Transport.Published).IsEmpty();
  }

  [Test]
  public async Task Checkpoint_AContestedDomain_OnAHostWithoutMetrics_IsStillWarnedAsync() {
    var logger = new CaptureLogger();
    var fx = _fixture(logger: logger, withMetrics: false);
    fx.Coordinator.Conflicts = [new IntegrityDomainClaim {
      Domain = "app.contracts.orders",
      OwnerServiceId = Guid.Parse("0f000000-0000-0000-0000-00000000000f"),
      OwnerServiceName = "rival-svc",
    }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 0, emptyBuckets: true) with { OwnedDomains = ["app.contracts.orders"] });

    await Assert.That(logger.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                             && e.Message.Contains("rival-svc", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the warning does not depend on the meter being registered");
  }

  // ── #2102: events held without the origin's commit sequence ─────────────

  private static StreamDigest _unsequenced(int count) => new() {
    TenantScope = "tenant-a",
    EventType = _verifiedType,
    StreamId = Guid.Empty,
    DigestLo = 1,
    DigestHi = 2,
    EventCount = count,
  };

  [Test]
  public async Task AShortWindow_TheEventsHeldWithoutASequenceCanFill_IsNoGapAsync() {
    // The consumer counts one event of the window by its origin sequence and holds two more of the origin's events
    // whose sequence it never learned. They may be exactly the window's other two, so the window is not short.
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];
    fx.Coordinator.Unsequenced = [_unsequenced(2)];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a consumer never reports a gap for events it holds, whether or not it knows their origin sequence");
    await Assert.That(fx.Coordinator.UnsequencedAsks.Single().Types).IsEquivalentTo([_verifiedType]);
    await Assert.That(fx.Coordinator.UnsequencedAsks.Single().Streams).IsNull()
      .Because("the checkpoint counts per (tenant, type), so the held events are counted per (tenant, type)");
  }

  [Test]
  public async Task AFullWindow_NeverAsksForTheEventsHeldWithoutASequenceAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 3 }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));

    await Assert.That(fx.Coordinator.UnsequencedAsks).IsEmpty()
      .Because("the checkpoint runs every interval; the extra read is paid only by a window that reads short");
  }

  [Test]
  public async Task AWindowShortByMoreThanTheEventsHeldWithoutASequence_IsStillAGapAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];
    fx.Coordinator.Unsequenced = [_unsequenced(1)];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    var report = (IntegrityGapDetected)fx.Dispatcher.Published.Single();
    await Assert.That(report.ExpectedCount).IsEqualTo(3)
      .Because("three expected and at most one held: a genuinely missing event is still a gap");
    await Assert.That(report.ActualCount).IsEqualTo(1)
      .Because("the report counts what the consumer holds, the event without a sequence included");
  }

  [Test]
  public async Task APendingGap_TheEventsHeldWithoutASequenceFillOnTheRecount_HealsAndForgetsTheLaneAsync() {
    // A gap recorded before the consumer could count its sequence-less events clears on the next comparison.
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];
    var key = new Whizbang.Core.Messaging.IntegrityRepairLedger.DivergenceKey(fx.OriginId, "tenant-a", _verifiedType, Guid.Empty);
    _ = await fx.Ledger.TryBeginRepairAsync(key, _fixedNow, TimeSpan.FromHours(1), 1);

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3, requestTopic: "origin.requests"));
    fx.Coordinator.Unsequenced = [_unsequenced(3)];
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Dispatcher.Published).IsEmpty();
    await Assert.That(fx.Transport.Published).IsEmpty().Because("nothing is missing, so nothing is redelivered");
    await Assert.That(await fx.Ledger.TryBeginRepairAsync(key, _fixedNow, TimeSpan.FromHours(1), 1)).IsTrue()
      .Because("the type lane healed, so its spent attempt is forgotten");
  }

  // ── #2104: the tracker's origins and pending gaps survive a restart ────

  [Test]
  public async Task Checkpoint_IsKeptByTheStore_WithTheOriginsAddressAndTimeAsync() {
    var fx = _fixture();

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    var kept = fx.Coordinator.KeptOrigins[fx.OriginId];
    await Assert.That(kept.OriginServiceName).IsEqualTo("origin-svc");
    await Assert.That(kept.RequestTopic).IsEqualTo("origin.requests");
    await Assert.That(fx.Coordinator.PendingWrites).IsEqualTo(0)
      .Because("a checkpoint that neither finds nor clears a gap writes no pending state");
  }

  [Test]
  public async Task APendingGapSeenBeforeARestart_IsConfirmedByTheFirstCheckpointAfterItAsync() {
    var options = new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly, PublishReportEvents = true };
    var before = _fixture(options);
    before.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];
    await before.Receptor.HandleAsync(_checkpoint(before, from: 0, to: 5, count: 3));   // deficit: pending, kept

    // A new process: a fresh tracker over the same store.
    var after = _fixture(options, coordinator: before.Coordinator);
    await after.Receptor.HandleAsync(_checkpoint(before, from: 5, to: 5, count: 0, emptyBuckets: true));

    var report = (IntegrityGapDetected)after.Dispatcher.Published.Single();
    await Assert.That(report.FromCommitSequence).IsEqualTo(0L)
      .Because("the gap seen before the restart is confirmed by the first checkpoint after it, not re-detected a cycle late");
    await Assert.That(before.Coordinator.KeptPending[before.OriginId]).IsEmpty()
      .Because("a confirmed gap leaves the kept pending set");
  }
}
