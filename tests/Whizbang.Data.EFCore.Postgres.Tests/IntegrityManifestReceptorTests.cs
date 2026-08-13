using System;
using System.Collections.Generic;
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
/// Stream-integrity Phase A receptors: the origin answers a manifest request with chunked,
/// targeted digest manifests of its own emissions; the consumer compares a received manifest
/// against its from-that-origin digests — identical folds are silent, divergent buckets report
/// and (ladder-gated) send stream-scoped repair requests back to the origin.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
[NotInParallel("IntegrityManifestGates")]   // the receptors' per-process gates are shared state
public class IntegrityManifestReceptorTests {

  [Test]
  public async Task RequestReceptor_SendsChunkedTargetedManifestsAsync() {
    var coordinator = new _auditCoordinator();
    var stream1 = TrackedGuid.NewMedo().Value;
    var stream2 = TrackedGuid.NewMedo().Value;
    var stream3 = TrackedGuid.NewMedo().Value;
    coordinator.OwnDigests = [
      _digest(stream1, 11, 21, 2), _digest(stream2, 12, 22, 1), _digest(stream3, 13, 23, 3),
    ];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { MaxDigestsPerManifest = 2, PublishReportEvents = true });
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"]
    });

    await Assert.That(transport.Published.Count).IsEqualTo(2)
      .Because("three digests at a chunk bound of two → manifests of 2 + 1.");
    await Assert.That(transport.Published.All(p => p.Envelope.Target == "auditor-svc")).IsTrue()
      .Because("manifests are DIRECTED at the requester — nobody else audits with them.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var first = (IntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(IntegrityManifest)))!;
    await Assert.That(first.OriginServiceId).IsEqualTo(coordinator.LocalServiceId);
    await Assert.That(first.Digests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task RequestReceptor_NothingEmitted_StaysSilentAsync() {
    var coordinator = new _auditCoordinator();   // OwnDigests empty
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(transport.Published).IsEmpty()
      .Because("comparison is per-bucket — absence means nothing to audit, not an empty manifest.");
  }

  [Test]
  public async Task ManifestReceptor_IdenticalFolds_StaySilentAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("identical folds prove the bucket complete — silence is the healthy steady state.");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task ManifestReceptor_MassDivergence_ReportsAreCapped_WithOneSummaryAsync() {
    // The comparator publishes one IntegrityDivergenceDetected per divergent stream, and each
    // publish is a durable outbox write. At N=2 that is invisible; at N=500 (MaxDigestsPerManifest)
    // it is 500 sequential database round-trips inside a single message handler, on one thread.
    // That starves the host's HTTP pipeline hard enough that the always-healthy /alive liveness
    // endpoint stops answering, so the pod is killed, restarts, re-audits, and starves again —
    // observed live as a fleet-wide restart loop.
    //
    // The audit worker already caps its equivalent fan-out (MaxCoverageGapReportsPerAudit) and
    // emits a single summary past the cap. The comparator is the other half of the same exchange
    // and must be bounded the same way. Every existing comparator test runs at N=2-3, which is
    // exactly why this class of defect reached production: the behavior was correct, the VOLUME
    // was never asserted.
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedDigests = [];                       // every incoming stream is a divergence
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    const int cap = 10;
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.ReportOnly,
        MaxDivergenceReportsPerManifest = cap,
        PublishReportEvents = true,
      },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var digests = Enumerable.Range(1, 200)
      .Select(i => _digest(TrackedGuid.NewMedo().Value, i, i + 1, i))
      .ToList();

    await receptor.HandleAsync(_manifest(coordinator, digests));

    var reports = dispatcher.Published.Cast<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsLessThanOrEqualTo(cap)
      .Because("each report is a durable write; an unbounded fan-out is what starves the pipeline.");
    await Assert.That(reports.Count).IsGreaterThan(0)
      .Because("capping must not silence divergence entirely — the first ones still have to be named.");
  }

  /// <summary>
  /// The default: detect and repair, but publish nothing.
  ///
  /// <para>
  /// Nothing in the framework consumes <see cref="IntegrityDivergenceDetected"/>, and each one
  /// carries its own ReportStreamId — so every report minted a NEW stream, with a stream row, an
  /// outbox row, an event-store pointer and body, and perspective work items. With no consumer no
  /// cursor advances past them, so the consumption-gated reaper can never collect them: unbounded
  /// permanent growth in the tables the work pump scans on every poll, rather than a backlog that
  /// drains. That is what turned a reporting feature into the thing that saturated a shared server.
  /// </para>
  ///
  /// <para>
  /// What must NOT change is detection. The ledger still records the divergence and the repair
  /// still goes out — this test asserts exactly that split, because a "fix" that quietly stopped
  /// auditing would look identical from the outbox's point of view.
  /// </para>
  /// </summary>
  [Test]
  public async Task ManifestReceptor_ByDefault_DetectsAndRepairsButPublishesNoReportsAsync() {
    var coordinator = new _auditCoordinator();
    var mismatched = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    // No PublishReportEvents — the production default.
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(mismatched, 11, 21, 2)]));

    await Assert.That(dispatcher.Published.OfType<IntegrityDivergenceDetected>().Any()).IsFalse()
      .Because("a durable event nothing reads, on a stream nothing can reap, is pure cost");

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("REPAIR is the closed half of the loop and must be completely unaffected — "
               + "silencing the report must not silence the fix");
  }

  /// <summary>
  /// With report publishing disabled (the production default), <c>reportsPublished</c> stays 0 —
  /// which made the "divergence reports capped: N divergent, 0 reported" WARNING fire on every
  /// comparison that found anything. That message describes hitting <c>MaxReportsPerAudit</c>;
  /// firing it for a deliberate configuration choice reads as data loss ("the remainder stays
  /// unhealed") when the ledger row was written and the repair went out. The cap warning must be
  /// conditional on publishing actually being enabled.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_ReportsDisabled_DoesNotWarnReportsCappedAsync() {
    var coordinator = new _auditCoordinator();
    var mismatched = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var logger = new _capturingLogger<IntegrityManifestReceptor>();
    // No PublishReportEvents — the production default.
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), logger);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(mismatched, 11, 21, 2)]));

    await Assert.That(logger.Entries.Any(e => e.Message.Contains("reports capped"))).IsFalse()
      .Because("publishing was never attempted, so nothing was capped — the warning is reserved "
               + "for MaxReportsPerAudit actually truncating an enabled report stream");
  }

  [Test]
  public async Task ManifestReceptor_Divergence_ReportsAndCappedRepairsAsync() {
    var coordinator = new _auditCoordinator();
    var mismatched = TrackedGuid.NewMedo().Value;
    var missing = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];   // fold differs; `missing` absent
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRepairRequestsPerAudit = 1, PublishReportEvents = true },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(mismatched, 11, 21, 2), _digest(missing, 12, 22, 3)]));

    var reports = dispatcher.Published.Cast<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(2)
      .Because("a differing fold AND a missing stream are both divergences worth naming.");
    var missingReport = reports.Single(r => r.AuditedStreamId == missing);
    await Assert.That(missingReport.LocalCount).IsEqualTo(0);
    await Assert.That(missingReport.OriginCount).IsEqualTo(3);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the audit repair cap is a hard budget per manifest chunk.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var command = (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
    await Assert.That(command.StreamIds!).IsEquivalentTo([mismatched])
      .Because("audit repair is STREAM-scoped — exactly the divergent bucket, nothing broader.");
    await Assert.That(command.EventTypes!).IsEquivalentTo(["Contracts.TypeX"]);
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(command.StateOnly).IsFalse()
      .Because("audit repair is REPAIR semantics — the delivery a live subscriber missed, receptors and all.");
  }

  [Test]
  public async Task Registrar_RegistersBothReceptorsAtThreeStagesAsync() {
    var registry = new _recordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityManifestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestRequestReceptor>.Instance, NullLogger<IntegrityManifestReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Count).IsEqualTo(6);
    await Assert.That(registry.Registered.Count(r => r.Msg == typeof(RequestIntegrityManifest))).IsEqualTo(3);
    await Assert.That(registry.Registered.Count(r => r.Msg == typeof(IntegrityManifest))).IsEqualTo(3);
  }

  // ── A1c: hierarchical (type-level) exchange + table-driven compares ──────

  [Test]
  public async Task RequestReceptor_TypesLevel_AnswersFromTypeTableAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.OwnTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
    });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.Level).IsEqualTo(ManifestLevel.Types);
    await Assert.That(manifest.Recomputed).IsFalse().Because("Table-driven answers say so — the consumer then compares against ITS table.");
    await Assert.That(manifest.Digests.Count).IsEqualTo(1);
    await Assert.That(manifest.Digests[0].StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(manifest.Digests[0].DigestLo).IsEqualTo(41L);
  }

  [Test]
  public async Task RequestReceptor_TypesLevelWithRecompute_RollsUpComputedRowsAsync() {
    var coordinator = new _auditCoordinator();
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    coordinator.OwnDigests = [_digest(s1, 0b1100, 0b0110, 2), _digest(s2, 0b1010, 0b0011, 3)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
      UseRecompute = true,
    });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.Recomputed).IsTrue().Because("The sweep cycle answers from the event-store recompute.");
    await Assert.That(manifest.Digests.Count).IsEqualTo(1).Because("Two stream rows of one (tenant, type) roll up to one type row.");
    await Assert.That(manifest.Digests[0].DigestLo).IsEqualTo((long)(0b1100 ^ 0b1010))
      .Because("The type digest is the XOR of its stream buckets.");
    await Assert.That(manifest.Digests[0].EventCount).IsEqualTo(5);
  }

  [Test]
  public async Task RequestReceptor_TypesLevelRecompute_RollsUpAtTheStoreAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.OwnDigests = [_digest(TrackedGuid.NewMedo().Value, 1, 2, 1)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
      UseRecompute = true,
    });

    await Assert.That(coordinator.TypeComputeCalls).IsEqualTo(1);
    await Assert.That(coordinator.StreamComputeCalls).IsEqualTo(0)
      .Because("a types-level answer must roll up AT THE STORE — materializing one row per stream " +
               "to answer a types-level request has memory-killed origins with large stores.");
  }

  [Test]
  public async Task RequestReceptor_ConcurrentRequests_AnswerOneAtATimeAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.OwnDigests = [_digest(TrackedGuid.NewMedo().Value, 1, 2, 1)];
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.BlockFirstCompute = gate;
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    // A enters its compute synchronously and parks INSIDE the answer.
    var taskA = receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "svc-a",
      Topic = "t",
      EventTypes = ["A"],
      Level = ManifestLevel.Types,
      UseRecompute = true,
    });
    await Assert.That(coordinator.ComputeLog).IsEquivalentTo(["A"]);

    // B starts while A holds the gate: without the gate it would enter its compute right here.
    var taskB = receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "svc-b",
      Topic = "t",
      EventTypes = ["B"],
      Level = ManifestLevel.Types,
      UseRecompute = true,
    });
    await Assert.That(coordinator.ComputeLog).IsEquivalentTo(["A"])
      .Because("one manifest answer at a time per process — concurrent request bursts each " +
               "recomputing digests is exactly what memory-killed origins.");

    gate.SetResult();
    await taskA;
    await taskB;
    await Assert.That(coordinator.ComputeLog[0]).IsEqualTo("A");
    await Assert.That(coordinator.ComputeLog).Contains("B");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelRecomputed_UsesStoreRollUpAsync() {
    var coordinator = new _auditCoordinator();
    var streamId = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(streamId, 1, 2, 1)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(new IntegrityManifest {
      ManifestStreamId = coordinator.OriginId,
      OriginServiceId = coordinator.OriginId,
      OriginServiceName = "origin-svc",
      Level = ManifestLevel.Types,
      Recomputed = true,
      Digests = [new StreamDigest {
        TenantScope = null,
        EventType = "Contracts.TypeX",
        StreamId = Guid.Empty,
        DigestLo = 1,
        DigestHi = 2,
        EventCount = 1,
      }],
    });

    await Assert.That(coordinator.TypeComputeCalls).IsEqualTo(1);
    await Assert.That(coordinator.StreamComputeCalls).IsEqualTo(0)
      .Because("the consumer's half of a types-level comparison must also roll up at the store — " +
               "consumers with unpopulated digest lanes recompute on EVERY manifest chunk.");
  }

  [Test]
  public async Task ManifestReceptor_ConcurrentManifests_CompareOneAtATimeAsync() {
    var coordinator = new _auditCoordinator();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.BlockFirstCompute = gate;
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    IntegrityManifest manifest(string tag) => new() {
      ManifestStreamId = coordinator.OriginId,
      OriginServiceId = coordinator.OriginId,
      OriginServiceName = "origin-svc",
      Level = ManifestLevel.Types,
      Recomputed = true,
      Digests = [new StreamDigest {
        TenantScope = null,
        EventType = tag,
        StreamId = Guid.Empty,
        DigestLo = 9,
        DigestHi = 9,
        EventCount = 1,
      }],
    };

    var taskA = receptor.HandleAsync(manifest("A"));
    await Assert.That(coordinator.ComputeLog).IsEquivalentTo(["A"]);

    // B completes IMMEDIATELY — declined, not queued. Queued manifests held their deserialized
    // payloads while waiting, and when chunks arrive faster than one comparison completes that
    // queue is the heap (observed live as a fleet-wide OOM-crashloop). A declined chunk's
    // buckets simply re-audit next cycle.
    await receptor.HandleAsync(manifest("B"));
    await Assert.That(coordinator.ComputeLog).IsEquivalentTo(["A"])
      .Because("one manifest comparison at a time per process — and never a memory queue behind it");

    gate.SetResult();
    await taskA;
    await Assert.That(coordinator.ComputeLog).IsEquivalentTo(["A"])
      .Because("the declined chunk never runs from a queue — it waits durably in the audit cadence, not in memory");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelMatch_NoDrillDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty()
      .Because("Matching type roll-ups prove every stream bucket of the type complete — no drill-down.");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("Reports only ever come from the stream-level compare.");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelMismatch_SendsCappedDrillDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)];   // differs; TypeY missing
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 1, PublishReportEvents = true }, dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [
      _typeDigest("Contracts.TypeX", 41, 42, 5),
      _typeDigest("Contracts.TypeY", 51, 52, 2),
    ], ManifestLevel.Types));

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("Two mismatched types at a drill-down cap of one → exactly one escalation this cycle.");
    var request = _deserializeRequest(transport.Published[0].Envelope);
    await Assert.That(request.Level).IsEqualTo(ManifestLevel.Streams)
      .Because("The drill-down asks for stream granularity of the mismatched types only.");
    await Assert.That(request.EventTypes!.Count).IsEqualTo(1);
    await Assert.That(request.UseRecompute).IsFalse().Because("Table-driven cycles drill down table-driven.");
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(dispatcher.Published).IsEmpty().Because("Type-level mismatches escalate; they never report directly.");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevel_SettleSkipsFreshBucketsAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)];   // differs...
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    // ...but the origin's bucket changed moments ago — in-flight deliveries, not divergence.
    await receptor.HandleAsync(_manifest(coordinator, [
      _typeDigest("Contracts.TypeX", 41, 42, 5) with { UpdatedAt = DateTimeOffset.UtcNow },
    ], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty()
      .Because("A bucket updated inside the settle window is skipped — the incremental equivalent of the recompute's settle filter.");
  }

  [Test]
  public async Task ManifestReceptor_StreamLevel_TableMode_ComparesAgainstTableAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    // The TABLE lane disagrees with the origin; the recompute lane would agree — the report
    // proves table-driven manifests compare against the consumer's TABLE.
    coordinator.ReceivedTableDigests = [_digest(stream, 99, 21, 1)];
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    var reports = dispatcher.Published.Cast<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(1)
      .Because("Non-recomputed manifests compare against the maintained table, not a fresh recompute.");
  }

  [Test]
  public async Task ManifestReceptor_StreamLevel_RecomputedManifest_ComparesAgainstRecomputeAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    // Inverse of the table-mode test: recompute agrees, table disagrees — a sweep manifest
    // (Recomputed=true) must stay silent because it compares against the consumer's recompute.
    coordinator.ReceivedTableDigests = [_digest(stream, 99, 21, 1)];
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)], recomputed: true));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("Sweep manifests compare recompute-to-recompute end to end.");
  }

  // ── storm-bounding: batched directed repairs, ledger suppression, no fallback ──

  [Test]
  public async Task ManifestReceptor_Divergence_BatchesRepairsIntoOneDirectedRequestAsync() {
    var coordinator = new _auditCoordinator();   // nothing local — every origin bucket diverges
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    var s3 = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { RepairDrainEnabled = false, PublishReportEvents = true }, dispatcher: dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(s1, 11, 21, 2), _digest(s2, 12, 22, 1), _digest(s3, 13, 23, 3)]));

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("divergent streams of one (tenant, type) batch into ONE repair request — " +
               "per-stream commands multiplied every storm by the stream count.");
    var command = _deserializeRedelivery(transport.Published[0].Envelope);
    await Assert.That(command.StreamIds!).IsEquivalentTo([s1, s2, s3]);
    await Assert.That(transport.Published[0].Destination.Address).IsEqualTo("origin.requests")
      .Because("the request publishes to the ORIGIN-carried address — never anywhere else.");
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
  }

  [Test]
  public async Task ManifestReceptor_RepeatedManifest_SuppressesReportsAndRepairsAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { RepairDrainEnabled = false, PublishReportEvents = true }, dispatcher: dispatcher, tracker: tracker, ledger: ledger);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    var manifest = _manifest(coordinator, [_digest(stream, 11, 21, 2)]);

    await receptor.HandleAsync(manifest);
    await Assert.That(dispatcher.Published.Count).IsEqualTo(1).Because("precondition: first sighting reports.");
    await Assert.That(transport.Published.Count).IsEqualTo(1).Because("precondition: first sighting repairs.");

    await receptor.HandleAsync(manifest);

    await Assert.That(dispatcher.Published.Count).IsEqualTo(1)
      .Because("the SAME unhealed divergence re-detected on the next cycle is cadence, not news — " +
               "unbounded re-reporting is what flooded a live outbox with tens of thousands of rows.");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the repair was already requested; the retry waits out the ledger's backoff.");
  }

  [Test]
  public async Task ManifestReceptor_UnknownOriginRequestTopic_SkipsRepairPublishAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(transport.Published).IsEmpty()
      .Because("no origin-carried request address → NO publish. The old fallback published to the " +
               "requester's own topic, which fanned the request out to every service (and back to " +
               "itself); the origin's next checkpoint teaches the address and the repair rides then.");
    await Assert.That(dispatcher.Published.Count).IsEqualTo(1)
      .Because("the report still flows — only the misroutable request is withheld.");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelMismatch_UnknownOriginTopic_SkipsDrillDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty()
      .Because("the drill-down is a directed request too — without the origin's address it must " +
               "wait for a checkpoint, not broadcast off the requester's own topic.");
  }

  /// <summary>
  /// The drill-down cap must ROTATE across cycles, not truncate the same tail forever. With a
  /// deterministic mismatch order and a cap of one, taking the first N every cycle means types
  /// past the cap are never drilled, never stream-compared, never repaired — observed live as a
  /// backlog whose largest deficit types received zero repair grants across many audit cycles
  /// while one early-sorting type consumed the entire budget every time.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_DrillDownCap_RotatesAcrossCycles_NoTypeStarvesAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [
      _typeDigest("Contracts.TypeX", 99, 42, 4),   // differs from origin
      _typeDigest("Contracts.TypeY", 98, 41, 3),   // differs from origin
    ];
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 1, PublishReportEvents = true }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    var manifest = _manifest(coordinator, [
      _typeDigest("Contracts.TypeX", 41, 42, 5),
      _typeDigest("Contracts.TypeY", 51, 52, 2),
    ], ManifestLevel.Types);

    // Two audit cycles deliver the same two-type mismatch; the cap allows one drill-down each.
    await receptor.HandleAsync(manifest);
    await receptor.HandleAsync(manifest);

    var drilled = transport.Published
      .Select(p => _deserializeRequest(p.Envelope))
      .SelectMany(r => r.EventTypes ?? [])
      .ToHashSet(StringComparer.Ordinal);
    await Assert.That(drilled.Count).IsEqualTo(2)
      .Because("a capped cycle defers types past the cap to LATER cycles — it must not re-pick " +
               "the same head every time, or every type past the cap starves forever");
  }

  /// <summary>
  /// A repair grant burned while the origin's request topic is unlearned is an attempt spent on
  /// a request that never left the process: the bucket enters backoff (and eventually permanent
  /// attempt-exhaustion) without the origin ever being asked. When the next checkpoint teaches
  /// the address, the re-offered comparison must be able to send immediately — the earlier
  /// non-send must not have consumed the bucket's budget.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_UnlearnedTopicSkip_DoesNotBurnTheRepairAttemptAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, PublishReportEvents = true }, tracker: tracker);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    var manifest = _manifest(coordinator, [_digest(stream, 11, 21, 2)]);

    // Cycle 1: deficit found, repair granted — but the origin's topic is unlearned, so nothing
    // can be sent.
    await receptor.HandleAsync(manifest);
    await Assert.That(transport.Published).IsEmpty()
      .Because("without the origin-carried address the request is withheld (established contract)");

    // The origin's checkpoint teaches the address; the next comparison re-offers the deficit.
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    await receptor.HandleAsync(manifest);

    var redeliveries = transport.Published
      .Select(p => _deserializeRedelivery(p.Envelope))
      .Where(r => r.StreamIds!.Contains(stream))
      .ToList();
    await Assert.That(redeliveries.Count).IsEqualTo(1)
      .Because("the unlearned-topic skip must not consume the bucket's attempt budget — once the " +
               "address exists the repair goes out at once, not after a backoff that was never " +
               "buying anything");
  }

  /// <summary>
  /// The seal certifies "this window was verified complete" — it must never advance on a
  /// comparison that verified NOTHING. An origin answering a windowed request with zero buckets
  /// while the consumer holds buckets in that window proves nothing about completeness (and may
  /// itself be the defect); vacuously sealing it buries every divergence in the window forever.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_EmptyWindowedAnswer_LocalHasBuckets_DoesNotAdvanceTheSealAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],   // the consumer HOLDS data here
      ComputedThrough = 300,
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("zero origin buckets against non-empty local state verifies nothing — sealing " +
               "the window would certify history that was never compared");
  }

  /// <summary>
  /// The paced-drain default: the stream-level compare is DISCOVERY-ONLY. Deficits are recorded
  /// and their compared window stamped for the drain worker; no repair request leaves the
  /// compare — even with the origin's topic learned and budget available. Dispatch is the
  /// drain's job, at its own adaptive pace.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_DrainMode_RecordsAndStampsWindows_NeverSendsFromTheCompareAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.WindowedStreamResult = new WindowedDigestResult {
      Digests = [],   // nothing local — a pure deficit
      ComputedThrough = 300,
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { PublishReportEvents = false }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_digest(stream, 11, 21, 2)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(transport.Published).IsEmpty()
      .Because("drain mode: the compare records; the drain worker dispatches at its own pace");
    await Assert.That(coordinator.StampedWindows.Count).IsEqualTo(1)
      .Because("the compared window must ride the ledger row so the drain can range-bound its ask");
    await Assert.That(coordinator.StampedWindows[0].From).IsEqualTo(100L);
    await Assert.That(coordinator.StampedWindows[0].Until).IsEqualTo(300L);
    await Assert.That(coordinator.StampedWindows[0].Keys.Any(k => k.StreamId == stream)).IsTrue();
  }

  /// <summary>
  /// A windowed type-level deficit at or past BulkBackfillThresholdEvents must send ONE bulk
  /// redelivery request for the whole (tenant, type) window instead of drilling down — the
  /// stream-by-stream path drips a large deficit through page and budget caps for days.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_TypeLevelBulkDeficit_SendsOneBulkBackfill_NotDrillDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [],   // the consumer holds NOTHING for this type in the window
      ComputedThrough = 5000,
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { PublishReportEvents = false, BulkBackfillThresholdEvents = 1000 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [
      _typeDigest("Contracts.BigType", 41, 42, 2500),   // 2 500-event deficit — past the threshold
    ], ManifestLevel.Types) with {
      SinceSequence = 0,
      ComputedThrough = 5000,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    // Discriminate by the ENVELOPE TYPE, not payload shape — the two commands share enough field
    // names that either JSON deserializes "successfully" as the other.
    var redeliveries = transport.Published
      .Where(p => p.EnvelopeType?.Contains(nameof(RequestRedeliveryCommand), StringComparison.Ordinal) == true)
      .Select(p => _deserializeRedelivery(p.Envelope))
      .ToList();
    await Assert.That(redeliveries.Count).IsEqualTo(1)
      .Because("a threshold-crossing type deficit escalates to ONE range-bounded bulk backfill");
    await Assert.That(redeliveries[0].StreamIds is null || redeliveries[0].StreamIds!.Count == 0).IsTrue()
      .Because("the bulk ask carries no stream list — the origin's WHERE covers the whole type window");
    var drillDowns = transport.Published
      .Where(p => p.EnvelopeType?.Contains(nameof(RequestIntegrityManifest), StringComparison.Ordinal) == true)
      .ToList();
    await Assert.That(drillDowns).IsEmpty()
      .Because("an escalated type leaves the drill-down list — drilling beside the bulk ask double-ships the window");
  }

  /// <summary>
  /// An ATTEMPT-EXHAUSTED bulk lane must not shadow-ban its type from the per-stream path. The
  /// escalation exclusion assumes a denied grant means a backfill is already in flight — but a
  /// lane that exhausted its attempts (e.g. every ask burned into a broken transport era) is
  /// denied FOREVER, and excluding the type from drill-down too leaves the largest deficits
  /// permanently unrepairable by every path. Observed live: the biggest deficit types' synthetic
  /// bulk keys sat at the attempt cap while their types silently vanished from every audit.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_ExhaustedBulkLane_StillDrillsDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [],   // the consumer holds nothing — a pure, huge deficit
      ComputedThrough = 5000,
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    IIntegrityRepairLedger ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions {
        PublishReportEvents = false,
        BulkBackfillThresholdEvents = 1000,
        MaxRepairAttemptsPerBucket = 2,
      }, tracker: tracker, ledger: ledger);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    // Exhaust the synthetic bulk key with the SAME divergence signature the manifest will carry —
    // a changed signature resets attempts (new incident), an unchanged one preserves exhaustion.
    // This is the live catch-up shape: a static origin side whose asks all burned into a broken
    // transport era. RECENT attempts with a real backoff keep the capped lane inside its
    // terminal wait (base × 2⁶ ≈ 5.3 h) at compare time — a lane past the terminal wait now
    // correctly earns one more ask instead of staying shadow-banned.
    var bulkKey = new IntegrityRepairLedger.DivergenceKey(
      coordinator.OriginId, "tenant-a", "Contracts.BigType", Guid.Empty);
    var recent = DateTimeOffset.UtcNow.AddMinutes(-15);
    _ = await ledger.TryBeginReportAsync(bulkKey, 41, 42, 0, 0, recent, TimeSpan.FromMinutes(60));
    for (var i = 0; i < 2; i++) {
      _ = await ledger.TryBeginRepairBatchAsync([bulkKey], recent.AddMinutes(i * 6), TimeSpan.FromSeconds(300), 2, 1);
    }

    var manifest = _manifest(coordinator, [
      _typeDigest("Contracts.BigType", 41, 42, 2500),
    ], ManifestLevel.Types) with {
      SinceSequence = 0,
      ComputedThrough = 5000,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    var bulkAsks = transport.Published
      .Where(p => p.EnvelopeType?.Contains(nameof(RequestRedeliveryCommand), StringComparison.Ordinal) == true)
      .ToList();
    await Assert.That(bulkAsks).IsEmpty()
      .Because("the exhausted lane stays denied — that part of the contract stands");
    var drillDowns = transport.Published
      .Where(p => p.EnvelopeType?.Contains(nameof(RequestIntegrityManifest), StringComparison.Ordinal) == true)
      .ToList();
    await Assert.That(drillDowns.Count).IsEqualTo(1)
      .Because("a lane denied by EXHAUSTION (not in-flight) must fall back to the per-stream " +
               "drill-down — otherwise the largest deficits are permanently unrepairable by every path");
  }

  [Test]
  public async Task ManifestReceptor_HealedBucket_ForgetsLedgerStateAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: tracker, ledger: ledger);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));   // diverges
    coordinator.ReceivedTableDigests = [_digest(stream, 11, 21, 2)];
    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));   // heals
    coordinator.ReceivedTableDigests = [];
    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));   // diverges again

    await Assert.That(dispatcher.Published.Count).IsEqualTo(2)
      .Because("a bucket that healed and re-diverged is a brand-new incident — it reports " +
               "immediately instead of waiting out a stale cooldown.");
  }

  // ── hotfix 2: non-queueing gate + one ledger round trip per chunk ───────

  [Test]
  public async Task ManifestReceptor_CompareInFlight_SkipsInsteadOfQueueingAsync() {
    // Waiting on the gate looked free but was not: every queued manifest held its deserialized
    // payload, and when arrivals outpace one comparison the queue IS the heap (observed live as
    // a fleet-wide OOM-crashloop). A skipped chunk is the documented benign case — its buckets
    // re-audit next cycle.
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [];
    var blockFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.BlockForChunk = blockFirst;
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly },
      dispatcher, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var first = receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]) with {
      SinceSequence = 0,
      ComputedThrough = 100,
      ChunkCount = 1,
    }).AsTask();
    await coordinator.ForChunkEntered.Task;   // the first comparison is provably in flight

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 12, 22, 3)]) with {
      SinceSequence = 0,
      ComputedThrough = 100,
      ChunkCount = 1,
    });   // completes immediately — no queueing

    await Assert.That(coordinator.ForChunkCalls).IsEqualTo(1)
      .Because("the second chunk must be DECLINED while a comparison runs — waiting in memory is the OOM");

    blockFirst.SetResult();
    await first;
    await Assert.That(coordinator.ForChunkCalls).IsEqualTo(1);
  }

  [Test]
  public async Task ManifestReceptor_ConsultsTheLedgerOncePerChunk_NotPerBucketAsync() {
    // The per-bucket consult made a 500-bucket chunk up to ~1000 sequential round trips — each
    // comparison took seconds, arrivals outpaced service, and the resulting in-memory queue was
    // the OOM. One batched consult per decision kind, per chunk.
    var ledger = new _countingLedger();
    var coordinator = new _auditCoordinator();
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    var s3 = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(s3, 13, 23, 4)];   // s3 heals; s1/s2 are deficits
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker, ledger: ledger);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator,
      [_digest(s1, 11, 21, 2), _digest(s2, 12, 22, 3), _digest(s3, 13, 23, 4)]));

    await Assert.That(ledger.ReportBatchCalls).IsEqualTo(1)
      .Because("one report consult for the whole chunk — per-bucket round trips are the throughput killer");
    await Assert.That(ledger.RepairBatchCalls).IsEqualTo(1);
    await Assert.That(ledger.HealedBatchCalls).IsEqualTo(1);
    await Assert.That(ledger.SingleCalls).IsEqualTo(0)
      .Because("no per-bucket fallback when the batch path is available");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("both deficits repair through the batched decisions — batching must not blunt repair");
  }

  private sealed class _countingLedger : IIntegrityRepairLedger {
    public int SingleCalls;
    public int ReportBatchCalls;
    public int RepairBatchCalls;
    public int HealedBatchCalls;

    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long ol, long oh, long ll, long lh,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) {
      SingleCalls++;
      return ValueTask.FromResult(true);
    }
    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
        TimeSpan baseBackoff, int maxAttempts, CancellationToken ct = default) {
      SingleCalls++;
      return ValueTask.FromResult(true);
    }
    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken ct = default) {
      SingleCalls++;
      return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<bool>> TryBeginReportBatchAsync(IReadOnlyList<IntegrityReportObservation> observations,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken ct = default) {
      ReportBatchCalls++;
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. observations.Select(_ => true)]);
    }
    public ValueTask<IReadOnlyList<bool>> TryBeginRepairBatchAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants, CancellationToken ct = default) {
      RepairBatchCalls++;
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. keys.Select((_, i) => i < maxGrants)]);
    }
    public ValueTask MarkHealedBatchAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, CancellationToken ct = default) {
      HealedBatchCalls++;
      return ValueTask.CompletedTask;
    }
  }

  // ── hotfix: the stream-level local compare is bounded by the CHUNK ──────

  [Test]
  public async Task ManifestReceptor_WindowedStreamChunk_FoldsOnlyTheChunksStreamsAsync() {
    // Observed live, fleet-wide: first-contact windowed audits (seal 0) drill down to stream
    // manifests, and the local compare folded the WHOLE window — every stream in the lane — to
    // check one 500-stream chunk. Pods OOMed in seconds, on every service at once. The local
    // side only ever needs the streams THE CHUNK NAMES.
    var coordinator = new _auditCoordinator();
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(s1, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_digest(s1, 11, 21, 2), _digest(s2, 12, 22, 3)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.ForChunkStreamsSeen).IsNotNull()
      .Because("the chunk-bounded fold is the ONLY acceptable local read at stream level");
    await Assert.That(coordinator.ForChunkStreamsSeen!.OrderBy(s => s).ToList())
      .IsEquivalentTo(new[] { s1, s2 }.OrderBy(s => s).ToList())
      .Because("exactly the chunk's streams — folding the lane to check a chunk is the OOM this fixes");
    await Assert.That(coordinator.ForChunkSinceSeen).IsEqualTo(100L);
    await Assert.That(coordinator.ForChunkUntilSeen).IsEqualTo(300L)
      .Because("window-vs-window, bounded by the chunk in the stream dimension");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("s2 is a deficit (missing locally) and still repairs — bounding must not blunt detection");
  }

  [Test]
  public async Task ManifestReceptor_LegacyStreamChunk_FallsBackChunkBounded_NotWholeStoreAsync() {
    // The pre-windowed shape of the same OOM: a legacy manifest against an unpopulated digest
    // lane fell back to a WHOLE-STORE recompute per chunk. The bounded fold answers the same
    // question for just the named streams.
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(stream, 99, 21, 1)];   // deficit vs origin's 2
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(coordinator.ForChunkStreamsSeen!).IsEquivalentTo([stream]);
    await Assert.That(coordinator.ForChunkSinceSeen).IsNull()
      .Because("a legacy manifest has no window — full history, but only for the named streams");
    await Assert.That(coordinator.StreamComputeCalls).IsEqualTo(0)
      .Because("the whole-store recompute is the memory-killer; it remains only as the compat path for engines without the bounded fold");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the deficit still repairs through the bounded path");
  }

  // ── #80-F: origin generation — seal coherence across legitimate mutation ─

  [Test]
  public async Task RequestReceptor_StampsTheOriginGeneration_OnEveryAnswerAsync() {
    var coordinator = new _auditCoordinator { OriginGeneration = 7 };
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.OwnDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.OriginGeneration).IsEqualTo(7L)
      .Because("the generation is how a consumer distinguishes legitimate history mutation from damage — every answer carries it");
  }

  [Test]
  public async Task ManifestReceptor_GenerationChanged_ResetsAndSkipsTheComparisonAsync() {
    // The origin's history legitimately moved (a close, a reclassification). This round's
    // comparison was aligned to the OLD world — running it would alarm on deliberate change.
    // The guard resets the seal; the next audit re-verifies from the beginning.
    var coordinator = new _auditCoordinator { SealGenerationCoherent = false };
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_digest(stream, 11, 21, 2)]) with { OriginGeneration = 9 };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.GenerationGuardSeen).IsEqualTo(9L)
      .Because("the guard runs against the carried generation before any comparison");
    await Assert.That(transport.Published).IsEmpty()
      .Because("no repairs from a comparison aligned to the old world");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("and no divergence reports — deliberate change is not damage");
  }

  [Test]
  public async Task ManifestReceptor_GenerationUnchanged_ComparesNormallyAsync() {
    var coordinator = new _auditCoordinator { SealGenerationCoherent = true };
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [];   // missing bucket → deficit → repair
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_digest(stream, 11, 21, 2)]) with { OriginGeneration = 9 };
    await receptor.HandleAsync(manifest);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("a coherent generation is the steady state — the deficit repairs exactly as before");
  }

  // ── #80-C: deficit drives repair; equal-count mismatch drives alarm ─────

  [Test]
  public async Task ManifestReceptor_EqualCountDifferentFold_AlarmsButNeverRequestsRepairAsync() {
    // The non-convergence at the heart of the observed storm: counts agree but folds differ, so
    // the consumer holds the same NUMBER of events with different IDENTITY. Redelivery re-ships
    // what the origin has, dedup drops what the consumer already holds, the fold never moves —
    // and the repair loop retries forever. Identity damage needs a human, not a redelivery.
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(stream, 99, 98, 2)];   // count 2, folds differ
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(transport.Published).IsEmpty()
      .Because("redelivery cannot change identity the consumer already holds — a repair request here loops forever");
    var reports = dispatcher.Published.OfType<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(1)
      .Because("the ALARM half must survive the repair suppression — this bucket needs eyes, not silence");
    await Assert.That(reports[0].LocalCount).IsEqualTo(2);
    await Assert.That(reports[0].AutoRepairRequested).IsFalse();
  }

  [Test]
  public async Task ManifestReceptor_LocalExtra_AlarmsButNeverRequestsRepairAsync() {
    // The consumer holds MORE than the origin claims. Redelivery only ever adds — it cannot
    // converge a surplus — and auto-deleting local history on a remote's say-so is not a thing
    // the framework will ever do. Investigation item.
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(stream, 99, 98, 5)];   // 5 held, origin claims 2
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(transport.Published).IsEmpty()
      .Because("more-than-the-origin cannot be fixed by asking the origin for more");
    await Assert.That(dispatcher.Published.OfType<IntegrityDivergenceDetected>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task ManifestReceptor_WindowedManifest_RangeBoundsTheRepairAsync() {
    // #80-C range-bounded backfill: a deficit found while comparing a WINDOW is a deficit IN THAT
    // WINDOW — the repair request carries the range, so the origin re-ships a slice instead of a
    // stream's whole history. [since, until) maps to the redelivery command's exclusive-floor /
    // inclusive-ceiling pair.
    var coordinator = new _auditCoordinator();
    var missing = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    // A windowed manifest is compared against a WINDOWED local fold — window counts against
    // all-history table folds would fabricate mismatches on any stream with prior history.
    var manifest = _manifest(coordinator, [_digest(missing, 12, 22, 3)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.ForChunkSinceSeen).IsEqualTo(100L)
      .Because("the local fold must cover the manifest's exact window, or the buckets are not comparable");
    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var command = _deserializeRedelivery(transport.Published[0].Envelope);
    await Assert.That(command.FromCommitSequence).IsEqualTo(99L)
      .Because("[100, 300) maps to exclusive floor 99 — sequence 100 must be included");
    await Assert.That(command.ToCommitSequence).IsEqualTo(299L)
      .Because("and inclusive ceiling 299 — the origin re-ships the window, not all history");
  }

  [Test]
  public async Task ManifestReceptor_CleanCompleteWindow_AdvancesTheSealAsync() {
    // The seal-advance rule: every bucket in the window matched, the answer was ONE complete
    // chunk with no resume cursor — only then has the whole window provably been verified, and
    // only then may the next audit start past it.
    var coordinator = new _auditCoordinator();
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
      ComputedThrough = 300,
    };
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(300L)
      .Because("a clean complete window is verified history — the seal is what stops re-verifying it forever");
    await Assert.That(coordinator.SealAdvancedOrigin).IsEqualTo(coordinator.OriginId);
  }

  [Test]
  public async Task ManifestReceptor_WindowedMismatch_KeepsTheSeal_AndDrillsDownWindowedAsync() {
    // A mismatch means the window is NOT verified: the seal stays put (the same window re-audits
    // after repair), and the drill-down inherits the window so the stream-level exchange — and
    // the repairs it spawns — stay bounded to the range that actually disagreed.
    var coordinator = new _auditCoordinator();
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 99, 98, 5)],   // fold differs
      ComputedThrough = 300,
    };
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("an unverified window must re-audit — advancing the seal would bury the divergence forever");
    var drillDown = transport.Published
      .Select(p => p.Envelope).OfType<MessageEnvelope<RequestIntegrityManifest>>().Select(e => e.Payload)
      .Concat(transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Where(r => r is not null)!)
      .First();
    await Assert.That(drillDown!.Level).IsEqualTo(ManifestLevel.Streams);
    await Assert.That(drillDown.Windowed).IsTrue()
      .Because("the drill-down inherits the window — an unwindowed drill-down would re-ship all history");
    await Assert.That(drillDown.SinceSequence).IsEqualTo(100L);
    await Assert.That(drillDown.UntilSequence).IsEqualTo(300L);
  }

  [Test]
  public async Task ManifestReceptor_MultiChunkWindow_NeverAdvancesTheSealAsync() {
    // Chunks carry no assembly protocol — a lost chunk's buckets simply never arrive. With more
    // than one chunk this receiver cannot know it saw the whole window, so it must not certify it.
    var coordinator = new _auditCoordinator();
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
      ComputedThrough = 300,
    };
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 2,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("certifying a window from one chunk of two would seal over whatever the lost chunk carried");
  }

  private static RequestIntegrityManifest? _tryDeserializeRequest(IMessageEnvelope envelope) {
    try {
      var options = JsonContextRegistry.CreateCombinedOptions();
      return (RequestIntegrityManifest?)JsonSerializer.Deserialize(
        ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
        options.GetTypeInfo(typeof(RequestIntegrityManifest)));
    } catch (JsonException) {
      return null;
    }
  }

  // ── #80-B: negotiated scope — the origin honors windowed asks ───────────

  [Test]
  public async Task RequestReceptor_WindowedTypesAsk_AnswersFromTheWindowedRead_WithTheWatermarkAsync() {
    var coordinator = new _auditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 71, 72, 4)],
        ComputedThrough = 900,
      },
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
      Windowed = true,
      SinceSequence = 400,
    });

    await Assert.That(coordinator.WindowedSinceSeen).IsEqualTo(400L)
      .Because("the asker's watermark IS the window start — anything else re-ships verified history");
    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.ComputedThrough).IsEqualTo(900L)
      .Because("the watermark rides the answer so the asker knows what it got and what to ask for next");
    await Assert.That(manifest.SinceSequence).IsEqualTo(400L);
    await Assert.That(manifest.Digests.Count).IsEqualTo(1);
    await Assert.That(manifest.Digests[0].DigestLo).IsEqualTo(71L);
  }

  [Test]
  public async Task RequestReceptor_WindowedQuietWindow_StillAnswers_SoTheSealCanAdvanceAsync() {
    // A window with no matching events is NOT the legacy nothing-emitted case: coverage advanced
    // even though no digests exist, and only an answer can carry that watermark. Legacy silence
    // here would freeze the asker's seal forever on a quiet type.
    var coordinator = new _auditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 500 },
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Types,
      Windowed = true,
      SinceSequence = 100,
    });

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("an empty answer with a watermark is progress; silence would freeze the seal");
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.Digests).IsEmpty();
    await Assert.That(manifest.ComputedThrough).IsEqualTo(500L);
  }

  [Test]
  public async Task RequestReceptor_WindowedNothingNewSettled_StaysSilentAsync() {
    // ComputedThrough == since means the origin has nothing settled beyond the asker's watermark.
    // There is no progress to report — the asker simply re-asks on its next cadence.
    var coordinator = new _auditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 100 },
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Types,
      Windowed = true,
      SinceSequence = 100,
    });

    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task RequestReceptor_WindowedStreamsAsk_CarriesTheResumeCursorAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var cursor = TrackedGuid.NewMedo().Value;
    var coordinator = new _auditCoordinator {
      WindowedStreamResult = new WindowedDigestResult {
        Digests = [_digest(stream, 11, 21, 2)],
        ComputedThrough = 300,
        ResumeAfterStreamId = cursor,
      },
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      Level = ManifestLevel.Streams,
      Windowed = true,
      SinceSequence = 0,
      MaxDigests = 1,
      ResumeAfterStreamId = null,
    });

    await Assert.That(coordinator.WindowedMaxSeen).IsEqualTo(1)
      .Because("the asker's page bound is honored — its memory, not the origin's default, is the constraint");
    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.ResumeAfterStreamId).IsEqualTo(cursor)
      .Because("a non-null cursor tells the asker the window is incomplete — do not advance the seal");
    await Assert.That(manifest.ComputedThrough).IsEqualTo(300L);
  }

  [Test]
  public async Task RequestReceptor_WindowedButEngineCannotWindow_FallsBackToTheFullAnswerAsync() {
    // The DIM default returns null = "cannot window". The honest fallback is the legacy full
    // answer (correct, just unbounded) — never a fabricated watermark the engine cannot stand
    // behind.
    var coordinator = new _auditCoordinator {   // WindowedTypeResult stays null
      OwnTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
      Windowed = true,
      SinceSequence = 400,
    });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.ComputedThrough).IsNull()
      .Because("no watermark may be claimed when the engine could not actually window the fold");
    await Assert.That(manifest.Digests[0].DigestLo).IsEqualTo(41L);
  }

  // ── cursor-following: multi-page windows audit past page one ────────────

  [Test]
  public async Task ManifestReceptor_StreamAnswerWithCursor_FollowsWithTheSameWindowAsync() {
    // A windowed stream-level answer with a resume cursor means the window is NOT complete: the
    // origin's lane holds more streams than one page. Without following, only the first page's
    // streams were ever compared or repaired — the rest of a large lane was invisible to the
    // audit forever, and the seal could never certify the window.
    var cursor = TrackedGuid.NewMedo().Value;
    var stream = TrackedGuid.NewMedo().Value;
    var coordinator = new _auditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
      ResumeAfterStreamId = cursor,
    };
    await receptor.HandleAsync(manifest);

    var follow = transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).FirstOrDefault(r => r is not null);
    await Assert.That(follow).IsNotNull()
      .Because("a cursor is the origin saying 'there is more' — not following abandons the rest of the lane");
    await Assert.That(follow!.Level).IsEqualTo(ManifestLevel.Streams);
    await Assert.That(follow.ResumeAfterStreamId).IsEqualTo(cursor);
    await Assert.That(follow.Windowed).IsTrue();
    await Assert.That(follow.SinceSequence).IsEqualTo(100L)
      .Because("the follow-up stays inside the SAME window — a shifted window compares incomparable folds");
    await Assert.That(follow.UntilSequence).IsEqualTo(300L);
    await Assert.That(follow.EventTypes).Contains("Contracts.TypeX");
  }

  [Test]
  public async Task ManifestReceptor_StreamAnswerWithoutCursor_DoesNotFollowAsync() {
    var stream = TrackedGuid.NewMedo().Value;
    var coordinator = new _auditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    });

    await Assert.That(transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Any(r => r is not null)).IsFalse()
      .Because("a null cursor means the window answered completely — following would loop the audit");
  }

  [Test]
  public async Task ManifestReceptor_CursorFollowing_IsCappedPerWindowAsync() {
    // The cap bounds a paging burst: each follow costs the origin an epoch read and this consumer
    // a chunk compare, so a million-stream lane must not turn one audit into an unbounded chain.
    // Whatever the cap leaves unfollowed re-audits from the seal next cycle.
    var stream = TrackedGuid.NewMedo().Value;
    var coordinator = new _auditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxManifestPagesPerAudit = 2 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    for (var page = 0; page < 3; page++) {
      await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
        SinceSequence = 100,
        ComputedThrough = 300,
        ChunkCount = 1,
        ResumeAfterStreamId = TrackedGuid.NewMedo().Value,
      });
    }

    var follows = transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Count(r => r is not null);
    await Assert.That(follows).IsEqualTo(2)
      .Because("past the cap the chain stops — the rest of the lane re-audits next cycle from the seal");
  }

  [Test]
  public async Task ManifestReceptor_CursorWithoutOriginTopic_SkipsFollowAsync() {
    // Directed or not at all — the same rule every other origin-bound request obeys.
    var stream = TrackedGuid.NewMedo().Value;
    var coordinator = new _auditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
      ResumeAfterStreamId = TrackedGuid.NewMedo().Value,
    });

    await Assert.That(transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Any(r => r is not null)).IsFalse();
  }

  // ── bulk-deficit escalation: big type-level deficits skip the drip ──────

  [Test]
  public async Task ManifestReceptor_TypeDeficitAtThreshold_SendsOneStateOnlyBulkBackfillAsync() {
    // A type-level windowed roll-up showing thousands of missing events would take the per-stream
    // path days: 500-stream pages, 25 repairs per cycle. One state-only range-bounded redelivery
    // of the whole (tenant, type) window covers it in a single request — and state-only means the
    // backfilled history builds state without re-firing trigger receptors.
    var coordinator = new _auditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 100)],   // local: 100 events
        ComputedThrough = 300,
      },
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { BulkBackfillThresholdEvents = 1000 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5000)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    });

    var bulk = transport.Published
      .Where(p => p.Envelope is MessageEnvelope<JsonElement> je && je.Payload.TryGetProperty("RequesterService", out _)
        && p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true)
      .Select(p => _deserializeRedelivery(p.Envelope)).FirstOrDefault();
    await Assert.That(bulk).IsNotNull()
      .Because("a 4900-event deficit is a backfill, not a drip — one range-bounded request covers it");
    await Assert.That(bulk!.StateOnly).IsTrue()
      .Because("backfilled history must build state only — re-firing triggers replays side effects");
    await Assert.That(bulk.EventTypes).Contains("Contracts.TypeX");
    await Assert.That(bulk.StreamIds).IsNull()
      .Because("the whole type's window backfills — a stream list is exactly the drip this path skips");
    await Assert.That(bulk.FromCommitSequence).IsEqualTo(99L);
    await Assert.That(bulk.ToCommitSequence).IsEqualTo(299L);

    await Assert.That(transport.Published.Any(p => p.EnvelopeType?.Contains("RequestIntegrityManifest") == true)).IsFalse()
      .Because("the bulk path REPLACES the stream drill-down for that type — both would double-ship");
  }

  [Test]
  public async Task ManifestReceptor_TypeDeficitBelowThreshold_KeepsTheDrillDownAsync() {
    var coordinator = new _auditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 4500)],   // deficit 500 < 1000
        ComputedThrough = 300,
      },
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { BulkBackfillThresholdEvents = 1000 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5000)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    });

    await Assert.That(transport.Published.Any(p => p.EnvelopeType?.Contains("RequestIntegrityManifest") == true)).IsTrue()
      .Because("a small deficit keeps the precise per-stream path — bulk would re-ship 4500 held events");
    await Assert.That(transport.Published.Any(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true)).IsFalse();
  }

  [Test]
  public async Task ManifestReceptor_BulkBackfill_IsLedgerGatedAsync() {
    // The synthetic (origin, tenant, type, empty-stream) ledger key applies the same attempt
    // backoff every stream-level repair gets — without it every audit cycle re-ships the whole
    // window while the first backfill is still in flight.
    var coordinator = new _auditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 100)],
        ComputedThrough = 300,
      },
    };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { BulkBackfillThresholdEvents = 1000 }, tracker: tracker, ledger: ledger);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    for (var round = 0; round < 2; round++) {
      await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5000)], ManifestLevel.Types) with {
        SinceSequence = 100,
        ComputedThrough = 300,
        ChunkCount = 1,
      });
    }

    var bulkCount = transport.Published.Count(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true);
    await Assert.That(bulkCount).IsEqualTo(1)
      .Because("the second sighting inside the backoff re-ships nothing — the first backfill is in flight");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static StreamDigest _digest(Guid stream, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = "Contracts.TypeX",
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
      _auditCoordinator coordinator, List<StreamDigest> digests,
      ManifestLevel level = ManifestLevel.Streams, bool recomputed = false) => new() {
        ManifestStreamId = coordinator.OriginId,
        OriginServiceId = coordinator.OriginId,
        OriginServiceName = "origin-svc",
        Digests = digests,
        Level = level,
        Recomputed = recomputed,
      };

  private static IntegrityManifest _deserializeManifest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (IntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(IntegrityManifest)))!;
  }

  private static RequestIntegrityManifest _deserializeRequest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
  }

  private static ServiceProvider _provider(
      _auditCoordinator coordinator, _captureTransport transport,
      StreamIntegrityOptions? options = null, _captureDispatcher? dispatcher = null,
      IntegrityGapTracker? tracker = null, IIntegrityRepairLedger? ledger = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher ?? new _captureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("auditor-svc"));
    // Report publishing is OFF by default in production (nothing consumes the events, and each
    // one mints its own stream). These tests exercise the opt-in publish path, so the fallback
    // turns it on; the default-off contract has its own tests below.
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions { PublishReportEvents = true }));
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    if (ledger is not null) {
      services.AddSingleton<IIntegrityRepairLedger>(ledger);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
  }

  private static RequestRedeliveryCommand _deserializeRedelivery(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
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

  private sealed class _auditCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;
    public Guid OriginId { get; } = TrackedGuid.NewMedo().Value;
    public List<(Guid Origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, long From, long Until)> StampedWindows { get; } = [];

    public Task IntegrityStampRepairWindowsAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        long windowFrom, long windowUntil, CancellationToken cancellationToken = default) {
      StampedWindows.Add((originServiceId, keys, windowFrom, windowUntil));
      return Task.CompletedTask;
    }
    public IReadOnlyList<StreamDigest> OwnDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> ReceivedDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> OwnTableDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> ReceivedTableDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> OwnTypeDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> ReceivedTypeDigests { get; set; } = [];

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public int StreamComputeCalls;
    public int TypeComputeCalls;
    public List<string> ComputeLog { get; } = [];
    public TaskCompletionSource? BlockFirstCompute { get; set; }
    private int _computeCalls;

    public Task<IReadOnlyList<StreamDigest>> ComputeStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref StreamComputeCalls);
      return Task.FromResult(originServiceId is null ? OwnDigests : ReceivedDigests);
    }

    public async Task<IReadOnlyList<StreamDigest>> ComputeTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref TypeComputeCalls);
      lock (ComputeLog) {
        ComputeLog.Add(eventTypes is { Count: > 0 } t ? t[0] : "-");
      }
      if (Interlocked.Increment(ref _computeCalls) == 1 && BlockFirstCompute is { } gate) {
        await gate.Task;
      }
      return IntegrityDigestMath.RollUpToTypes(originServiceId is null ? OwnDigests : ReceivedDigests);
    }

    public WindowedDigestResult? WindowedTypeResult { get; set; }
    public WindowedDigestResult? WindowedStreamResult { get; set; }
    public long? WindowedSinceSeen;
    public long? WindowedUntilSeen;
    public Guid? WindowedResumeSeen;
    public int? WindowedMaxSeen;
    public long? SealAdvancedTo;
    public Guid? SealAdvancedOrigin;
    public long OriginGeneration { get; set; }
    public bool SealGenerationCoherent { get; set; } = true;
    public long? GenerationGuardSeen;

    public Task AdvanceIntegritySealAsync(Guid originServiceId, long through, CancellationToken cancellationToken = default) {
      SealAdvancedOrigin = originServiceId;
      SealAdvancedTo = through;
      return Task.CompletedTask;
    }

    public Task<long> GetIntegrityOriginGenerationAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(OriginGeneration);

    public Task<bool> EnsureIntegritySealGenerationAsync(
        Guid originServiceId, long generation, CancellationToken cancellationToken = default) {
      GenerationGuardSeen = generation;
      return Task.FromResult(SealGenerationCoherent);
    }

    public Task<WindowedDigestResult?> ComputeTypeDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      WindowedSinceSeen = sinceSequence;
      WindowedUntilSeen = untilSequence;
      return Task.FromResult(WindowedTypeResult);
    }

    public Task<WindowedDigestResult?> ComputeStreamDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, Guid? resumeAfterStreamId, int maxDigests,
      TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      WindowedSinceSeen = sinceSequence;
      WindowedUntilSeen = untilSequence;
      WindowedResumeSeen = resumeAfterStreamId;
      WindowedMaxSeen = maxDigests;
      return Task.FromResult(WindowedStreamResult);
    }

    public IReadOnlyList<Guid>? ForChunkStreamsSeen;
    public long? ForChunkSinceSeen;
    public long? ForChunkUntilSeen;
    public int ForChunkCalls;
    public TaskCompletionSource? BlockForChunk { get; set; }
    public TaskCompletionSource ForChunkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
      Guid originServiceId, IReadOnlyList<Guid> streamIds,
      long? sinceSequence, long? untilSequence, TimeSpan settleWindow,
      CancellationToken cancellationToken = default) {
      ForChunkCalls++;
      ForChunkStreamsSeen = streamIds;
      ForChunkSinceSeen = sinceSequence;
      ForChunkUntilSeen = untilSequence;
      ForChunkEntered.TrySetResult();
      if (BlockForChunk is { } gate) {
        await gate.Task;
      }
      return ReceivedDigests.Where(d => streamIds.Contains(d.StreamId)).ToList();
    }

    public Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnTableDigests : ReceivedTableDigests);

    public Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnTypeDigests : ReceivedTypeDigests);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.CompletedTask;
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

  private sealed class _recordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>Captures PublishAsync payloads; every other dispatcher member is unused here.</summary>
  private sealed class _captureDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new _receipt());
    }

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, Whizbang.Core.Dispatch.DispatchOptions options) => PublishAsync(eventData);
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, Whizbang.Core.Dispatch.DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, Whizbang.Core.Dispatch.DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

    private sealed class _receipt : IDeliveryReceipt {
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
  public async Task ManifestReceptor_ReportOnly_StillPublishesReportsAsync() {
    // ReportOnly is the operator's explicit report-and-decide opt-down. Bounding reports by
    // repairs must not silence the mode whose entire purpose is reporting without repairing.
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedDigests = [];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.ReportOnly,
        MaxDivergenceReportsPerManifest = 10,
        PublishReportEvents = true,
      },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var digests = Enumerable.Range(1, 50)
      .Select(i => _digest(TrackedGuid.NewMedo().Value, i, i + 1, i))
      .ToList();

    await receptor.HandleAsync(_manifest(coordinator, digests));

    await Assert.That(dispatcher.Published.OfType<IntegrityDivergenceDetected>().Any()).IsTrue()
      .Because("an operator who chose report-and-decide must still get reports");
  }

  /// <summary>Minimal capturing logger — records level/message for assertions on log output.</summary>
  private sealed class _capturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T> {
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => _nullScope.Instance;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class _nullScope : IDisposable {
      public static readonly _nullScope Instance = new();
      public void Dispose() { }
    }
  }
}
