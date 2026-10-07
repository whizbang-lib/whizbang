// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for the stream-integrity manifest receptors: each optional collaborator (the
/// options, the service identity, the metrics, the gap tracker that knows the origin's request
/// topic) is exercised both present and absent, on the answer, compare, cursor-follow, type-level
/// bulk backfill and drill-down paths.
/// </summary>
/// <remarks>
/// Both arms of every decision are taken inside this class: the CI coverage merge keeps the best
/// per-line condition count of any single shard, so an arm exercised only in another shard does
/// not count. No database. The receptors' compare and answer gates are process-wide, hence the
/// shared not-in-parallel key with the sibling suite.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
[NotInParallel("IntegrityManifestGates")]
[Category("Shard3")]
public class IntegrityManifestReceptorBranchTests {

  private const string TYPE_X = "Contracts.TypeX";

  // ── origin side: answering a manifest request ──────────────────────────

  [Test]
  public async Task Answer_FullyWired_NamesTheOriginAndRecordsChunksAndDurationAsync() {
    var coordinator = new AuditCoordinator { OwnDigests = [_digest(Guid.NewGuid(), 1, 2, 1)] };
    var transport = new CaptureTransport();
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.ManifestAnswerDuration.Meter);
    var sp = _provider(coordinator, transport, new Wiring { Metrics = metrics });
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(_deserialize<IntegrityManifest>(transport.Published[0].Envelope).OriginServiceName).IsEqualTo("auditor-svc")
      .Because("the answer names the origin from its own service identity");
    await Assert.That(probe.CounterTotal(metrics.ManifestChunksSent.Instrument)).IsEqualTo(1);
    await Assert.That(probe.HistogramCount(metrics.ManifestAnswerDuration)).IsEqualTo(1);
  }

  // No options, no identity, no metrics: the answer still goes out under the default chunking,
  // naming the origin as empty rather than failing.
  [Test]
  public async Task Answer_NothingOptionalRegistered_StillAnswersWithAnUnnamedOriginAsync() {
    var coordinator = new AuditCoordinator { OwnDigests = [_digest(Guid.NewGuid(), 1, 2, 1)] };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, new Wiring { RegisterOptions = false, RegisterInstanceProvider = false });
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserialize<IntegrityManifest>(transport.Published[0].Envelope);
    await Assert.That(manifest.OriginServiceName).IsEqualTo(string.Empty);
    await Assert.That(manifest.Digests.Count).IsEqualTo(1);
  }

  // ── consumer side: comparing a stream-level manifest ───────────────────

  // No options registered: the defaults keep auditing on, and a matching bucket heals silently.
  [Test]
  public async Task Compare_NoOptionsRegistered_AuditsUnderTheDefaultsAsync() {
    var stream = Guid.NewGuid();
    var coordinator = new AuditCoordinator { ReceivedTableDigests = [_digest(stream, 11, 21, 2)] };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var ledger = new ScriptedLedger();
    var sp = _provider(coordinator, transport, new Wiring { RegisterOptions = false, Dispatcher = dispatcher, Ledger = ledger });
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(ledger.HealedKeys).IsEqualTo(1)
      .Because("auditing is on by default, so the matching bucket is compared and marked healed");
    await Assert.That(dispatcher.Published).IsEmpty();
    await Assert.That(transport.Published).IsEmpty();
  }

  // Fully wired, burst repair mode: the heal age, each divergence and each repair are metered,
  // only the reports the ledger granted are published, and the repair goes to the origin's topic.
  [Test]
  public async Task Compare_FullyWired_MetersEveryStage_PublishesGrantedReports_SendsRepairAsync() {
    var matching = Guid.NewGuid();
    var coordinator = new AuditCoordinator { ReceivedTableDigests = [_digest(matching, 11, 21, 2)] };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.DivergencesDetected.Meter);
    var ledger = new ScriptedLedger { ReportFlag = i => i == 0 };
    var sp = _provider(coordinator, transport, new Wiring {
      Options = _burstRepair(),
      Dispatcher = dispatcher,
      Tracker = tracker,
      Ledger = ledger,
      Metrics = metrics,
    });
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [
      _digest(matching, 11, 21, 2), _digest(Guid.NewGuid(), 31, 41, 2), _digest(Guid.NewGuid(), 51, 61, 3),
    ]));

    await Assert.That(dispatcher.Published.Count).IsEqualTo(1)
      .Because("the ledger granted the report for the first divergence only; the second is inside its cooldown");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("both deficits share one (tenant, type), so one directed repair request carries both streams");
    await Assert.That(transport.Published[0].Destination.Address).IsEqualTo("origin.requests");
    await Assert.That(_deserialize<RequestRedeliveryCommand>(transport.Published[0].Envelope).StreamIds!.Count).IsEqualTo(2);
    await Assert.That(probe.HistogramCount(metrics.BucketHealSeconds)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.DivergencesDetected.Instrument)).IsEqualTo(2);
    await Assert.That(probe.CounterTotal(metrics.RepairsRequested.Instrument)).IsEqualTo(2);
    await Assert.That(probe.HistogramCount(metrics.ManifestCompareDuration)).IsEqualTo(1);
  }

  // Same divergence with no metrics and no learned origin topic, and a ledger that returned no
  // report decisions at all: nothing is published, and the repair is withheld rather than
  // broadcast off this service's own topic.
  [Test]
  public async Task Compare_NoMetricsNoTrackerNoReportDecisions_WithholdsEverythingAsync() {
    var matching = Guid.NewGuid();
    var coordinator = new AuditCoordinator { ReceivedTableDigests = [_digest(matching, 11, 21, 2)] };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var ledger = new ScriptedLedger { ReportFlag = null };
    var sp = _provider(coordinator, transport, new Wiring {
      Options = _burstRepair(),
      Dispatcher = dispatcher,
      Ledger = ledger,
    });
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(matching, 11, 21, 2), _digest(Guid.NewGuid(), 31, 41, 2)]));

    await Assert.That(ledger.RepairKeys).IsEqualTo(1)
      .Because("the deficit was still offered for repair");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("a decision missing from the ledger's answer reads as 'do not report'");
    await Assert.That(transport.Published).IsEmpty()
      .Because("without the origin's carried request address the repair is withheld, never broadcast");
  }

  // ── consumer side: following a windowed answer's resume cursor ─────────

  [Test]
  public async Task CursorFollow_PageBudgetSpent_IsCappedWithAndWithoutMetricsAsync() {
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.ManifestPagesCapped.Meter);
    var options = new StreamIntegrityOptions { MaxManifestPagesPerAudit = 0 };

    var metered = await _followCursorAsync(options, metrics, withTracker: true);
    var unmetered = await _followCursorAsync(options, metrics: null, withTracker: true);

    await Assert.That(metered.Published).IsEmpty()
      .Because("a zero page budget caps the follow before anything is sent");
    await Assert.That(unmetered.Published).IsEmpty();
    await Assert.That(probe.CounterTotal(metrics.ManifestPagesCapped.Instrument)).IsEqualTo(1)
      .Because("only the metered host counts the cap");
  }

  [Test]
  public async Task CursorFollow_OriginTopicKnown_RequestsTheNextPageWithAndWithoutMetricsAsync() {
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.ManifestPagesFollowed.Meter);
    var options = new StreamIntegrityOptions();

    var metered = await _followCursorAsync(options, metrics, withTracker: true);
    var unmetered = await _followCursorAsync(options, metrics: null, withTracker: true);

    await Assert.That(metered.Published.Count).IsEqualTo(1);
    await Assert.That(_deserialize<RequestIntegrityManifest>(metered.Published[0].Envelope).ResumeAfterStreamId).IsNotNull()
      .Because("the follow-up resumes exactly where the origin's answer stopped");
    await Assert.That(unmetered.Published.Count).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.ManifestPagesFollowed.Instrument)).IsEqualTo(1);
  }

  [Test]
  public async Task CursorFollow_NoTracker_SkipsTheFollowAsync() {
    var transport = await _followCursorAsync(new StreamIntegrityOptions(), metrics: null, withTracker: false);

    await Assert.That(transport.Published).IsEmpty()
      .Because("the follow-up is directed at the origin's request topic or not sent at all");
  }

  // ── consumer side: type-level bulk backfill and drill-down ─────────────

  [Test]
  public async Task TypeLevel_FullyWired_SendsBulkBackfillAndDrillDown_AndMetersBothAsync() {
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.DrillDownsRequested.Meter);

    var transport = await _typeLevelAsync(metrics, withTracker: true, includeBulk: true);

    await Assert.That(transport.Published.Count(p => _isKind(p.EnvelopeType, nameof(RequestRedeliveryCommand)))).IsEqualTo(1)
      .Because("the large deficit escalates to one bulk backfill for the whole window");
    await Assert.That(transport.Published.Count(p => _isKind(p.EnvelopeType, nameof(RequestIntegrityManifest)))).IsEqualTo(1)
      .Because("the small mismatch drills down to a stream-level request");
    await Assert.That(probe.CounterTotal(metrics.RepairsRequested.Instrument)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.DrillDownsRequested.Instrument)).IsEqualTo(1);
  }

  [Test]
  public async Task TypeLevel_NoMetricsNoTracker_WithholdsBulkAndDrillDownAsync() {
    var transport = await _typeLevelAsync(metrics: null, withTracker: false, includeBulk: true);

    await Assert.That(transport.Published).IsEmpty()
      .Because("without the origin's request topic neither the bulk ask nor the drill-down may be sent");
  }

  [Test]
  public async Task TypeLevel_NoMetrics_StillSendsTheDrillDownAsync() {
    var transport = await _typeLevelAsync(metrics: null, withTracker: true, includeBulk: false);

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(_isKind(transport.Published[0].EnvelopeType, nameof(RequestIntegrityManifest))).IsTrue();
  }

  // ── consumer side: the non-queueing compare gate ───────────────────────

  // A manifest arriving while another comparison holds the process-wide gate is declined at once
  // (its buckets re-audit next cycle), and only a metered host counts the decline.
  [Test]
  public async Task CompareGateBusy_DeclinesWithAndWithoutMetricsAsync() {
    var stream = Guid.NewGuid();
    var blocking = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 11, 21, 2)],
      BlockForChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    var blockingMetrics = _newMetrics();
    using var blockingProbe = new MeterProbe(blockingMetrics.ManifestCompareDuration.Meter);
    var holder = new IntegrityManifestReceptor(
      _provider(blocking, new CaptureTransport(), new Wiring { Metrics = blockingMetrics }).GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestReceptor>.Instance);
    var held = holder.HandleAsync(_windowed(_manifest(blocking, [_digest(stream, 11, 21, 2)]), resumeAfter: null)).AsTask();
    await blocking.ForChunkEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    var meteredCoordinator = new AuditCoordinator();
    var declinedMetrics = _newMetrics();
    using var declinedProbe = new MeterProbe(declinedMetrics.ComparesDeclined.Meter);
    var metered = new IntegrityManifestReceptor(
      _provider(meteredCoordinator, new CaptureTransport(), new Wiring { Metrics = declinedMetrics }).GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestReceptor>.Instance);
    var unmeteredCoordinator = new AuditCoordinator();
    var unmetered = new IntegrityManifestReceptor(
      _provider(unmeteredCoordinator, new CaptureTransport()).GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestReceptor>.Instance);

    await metered.HandleAsync(_windowed(_manifest(meteredCoordinator, [_digest(stream, 11, 21, 2)]), resumeAfter: null));
    await unmetered.HandleAsync(_windowed(_manifest(unmeteredCoordinator, [_digest(stream, 11, 21, 2)]), resumeAfter: null));
    blocking.BlockForChunk!.SetResult();
    await held;

    await Assert.That(meteredCoordinator.ForChunkCalls + unmeteredCoordinator.ForChunkCalls).IsEqualTo(0)
      .Because("a declined manifest must not start a comparison of its own");
    await Assert.That(declinedProbe.CounterTotal(declinedMetrics.ComparesDeclined.Instrument)).IsEqualTo(1);
    await Assert.That(blockingProbe.HistogramCount(blockingMetrics.ManifestCompareDuration)).IsEqualTo(1)
      .Because("the comparison that held the gate records its duration once it finishes");
  }

  // ── fixture ─────────────────────────────────────────────────────────────

  private static StreamIntegrityOptions _burstRepair() => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    RepairDrainEnabled = false,
    PublishReportEvents = true,
  };

  private static async Task<CaptureTransport> _followCursorAsync(
      StreamIntegrityOptions options, StreamIntegrityMetrics? metrics, bool withTracker) {
    var stream = Guid.NewGuid();
    var coordinator = new AuditCoordinator { ReceivedDigests = [_digest(stream, 11, 21, 2)] };
    var transport = new CaptureTransport();
    IntegrityGapTracker? tracker = null;
    if (withTracker) {
      tracker = new IntegrityGapTracker();
      tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    }
    var sp = _provider(coordinator, transport, new Wiring { Options = options, Tracker = tracker, Metrics = metrics });
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 11, 21, 2)]), resumeAfter: Guid.NewGuid()));
    return transport;
  }

  private static async Task<CaptureTransport> _typeLevelAsync(StreamIntegrityMetrics? metrics, bool withTracker, bool includeBulk) {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 5000 },
    };
    var transport = new CaptureTransport();
    IntegrityGapTracker? tracker = null;
    if (withTracker) {
      tracker = new IntegrityGapTracker();
      tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    }
    var sp = _provider(coordinator, transport, new Wiring {
      Options = new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
        PublishReportEvents = false,
        BulkBackfillThresholdEvents = 1000,
      },
      Tracker = tracker,
      Ledger = new ScriptedLedger(),
      Metrics = metrics,
    });
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    List<StreamDigest> digests = [_typeDigest("Contracts.TypeSmall", 3, 4, 5)];
    if (includeBulk) {
      digests.Insert(0, _typeDigest("Contracts.TypeBulk", 1, 2, 2000));
    }
    var manifest = _manifest(coordinator, digests, ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 5000,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);
    return transport;
  }

  private static IntegrityManifest _windowed(IntegrityManifest manifest, Guid? resumeAfter) => manifest with {
    SinceSequence = 100,
    ComputedThrough = 300,
    ChunkCount = 1,
    ResumeAfterStreamId = resumeAfter,
  };

  private static bool _isKind(string? envelopeType, string name) =>
    envelopeType?.Contains(name, StringComparison.Ordinal) == true;

  private static StreamIntegrityMetrics _newMetrics() =>
    new(new WhizbangMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private static StreamDigest _digest(Guid stream, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = TYPE_X,
    StreamId = stream,
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };

  private static StreamDigest _typeDigest(string eventType, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = eventType,
    StreamId = Guid.Empty,
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };

  private static IntegrityManifest _manifest(
      AuditCoordinator coordinator, List<StreamDigest> digests, ManifestLevel level = ManifestLevel.Streams) => new() {
        ManifestStreamId = coordinator.OriginId,
        OriginServiceId = coordinator.OriginId,
        OriginServiceName = "origin-svc",
        Digests = digests,
        Level = level,
      };

  private static T _deserialize<T>(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (T)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(T)))!;
  }

  private sealed class Wiring {
    public bool RegisterOptions { get; init; } = true;
    public StreamIntegrityOptions? Options { get; init; }
    public bool RegisterInstanceProvider { get; init; } = true;
    public CaptureDispatcher? Dispatcher { get; init; }
    public IntegrityGapTracker? Tracker { get; init; }
    public IIntegrityRepairLedger? Ledger { get; init; }
    public StreamIntegrityMetrics? Metrics { get; init; }
  }

  private static ServiceProvider _provider(AuditCoordinator coordinator, CaptureTransport transport, Wiring? wiring = null) {
    wiring ??= new Wiring();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(wiring.Dispatcher ?? new CaptureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    if (wiring.RegisterInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("auditor-svc"));
    }
    if (wiring.RegisterOptions) {
      services.AddSingleton(Options.Create(wiring.Options ?? new StreamIntegrityOptions()));
    }
    if (wiring.Tracker is not null) {
      services.AddSingleton(wiring.Tracker);
    }
    if (wiring.Ledger is not null) {
      services.AddSingleton(wiring.Ledger);
    }
    if (wiring.Metrics is not null) {
      services.AddSingleton(wiring.Metrics);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  /// <summary>Reads one meter's instruments by identity, so sibling meters of the same name
  /// (other tests' metrics instances) never leak into a reading.</summary>
  private sealed class MeterProbe : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(Instrument Instrument, double Value)> _readings = [];

    public MeterProbe(Meter meter) {
      _listener.InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument.Meter, meter)) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _record(instrument, value));
      _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => _record(instrument, value));
      _listener.Start();
    }

    /// <summary>The cumulative total of a passive (observable) counter, across all its series.</summary>
    public double CounterTotal(Instrument instrument) {
      lock (_readings) {
        _readings.RemoveAll(r => ReferenceEquals(r.Instrument, instrument));
      }
      _listener.RecordObservableInstruments();
      lock (_readings) {
        return _readings.Where(r => ReferenceEquals(r.Instrument, instrument)).Sum(r => r.Value);
      }
    }

    /// <summary>How many values a histogram recorded since the probe started.</summary>
    public int HistogramCount(Instrument instrument) {
      lock (_readings) {
        return _readings.Count(r => ReferenceEquals(r.Instrument, instrument));
      }
    }

    public void Dispose() => _listener.Dispose();

    private void _record(Instrument instrument, double value) {
      lock (_readings) {
        _readings.Add((instrument, value));
      }
    }
  }

  /// <summary>Grants every repair, returns one heal age per healed key, and answers report
  /// decisions from <see cref="ReportFlag"/> (null: answers with no decisions at all).</summary>
  private sealed class ScriptedLedger : IIntegrityRepairLedger {
    public Func<int, bool>? ReportFlag { get; init; } = _ => true;
    public int HealedKeys { get; private set; }
    public int RepairKeys { get; private set; }

    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
        TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<bool>> TryBeginReportBatchAsync(IReadOnlyList<IntegrityReportObservation> observations,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
      var flag = ReportFlag;
      return ValueTask.FromResult<IReadOnlyList<bool>>(flag is null ? [] : [.. observations.Select((_, i) => flag(i))]);
    }
    public ValueTask<IReadOnlyList<bool>> TryBeginRepairBatchAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants, CancellationToken cancellationToken = default) {
      RepairKeys += keys.Count;
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. keys.Select(_ => true)]);
    }
    public ValueTask<IReadOnlyList<double>> MarkHealedBatchWithAgesAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        CancellationToken cancellationToken = default) {
      HealedKeys += keys.Count;
      return ValueTask.FromResult<IReadOnlyList<double>>([.. keys.Select(_ => 3.0)]);
    }
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
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

  private sealed class AuditCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = Guid.NewGuid();
    public Guid OriginId { get; } = Guid.NewGuid();
    public IReadOnlyList<StreamDigest> OwnDigests { get; init; } = [];
    public IReadOnlyList<StreamDigest> ReceivedDigests { get; init; } = [];
    public IReadOnlyList<StreamDigest> ReceivedTableDigests { get; init; } = [];
    public WindowedDigestResult? WindowedTypeResult { get; init; }
    public TaskCompletionSource? BlockForChunk { get; init; }
    public TaskCompletionSource ForChunkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ForChunkCalls;

    public Task IntegrityStampRepairWindowsAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        long windowFrom, long windowUntil, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public Task<IReadOnlyList<StreamDigest>> ComputeStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnDigests : ReceivedDigests);

    public Task<IReadOnlyList<StreamDigest>> ComputeTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(IntegrityDigestMath.RollUpToTypes(originServiceId is null ? OwnDigests : ReceivedDigests));

    public Task AdvanceIntegritySealAsync(Guid originServiceId, long through, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<long> GetIntegrityOriginGenerationAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public Task<bool> EnsureIntegritySealGenerationAsync(
        Guid originServiceId, long generation, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<WindowedDigestResult?> ComputeTypeDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(WindowedTypeResult);

    public Task<WindowedDigestResult?> ComputeStreamDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, Guid? resumeAfterStreamId, int maxDigests,
      TimeSpan settleWindow, CancellationToken cancellationToken = default) => Task.FromResult<WindowedDigestResult?>(null);

    public async Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
      Guid originServiceId, IReadOnlyList<Guid> streamIds,
      long? sinceSequence, long? untilSequence, TimeSpan settleWindow,
      CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref ForChunkCalls);
      ForChunkEntered.TrySetResult();
      if (BlockForChunk is { } gate) {
        await gate.Task;
      }
      return [.. ReceivedDigests.Where(d => streamIds.Contains(d.StreamId))];
    }

    public Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? (IReadOnlyList<StreamDigest>)[] : ReceivedTableDigests);

    public Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<StreamDigest>>([]);

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

  /// <summary>Captures PublishAsync payloads; every other dispatcher member is unused here.</summary>
  private sealed class CaptureDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      lock (Published) {
        Published.Add(eventData!);
      }
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
}
