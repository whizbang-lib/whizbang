using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Stream-integrity Phases A + L: one audit cycle reports local perspective coverage gaps (with a
/// capped LOCAL rebuild at AutoRepairCapped) and sends a DIRECTED manifest request to every origin
/// the checkpoint tracker knows.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
public class IntegrityAuditWorkerTests {

  public sealed record AuditProbeEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  [Test]
  public async Task LocalGaps_ReportAndDispatchCappedRebuildsAsync() {
    var coordinator = new _auditCoordinator {
      Gaps = [
        new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "OrdersPerspective", EventCount = 7 },
        new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "ItemsPerspective", EventCount = 3 },
      ]
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, new _captureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRebuildsPerAudit = 1 });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var reports = dispatcher.Published.Cast<PerspectiveCoverageGapDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(2);
    await Assert.That(reports.Count(r => r.AutoRebuildRequested)).IsEqualTo(1)
      .Because("the rebuild cap is a hard per-cycle budget — the second gap reports without rebuilding.");
    var rebuild = (RebuildPerspectiveCommand)dispatcher.Sent.Single();
    await Assert.That(rebuild.PerspectiveNames!).IsEquivalentTo(["OrdersPerspective"]);
    await Assert.That(rebuild.IncludeStreamIds!).IsEquivalentTo([coordinator.Gaps[0].StreamId])
      .Because("the rebuild is scoped to exactly the uncovered stream — local repair, minimal blast radius.");
  }

  [Test]
  public async Task ReportOnly_ReportsGapsWithoutRebuildingAsync() {
    var coordinator = new _auditCoordinator {
      Gaps = [new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "OrdersPerspective", EventCount = 7 }]
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, new _captureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var report = (PerspectiveCoverageGapDetected)dispatcher.Published.Single();
    await Assert.That(report.AutoRebuildRequested).IsFalse()
      .Because("the ReportOnly rung reports without rebuilding — the operator's explicit opt-down.");
    await Assert.That(dispatcher.Sent).IsEmpty();
  }

  [Test]
  public async Task KnownOrigins_GetDirectedManifestRequestsAsync() {
    var coordinator = new _auditCoordinator();
    var tracker = new IntegrityGapTracker();
    var originA = TrackedGuid.NewMedo().Value;
    var originB = TrackedGuid.NewMedo().Value;
    tracker.RecordCheckpoint(originA, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    tracker.RecordCheckpoint(originB, "origin-b", DateTimeOffset.UtcNow);
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport, new StreamIntegrityOptions(), tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("directed or not at all — an origin that never announced a request address is " +
               "SKIPPED, not broadcast to: the legacy own-topic fallback fanned requests out to " +
               "every service on the shared topic (and back to the requester itself).");
    var toA = transport.Published.Single(p => p.Envelope.Target == "origin-a");
    await Assert.That(toA.Destination.Address).IsEqualTo("origin-a.requests")
      .Because("a DIRECTED request must publish to the ORIGIN-carried address — a topic the " +
               "origin actually consumes; publishing to the requester's own destination sent " +
               "requests where no origin listens (observed live: six requests, zero receipts).");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var request = (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
    await Assert.That(request.RequesterService).IsEqualTo("auditor-svc");
    await Assert.That(request.EventTypes!).IsEquivalentTo([TypeNameFormatter.Format(typeof(AuditProbeEvent))])
      .Because("the request restricts the manifest to the types this consumer actually subscribes to — " +
               "in the assembly-qualified wire form the origin's event_type/digest columns store, " +
               "or the origin's exact-match lookup silently returns nothing.");
    await Assert.That(Guid.TryParse(transport.Published[0].Destination.Metadata?["StreamId"].GetString(), out _)).IsTrue()
      .Because("session-enabled subscriptions dead-letter sessionless deliveries — the manifest " +
               "request must carry a session key in its destination metadata.");
  }

  // ── A1c: hierarchical requests + the full-sweep cadence ─────────────────

  [Test]
  public async Task DefaultCycle_RequestsTypeLevelTableManifests_NoVerifyAsync() {
    var coordinator = new _auditCoordinator();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.NewMedo().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport, new StreamIntegrityOptions(), tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.Level).IsEqualTo(ManifestLevel.Types)
      .Because("the scheduled audit starts at type granularity — O(types) wire cost; mismatches drill down.");
    await Assert.That(request.UseRecompute).IsFalse()
      .Because("steady-state cycles run on the maintained digest table, not a store-wide recompute.");
    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0)
      .Because("the trust-but-verify heal is the sweep cycle's job (default every 7th).");
  }

  [Test]
  public async Task SweepCycle_ForcesRecomputeAndVerifiesDigestTableAsync() {
    var coordinator = new _auditCoordinator();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.NewMedo().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport,
      new StreamIntegrityOptions { FullSweepEveryNthAudit = 1 }, tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.UseRecompute).IsTrue()
      .Because("the sweep cycle exchanges recomputed digests end to end — busy buckets get their coverage here.");
    await Assert.That(request.Level).IsEqualTo(ManifestLevel.Types)
      .Because("even the sweep starts at type level; only mismatched types pay stream-level wire cost.");
    await Assert.That(coordinator.VerifyCalls).IsEqualTo(1)
      .Because("each sweep also verifies + heals this service's OWN digest table against the recompute.");
  }

  [Test]
  public async Task SweepDisabled_NeverVerifiesAsync() {
    var coordinator = new _auditCoordinator();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.NewMedo().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport,
      new StreamIntegrityOptions { FullSweepEveryNthAudit = 0 }, tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);
    await worker.RunAuditOnceAsync(CancellationToken.None);
    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(0);
    await Assert.That(transport.Published.All(p => !_deserializeRequest(p.Envelope).UseRecompute)).IsTrue()
      .Because("0 disables sweeps — every cycle stays table-driven.");
  }

  private static RequestIntegrityManifest _deserializeRequest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
  }

  // ── OTel: the self-healing loop must be observable ──────────────────────

  [Test]
  public async Task AuditCycle_EmitsGapRebuildAndManifestCountersAsync() {
    // Filter on THIS test's meter INSTANCE (not the name) — parallel tests share the meter name.
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics());
    var meter = metrics.CoverageGapsDetected.Meter;
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

    var coordinator = new _auditCoordinator {
      Gaps = [new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "OrdersPerspective", EventCount = 7 }]
    };
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.NewMedo().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var worker = _buildWorker(coordinator, new _captureDispatcher(), new _captureTransport(),
      new StreamIntegrityOptions { FullSweepEveryNthAudit = 1 }, tracker, metrics);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.coverage_gaps_detected")).IsEqualTo(1L)
      .Because("self-healing by default only works when operators can SEE what the healer detects.");
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.rebuilds_requested")).IsEqualTo(1L)
      .Because("the default AutoRepairCapped rung dispatched a rebuild — the counter proves it.");
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.manifests_requested")).IsEqualTo(1L);
  }

  // ── coverage-gap report cap ─────────────────────────────────────────────

  [Test]
  public async Task MassCoverageGaps_ReportsAreCapped_WithOneSummaryAsync() {
    // A systematically-uncovered perspective can surface THOUSANDS of gaps in one cycle. Reports
    // must be bounded — an unbounded report loop flooded a live consumer's dispatcher at startup
    // and crashlooped the pod (probe timeout). Detection is still complete: the summary names the
    // total; the remainder re-audits next cycle after repairs shrink it.
    var coordinator = new _auditCoordinator {
      Gaps = [.. Enumerable.Range(0, 500).Select(i => new PerspectiveCoverageGap {
        StreamId = TrackedGuid.NewMedo().Value,
        PerspectiveName = "FloodedPerspective",
        EventCount = 2,
      })]
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, new _captureTransport(),
      new StreamIntegrityOptions { MaxCoverageGapReportsPerAudit = 100 });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(dispatcher.Published.OfType<PerspectiveCoverageGapDetected>().Count()).IsEqualTo(100)
      .Because("the report loop is hard-capped — a mass gap must never flood the dispatcher/outbox.");
    await Assert.That(dispatcher.Sent.Count).IsEqualTo(5)
      .Because("rebuilds keep their own cap (MaxAutoRebuildsPerAudit default 5) within the capped set.");
  }

  [Test]
  public async Task CoverageGapQuery_IsBoundedByTheReportCapAsync() {
    var coordinator = new _auditCoordinator();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), new _captureTransport(),
      new StreamIntegrityOptions { MaxCoverageGapReportsPerAudit = 42 });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.LastMaxGaps).IsEqualTo(42)
      .Because("the bound belongs in the QUERY too — fetching thousands of rows to report 100 is the same flood one layer down.");
  }

  // ── startup audit timing ────────────────────────────────────────────────

  [Test]
  public async Task FirstAuditDelay_OnStartupDefault_IsJitteredStartupWindowAsync() {
    var options = new StreamIntegrityOptions();   // AuditOnStartup default true

    var atFloor = IntegrityAuditWorker.ComputeFirstAuditDelay(options, () => 0.0);
    var atCeiling = IntegrityAuditWorker.ComputeFirstAuditDelay(options, () => 1.0);

    await Assert.That(atFloor).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("the floor gives the schema gate + baseline registration a moment to settle before auditing.");
    await Assert.That(atCeiling).IsEqualTo(TimeSpan.FromSeconds(30 + 300))
      .Because("jitter spreads a fleet deploy's startup audits across the splay window — no audit storm.");
  }

  [Test]
  public async Task FirstAuditDelay_StartupDisabled_IsFullIntervalAsync() {
    var options = new StreamIntegrityOptions { AuditOnStartup = false };

    var delay = IntegrityAuditWorker.ComputeFirstAuditDelay(options, () => 0.5);

    await Assert.That(delay).IsEqualTo(TimeSpan.FromMinutes(options.AuditIntervalMinutes))
      .Because("opting out restores the original interval-first behavior.");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static IntegrityAuditWorker _buildWorker(
      _auditCoordinator coordinator, _captureDispatcher dispatcher, _captureTransport transport,
      StreamIntegrityOptions options, IntegrityGapTracker? tracker = null,
      Whizbang.Core.Observability.StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker ?? new IntegrityGapTracker());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("auditor-svc"));
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    services.AddSingleton(metrics
      ?? new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics()));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new IntegrityAuditWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(options),
      NullLogger<IntegrityAuditWorker>.Instance);
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(AuditProbeEvent)];
  }

  private sealed class _instanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
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

  private sealed class _auditCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<PerspectiveCoverageGap> Gaps { get; init; } = [];
    public int VerifyCalls { get; private set; }
    public int? LastMaxGaps { get; private set; }

    public Task<IReadOnlyList<PerspectiveCoverageGap>> GetPerspectiveCoverageGapsAsync(
      TimeSpan settleWindow, int maxGaps, CancellationToken cancellationToken = default) {
      LastMaxGaps = maxGaps;
      return Task.FromResult<IReadOnlyList<PerspectiveCoverageGap>>([.. Gaps.Take(maxGaps)]);
    }

    public Task<DigestVerificationResult> VerifyDigestTableAsync(
      TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      VerifyCalls++;
      return Task.FromResult(new DigestVerificationResult {
        BucketsChecked = 0,
        DriftUpdated = 0,
        DriftRemoved = 0,
        DriftAdded = 0,
      });
    }
  }

  private sealed class _captureDispatcher : FakeDispatcher, IDispatcher {
    public List<object> Published { get; } = [];
    public List<object> Sent { get; } = [];

    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }

    public new Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull {
      Sent.Add(message);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }
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
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
