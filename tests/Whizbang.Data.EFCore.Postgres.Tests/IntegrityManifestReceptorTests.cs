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
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { MaxDigestsPerManifest = 2 });
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
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRepairRequestsPerAudit = 1 },
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

    var taskB = receptor.HandleAsync(manifest("B"));
    await Assert.That(coordinator.ComputeLog).IsEquivalentTo(["A"])
      .Because("one manifest comparison at a time per process — a burst of chunks each falling " +
               "back to a full-store recompute is what memory-cycled consumers.");

    gate.SetResult();
    await taskA;
    await taskB;
    await Assert.That(coordinator.ComputeLog).Contains("B");
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
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 1 }, dispatcher, tracker: tracker);
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
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: tracker);
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
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: tracker, ledger: ledger);
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
      IntegrityGapTracker? tracker = null, IntegrityRepairLedger? ledger = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher ?? new _captureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("auditor-svc"));
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions()));
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    if (ledger is not null) {
      services.AddSingleton(ledger);
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
}
