// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Core.Priority;
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
[Category("Shard3")]
public class IntegrityManifestReceptorTests {
  // A fixed instant: these tests pass "now" explicitly, so it never needs the real clock.
  private static readonly DateTimeOffset _fixedNow = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);


  [Test]
  public async Task RequestReceptor_SendsChunkedTargetedManifestsAsync() {
    var coordinator = new AuditCoordinator();
    var stream1 = TrackedGuid.New().Value;
    var stream2 = TrackedGuid.New().Value;
    var stream3 = TrackedGuid.New().Value;
    coordinator.OwnDigests = [
      _digest(stream1, 11, 21, 2), _digest(stream2, 12, 22, 1), _digest(stream3, 13, 23, 3),
    ];
    var transport = new CaptureTransport();
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
    await Assert.That(transport.Published.All(p =>
        ControlMessageTtl.FromMetadata(p.Destination.Metadata) == TimeSpan.FromMinutes(60))).IsTrue()
      .Because("#2078: every manifest chunk is an integrity control message, so it expires at the broker");
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
    var coordinator = new AuditCoordinator();   // OwnDigests empty
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(transport.Published).IsEmpty()
      .Because("comparison is per-bucket — absence means nothing to audit, not an empty manifest.");
  }

  [Test]
  public async Task ManifestReceptor_IdenticalFolds_StaySilentAsync() {
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator {
      ReceivedDigests = []                       // every incoming stream is a divergence
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
      .Select(i => _digest(TrackedGuid.New().Value, i, i + 1, i))
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
    var coordinator = new AuditCoordinator();
    var mismatched = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();
    var mismatched = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var tracker = new IntegrityGapTracker();
    var logger = new CapturingLogger<IntegrityManifestReceptor>();
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
    var coordinator = new AuditCoordinator();
    var mismatched = TrackedGuid.New().Value;
    var missing = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];   // fold differs; `missing` absent
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    await Assert.That(command.StreamIds).IsEquivalentTo([mismatched])
      .Because("audit repair is STREAM-scoped — exactly the divergent bucket, nothing broader.");
    await Assert.That(ControlMessageTtl.FromMetadata(transport.Published[0].Destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60))
      .Because("#2078: a stream repair request expires at the broker");
    await Assert.That(command.EventTypes).IsEquivalentTo(["Contracts.TypeX"]);
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(command.StateOnly).IsFalse()
      .Because("audit repair is REPAIR semantics — the delivery a live subscriber missed, receptors and all.");
  }

  /// <summary>
  /// Priority step 1: every request the manifest receptor sends to an origin is background by construction, on the
  /// envelope it publishes. A repair replays history to this service, a drill-down asks for more detail, a bulk
  /// backfill is the largest replay the framework asks for; none of them may be served ahead of the origin's live work.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_Divergence_TheRepairRequestIsBackgroundAsync() {
    var mismatched = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator { ReceivedDigests = [_digest(mismatched, 99, 21, 1)] };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRepairRequestsPerAudit = 1, PublishReportEvents = true },
      new CaptureDispatcher(), tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(mismatched, 11, 21, 2)]));

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(transport.Published[0].Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the repair replays history to this service; the origin must not serve it ahead of its live work, and the replay must arrive here declared background");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelMismatch_TheDrillDownRequestIsBackgroundAsync() {
    var coordinator = new AuditCoordinator { ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)] };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 1, PublishReportEvents = true }, new CaptureDispatcher(), tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5), _typeDigest("Contracts.TypeY", 51, 52, 2)], ManifestLevel.Types));

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(transport.Published[0].Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a drill-down asks the origin for more detail on a mismatch; it is served from the origin's inbox behind live work");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelBulkDeficit_TheBackfillRequestIsBackgroundAsync() {
    var coordinator = new AuditCoordinator { WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 5000 } };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = false, BulkBackfillThresholdEvents = 1000 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.BigType", 41, 42, 2500)], ManifestLevel.Types) with {
      SinceSequence = 0,
      ComputedThrough = 5000,
      ChunkCount = 1,
    });

    var (bulkEnvelope, _, _) = transport.Published.Single(p => p.EnvelopeType?.Contains(nameof(RequestRedeliveryCommand), StringComparison.Ordinal) == true);
    await Assert.That(bulkEnvelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a whole-type backfill is the largest replay the framework asks for; it is background by definition");
  }

  [Test]
  public async Task Registrar_RegistersBothReceptorsAtThreeStagesAsync() {
    var registry = new RecordingRegistry();
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
    var coordinator = new AuditCoordinator {
      OwnTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)]
    };
    var transport = new CaptureTransport();
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

  /// <summary>
  /// #2058: a requester that absorbs a namespace asks for it by namespace; the origin adds its own audited types in that
  /// namespace (nested types included, other namespaces not) to the requested types.
  /// </summary>
  [Test]
  public async Task RequestReceptor_AbsorbedNamespaces_AddTheOriginsOwnTypesInThemAsync() {
    var coordinator = new AuditCoordinator {
      OwnTypeDigests = [_typeDigest("Contracts.Jobs.JobStarted, Contracts", 41, 42, 5)],
      OwnAuditedTypes = [
        "Contracts.Jobs.JobStarted, Contracts",
        "Contracts.Jobs.Outer+JobNested, Contracts",
        "Contracts.Jobs.Archive.JobArchived, Contracts",
        "Contracts.Orders.OrderShipped, Contracts",
      ],
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.Consumed, Contracts"],
      EventNamespaces = ["contracts.jobs"],
      Level = ManifestLevel.Types,
    });

    await Assert.That(coordinator.LastOwnTypeQuery).IsEquivalentTo([
      "Contracts.Consumed, Contracts",
      "Contracts.Jobs.JobStarted, Contracts",
      "Contracts.Jobs.Outer+JobNested, Contracts",
    ]).Because("the requester's consumed types plus every type this origin emits in the namespace it absorbs, and no other");
  }

  /// <summary>
  /// #2058: absorbed namespaces with no type list add the origin's types in them alone; a type with no namespace is in
  /// none.
  /// </summary>
  [Test]
  public async Task RequestReceptor_AbsorbedNamespacesWithoutATypeList_AnswerTheNamespacesTypesAsync() {
    var coordinator = new AuditCoordinator {
      OwnTypeDigests = [_typeDigest("Contracts.Jobs.JobStarted, Contracts", 41, 42, 5)],
      OwnAuditedTypes = ["Contracts.Jobs.JobStarted, Contracts", "GlobalEvent, Contracts"],
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventNamespaces = ["contracts.jobs"],
      Level = ManifestLevel.Types,
    });

    await Assert.That(coordinator.LastOwnTypeQuery).IsEquivalentTo(["Contracts.Jobs.JobStarted, Contracts"]);
  }

  /// <summary>#2058: an empty namespace list adds nothing; the request's type list is answered as it came.</summary>
  [Test]
  public async Task RequestReceptor_EmptyAbsorbedNamespaces_AnswerTheTypeListAsItCameAsync() {
    var coordinator = new AuditCoordinator {
      OwnTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
      OwnAuditedTypes = ["Contracts.Jobs.JobStarted, Contracts"],
    };
    var sp = _provider(coordinator, new CaptureTransport());
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      EventNamespaces = [],
      Level = ManifestLevel.Types,
    });

    await Assert.That(coordinator.LastOwnTypeQuery).IsEquivalentTo(["Contracts.TypeX"]);
  }

  [Test]
  public async Task RequestReceptor_TypesLevelWithRecompute_RollsUpComputedRowsAsync() {
    var coordinator = new AuditCoordinator();
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    coordinator.OwnDigests = [_digest(s1, 0b1100, 0b0110, 2), _digest(s2, 0b1010, 0b0011, 3)];
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      OwnDigests = [_digest(TrackedGuid.New().Value, 1, 2, 1)]
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      OwnDigests = [_digest(TrackedGuid.New().Value, 1, 2, 1)]
    };
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.BlockFirstCompute = gate;
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator();
    var streamId = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(streamId, 1, 2, 1)];
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.BlockFirstCompute = gate;
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)]
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)]   // differs; TypeY missing
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    await Assert.That(ControlMessageTtl.FromMetadata(transport.Published[0].Destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60))
      .Because("#2078: a drill-down request expires at the broker");
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(dispatcher.Published).IsEmpty().Because("Type-level mismatches escalate; they never report directly.");
  }

  /// <summary>
  /// #2058: a manifest that names a type nothing here consumes (an origin answering a request from before the audit
  /// was narrowed) never drills down, reports or repairs it: its redelivery would be discarded at the receive boundary,
  /// so the divergence could never heal.
  /// </summary>
  [Test]
  public async Task ManifestReceptor_TypeLevelMismatch_OfATypeNothingHereConsumes_IsNeverEscalatedAsync() {
    var coordinator = new AuditCoordinator();   // nothing stored locally: both types mismatch
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 10 }, tracker: tracker,
      consumers: new ConsumesOnly("Contracts.TypeX"));
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [
      _typeDigest("Contracts.TypeX", 41, 42, 5),
      _typeDigest("Contracts.TypeY", 51, 52, 2),
    ], ManifestLevel.Types));

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.EventTypes).IsEquivalentTo(["Contracts.TypeX"])
      .Because("only the consumed type is escalated; the other is not this service's contract");
  }

  /// <summary>#2058: a manifest naming only types nothing here consumes is not compared at all.</summary>
  [Test]
  public async Task ManifestReceptor_OnlyTypesNothingHereConsumes_ComparesNothingAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions(), tracker: tracker,
      consumers: new ConsumesOnly("Contracts.TypeX"));
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeY", 51, 52, 2)], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(coordinator.TypeComputeCalls).IsEqualTo(0)
      .Because("nothing this service keeps is in the manifest, so no local fold is computed for it");
  }

  /// <summary>#2058: the stream-level compare skips the buckets of a type nothing here consumes.</summary>
  [Test]
  public async Task ManifestReceptor_StreamLevelDivergence_OfATypeNothingHereConsumes_IsNeverRepairedAsync() {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      tracker: tracker, ledger: ledger, consumers: new ConsumesOnly("Contracts.TypeX"));
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [
      _digest(TrackedGuid.New().Value, 11, 21, 2),
      _digest(TrackedGuid.New().Value, 12, 22, 3) with { EventType = "Contracts.TypeY" },
    ]));

    var repairs = transport.Published.ConvertAll(p => _deserializeRedelivery(p.Envelope));
    await Assert.That(repairs.SelectMany(r => r.EventTypes ?? []).Distinct()).IsEquivalentTo(["Contracts.TypeX"])
      .Because("the consumed type's divergence is repaired; the other type's never is");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevel_SettleSkipsFreshBucketsAsync() {
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)]   // differs...
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    // The TABLE lane disagrees with the origin; the recompute lane would agree — the report
    // proves table-driven manifests compare against the consumer's TABLE.
    coordinator.ReceivedTableDigests = [_digest(stream, 99, 21, 1)];
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    // Inverse of the table-mode test: recompute agrees, table disagrees — a sweep manifest
    // (Recomputed=true) must stay silent because it compares against the consumer's recompute.
    coordinator.ReceivedTableDigests = [_digest(stream, 99, 21, 1)];
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();   // nothing local — every origin bucket diverges
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    var s3 = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = false, PublishReportEvents = true }, dispatcher: dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(s1, 11, 21, 2), _digest(s2, 12, 22, 1), _digest(s3, 13, 23, 3)]));

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("divergent streams of one (tenant, type) batch into ONE repair request — " +
               "per-stream commands multiplied every storm by the stream count.");
    var command = _deserializeRedelivery(transport.Published[0].Envelope);
    await Assert.That(command.StreamIds).IsEquivalentTo([s1, s2, s3]);
    await Assert.That(transport.Published[0].Destination.Address).IsEqualTo("origin.requests")
      .Because("the request publishes to the ORIGIN-carried address — never anywhere else.");
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
  }

  [Test]
  public async Task ManifestReceptor_RepeatedManifest_SuppressesReportsAndRepairsAsync() {
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = false, PublishReportEvents = true }, dispatcher: dispatcher, tracker: tracker, ledger: ledger);
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)]
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [
        _typeDigest("Contracts.TypeX", 99, 42, 4),   // differs from origin
        _typeDigest("Contracts.TypeY", 98, 41, 3),   // differs from origin
      ]
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, RepairDrainEnabled = false, PublishReportEvents = true }, tracker: tracker);
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],   // the consumer HOLDS data here
        ComputedThrough = 300,
      }
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.WindowedStreamResult = new WindowedDigestResult {
      Digests = [],   // nothing local — a pure deficit
      ComputedThrough = 300,
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [],   // the consumer holds NOTHING for this type in the window
        ComputedThrough = 5000,
      }
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = false, BulkBackfillThresholdEvents = 1000 }, tracker: tracker);
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [],   // the consumer holds nothing — a pure, huge deficit
        ComputedThrough = 5000,
      }
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [];
    var blockFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coordinator.BlockForChunk = blockFirst;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var ledger = new CountingLedger();
    var coordinator = new AuditCoordinator();
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    var s3 = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(s3, 13, 23, 4)];   // s3 heals; s1/s2 are deficits
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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

  [Test]
  public async Task ManifestReceptor_StreamLevelReport_CarriesEachDeficitsSize_SoTheLedgerCanTellAGrowingDeficitAsync() {
    // #2030: an origin's new events alone no longer reset the repair ladder; a deficit that GREW does, and only the
    // comparison knows the counts.
    var ledger = new CountingLedger();
    var coordinator = new AuditCoordinator();
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(s2, 5, 6, 1)];
    var sp = _provider(coordinator, new CaptureTransport(),
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      tracker: new IntegrityGapTracker(), ledger: ledger);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(s1, 11, 21, 2), _digest(s2, 12, 22, 4)]));

    await Assert.That(ledger.Observations.Single(o => o.Key.StreamId == s1).DeficitEvents).IsEqualTo(2L)
      .Because("the consumer holds none of the origin's two events");
    await Assert.That(ledger.Observations.Single(o => o.Key.StreamId == s2).DeficitEvents).IsEqualTo(3L)
      .Because("the origin holds four and the consumer one");
  }

  [Test]
  public async Task ManifestReceptor_AGrantedBulkBackfill_MarksTheTypeLaneAnswerPending_OverTheWindowItAskedForAsync() {
    // #2034: the per-stream drain rows of the type are answered by the bulk request, so they wait for it instead of
    // re-shipping the same window beside it.
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 5000 },
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var ledger = new RecordingIntegrityRepairLedger();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
        PublishReportEvents = false,
        BulkBackfillThresholdEvents = 1000,
        RepairAnswerTimeoutMinutes = 77,
      }, tracker: tracker, ledger: ledger);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.BigType", 41, 42, 2500)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 5000,
      ChunkCount = 1,
    });

    var mark = ledger.PendingMarks.Single();
    await Assert.That(mark.Keys.Single().OriginServiceId).IsEqualTo(coordinator.OriginId);
    await Assert.That(mark.Keys.Single().StreamId).IsEqualTo(Guid.Empty).Because("the type lane covers every stream of the type");
    await Assert.That(mark.Keys.Single().EventType).IsEqualTo("Contracts.BigType");
    await Assert.That(mark.WindowFrom).IsEqualTo(100L);
    await Assert.That(mark.WindowUntil).IsEqualTo(5000L);
    await Assert.That(mark.Timeout).IsEqualTo(TimeSpan.FromMinutes(77));
  }

  [Test]
  public async Task ManifestReceptor_ABulkBackfillTheLedgerRefuses_MarksNothingPendingAsync() {
    var refusing = new RecordingIntegrityRepairLedger { RefuseRepairs = true };
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 5000 },
    };
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, new CaptureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = false, BulkBackfillThresholdEvents = 1000 },
      tracker: tracker, ledger: refusing);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.BigType", 41, 42, 2500)], ManifestLevel.Types) with {
      SinceSequence = 0,
      ComputedThrough = 5000,
      ChunkCount = 1,
    });

    await Assert.That(refusing.PendingMarks).IsEmpty();
  }

  [Test]
  public async Task ManifestReceptor_ABulkBackfillThatCouldNotBeAddressed_MarksNothingPendingAsync() {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 5000 },
    };
    var transport = new CaptureTransport();
    var ledger = new RecordingIntegrityRepairLedger();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = false, BulkBackfillThresholdEvents = 1000 },
      tracker: new IntegrityGapTracker(), ledger: ledger);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.BigType", 41, 42, 2500)], ManifestLevel.Types) with {
      SinceSequence = 0,
      ComputedThrough = 5000,
      ChunkCount = 1,
    });

    await Assert.That(transport.Published).IsEmpty().Because("no checkpoint taught the origin's request topic");
    await Assert.That(ledger.PendingMarks).IsEmpty().Because("no request left, so no answer is on its way");
  }

  private sealed class CountingLedger : IIntegrityRepairLedger {
    public List<IntegrityReportObservation> Observations { get; } = [];
    public int SingleCalls;
    public int ReportBatchCalls;
    public int RepairBatchCalls;
    public int HealedBatchCalls;

    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
      SingleCalls++;
      return ValueTask.FromResult(true);
    }
    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
        TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) {
      SingleCalls++;
      return ValueTask.FromResult(true);
    }
    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) {
      SingleCalls++;
      return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<bool>> TryBeginReportBatchAsync(IReadOnlyList<IntegrityReportObservation> observations,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
      ReportBatchCalls++;
      Observations.AddRange(observations);
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. observations.Select(_ => true)]);
    }
    public ValueTask<IReadOnlyList<bool>> TryBeginRepairBatchAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants, CancellationToken cancellationToken = default) {
      RepairBatchCalls++;
      return ValueTask.FromResult<IReadOnlyList<bool>>([.. keys.Select((_, i) => i < maxGrants)]);
    }
    public ValueTask MarkHealedBatchAsync(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, CancellationToken cancellationToken = default) {
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
    var coordinator = new AuditCoordinator();
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(s1, 11, 21, 2)];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    await Assert.That(coordinator.ForChunkStreamsSeen!.Order().ToList())
      .IsEquivalentTo(new[] { s1, s2 }.Order().ToList())
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 99, 21, 1)];   // deficit vs origin's 2
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      dispatcher, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(coordinator.ForChunkStreamsSeen).IsEquivalentTo([stream]);
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
    var coordinator = new AuditCoordinator { OriginGeneration = 7 };
    var stream = TrackedGuid.New().Value;
    coordinator.OwnDigests = [_digest(stream, 11, 21, 2)];
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator { SealGenerationCoherent = false };
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator { SealGenerationCoherent = true };
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [];   // missing bucket → deficit → repair
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 99, 98, 2)];   // count 2, folds differ
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 99, 98, 5)];   // 5 held, origin claims 2
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator();
    var missing = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [];
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator {
      Seal = 100,   // the audit asked from the seal
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
        ComputedThrough = 300,
      }
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 5)],   // fold differs
        ComputedThrough = 300,
      }
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
        ComputedThrough = 300,
      }
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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

  // ── #2107: one audit window is capped; the asker continues from where it stopped ───────────

  private static RequestIntegrityManifest _windowedAsk(long since, long? until = null, ManifestLevel level = ManifestLevel.Types) => new() {
    RequesterService = "auditor-svc",
    Topic = "inbox",
    EventTypes = ["Contracts.TypeX"],
    Level = level,
    Windowed = true,
    SinceSequence = since,
    UntilSequence = until,
  };

  private static async Task<(AuditCoordinator Coordinator, CaptureTransport Transport)> _answerAsync(
      RequestIntegrityManifest ask, long computedThrough, int maxWindow = 1_000) {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 1, 2, 3)], ComputedThrough = computedThrough },
      WindowedStreamResult = new WindowedDigestResult { Digests = [_digest(Guid.Empty, 1, 2, 3)], ComputedThrough = computedThrough },
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { MaxAuditWindowSequences = maxWindow });
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);
    await receptor.HandleAsync(ask);
    return (coordinator, transport);
  }

  [Test]
  public async Task RequestReceptor_AnOpenEndedAsk_ReadsOneCappedWindow_AndSaysItWasCappedAsync() {
    var (coordinator, transport) = await _answerAsync(_windowedAsk(since: 400), computedThrough: 1_400);

    await Assert.That(coordinator.WindowedUntilSeen).IsEqualTo(1_400L)
      .Because("an audit asked from an old seal must not read every sequence from there to the settled head in one call");
    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.ComputedThrough).IsEqualTo(1_400L);
    await Assert.That(manifest.WindowCapped).IsTrue()
      .Because("the asker continues from the end of a capped window instead of waiting a whole audit interval");
  }

  [Test]
  public async Task RequestReceptor_EchoesTheWindowNumber_OnItsAnswerAsync() {
    var (_, transport) = await _answerAsync(_windowedAsk(since: 400) with { WindowNumber = 3 }, computedThrough: 1_400);

    await Assert.That(_deserializeManifest(transport.Published.Single().Envelope).WindowNumber).IsEqualTo(3)
      .Because("the asker counts the windows of one audit by the number its answer carries back");
  }

  [Test]
  public async Task RequestReceptor_AWindowThatSettlesBeforeTheCap_IsNotCappedAsync() {
    var (coordinator, transport) = await _answerAsync(_windowedAsk(since: 400), computedThrough: 900);

    await Assert.That(coordinator.WindowedUntilSeen).IsEqualTo(1_400L);
    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.WindowCapped).IsFalse()
      .Because("the answer reached the settled head, so there is nothing to continue to");
  }

  [Test]
  public async Task RequestReceptor_ABoundedAskInsideTheCap_IsReadAsAskedAsync() {
    var (coordinator, transport) = await _answerAsync(
      _windowedAsk(since: 400, until: 900, ManifestLevel.Streams), computedThrough: 900);

    await Assert.That(coordinator.WindowedUntilSeen).IsEqualTo(900L)
      .Because("a drill-down inherits the window its type-level answer covered, which the cap already bounded");
    await Assert.That(_deserializeManifest(transport.Published.Single().Envelope).WindowCapped).IsFalse();
  }

  [Test]
  public async Task RequestReceptor_ABoundedAskWiderThanTheCap_IsCappedAsync() {
    var (coordinator, transport) = await _answerAsync(
      _windowedAsk(since: 400, until: 5_000, ManifestLevel.Streams), computedThrough: 1_400);

    await Assert.That(coordinator.WindowedUntilSeen).IsEqualTo(1_400L);
    await Assert.That(_deserializeManifest(transport.Published.Single().Envelope).WindowCapped).IsTrue();
  }

  [Test]
  public async Task RequestReceptor_AWindowStartingNearTheEndOfTheSequence_SaturatesTheCapAsync() {
    var (coordinator, transport) = await _answerAsync(_windowedAsk(since: long.MaxValue - 10), computedThrough: long.MaxValue);

    await Assert.That(coordinator.WindowedUntilSeen).IsEqualTo(long.MaxValue)
      .Because("the cap's end saturates rather than overflowing into a negative window");
    await Assert.That(_deserializeManifest(transport.Published.Single().Envelope).WindowCapped).IsFalse();
  }

  private static (AuditCoordinator Coordinator, CaptureTransport Transport, IntegrityManifestReceptor Receptor) _consumer(
      long seal, IReadOnlyList<StreamDigest> local, bool originTopicKnown = true, bool withTracker = true,
      bool withInstanceProvider = true, Whizbang.Core.Routing.RoutingOptions? routing = null,
      StreamIntegrityOptions? options = null) {
    var coordinator = new AuditCoordinator {
      Seal = seal,
      WindowedTypeResult = new WindowedDigestResult { Digests = local, ComputedThrough = 1_100 },
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    if (originTopicKnown) {
      tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    }
    var sp = _provider(coordinator, transport, options, tracker: withTracker ? tracker : null, routing: routing,
      consumers: new ConsumesOnly("Contracts.TypeX"), withInstanceProvider: withInstanceProvider);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    return (coordinator, transport, receptor);
  }

  private static List<RequestIntegrityManifest> _requests(CaptureTransport transport) =>
    [.. transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Where(r => r is not null).Select(r => r!)];

  private static IntegrityManifest _cappedAnswer(
      AuditCoordinator coordinator, List<StreamDigest> digests, bool capped = true, int windowNumber = 0,
      long since = 100, long through = 1_100, int chunks = 1) =>
    _manifest(coordinator, digests, ManifestLevel.Types) with {
      SinceSequence = since,
      ComputedThrough = through,
      ChunkCount = chunks,
      WindowCapped = capped,
      WindowNumber = windowNumber,
    };

  [Test]
  public async Task ManifestReceptor_ACleanCappedWindow_SealsIt_AndAsksForTheNextWindowAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(1_100L);
    var next = _requests(transport).Single();
    await Assert.That(next.Level).IsEqualTo(ManifestLevel.Types);
    await Assert.That(next.Windowed).IsTrue();
    await Assert.That(next.SinceSequence).IsEqualTo(1_100L)
      .Because("the next window starts exactly where the capped one stopped, so no history is skipped");
    await Assert.That(next.UntilSequence).IsNull();
    await Assert.That(next.EventTypes).Contains("Contracts.TypeX")
      .Because("the next window audits the whole consumed surface, as the scheduled audit does");
    await Assert.That(next.WindowNumber).IsEqualTo(2)
      .Because("an answer without a window number is the audit's first window; the next one is its second");
    await Assert.That(transport.Published.Single().Envelope.Target).IsEqualTo("origin-svc");
  }

  [Test]
  public async Task ManifestReceptor_ACleanWindowThatWasNotCapped_AsksForNothingMoreAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], capped: false));

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(1_100L);
    await Assert.That(_requests(transport)).IsEmpty()
      .Because("the answer reached the origin's settled head; the next scheduled audit carries on from the seal");
  }

  [Test]
  public async Task ManifestReceptor_AStuckCappedWindow_KeepsTheSeal_AndStillAsksForTheNextWindowAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 99, 98, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], windowNumber: 1));

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("a window that did not verify is never sealed; the next audit starts again from it");
    var next = _requests(transport).Single(r => r.Level == ManifestLevel.Types);
    await Assert.That(next.SinceSequence).IsEqualTo(1_100L)
      .Because("the history behind a stuck difference is still compared in the same audit");
    await Assert.That(next.WindowNumber).IsEqualTo(2);
  }

  [Test]
  public async Task ManifestReceptor_AGapInTheWindowAfterAStuckOne_IsFoundInTheSameAudit_AndTheSealHoldsAsync() {
    // The audit's first window [100, 1100) did not verify, so the seal stays at 100. The walk carried on to
    // [1100, 2100), where this service is missing events: that window drills down like any other, but it starts above
    // the seal and so is never sealed, and the walk goes on to the third window.
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 3)]);
    coordinator.WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 41, 42, 3)], ComputedThrough = 2_100 };

    await receptor.HandleAsync(_cappedAnswer(
      coordinator, [_typeDigest("Contracts.TypeX", 77, 78, 5)], windowNumber: 2, since: 1_100, through: 2_100));

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("the seal never moves past the first window that did not verify");
    var requests = _requests(transport);
    var drillDown = requests.Single(r => r.Level == ManifestLevel.Streams);
    await Assert.That(drillDown.SinceSequence).IsEqualTo(1_100L)
      .Because("the gap is found, and repaired, in the window that holds it");
    await Assert.That(drillDown.UntilSequence).IsEqualTo(2_100L);
    var next = requests.Single(r => r.Level == ManifestLevel.Types);
    await Assert.That(next.SinceSequence).IsEqualTo(2_100L);
    await Assert.That(next.WindowNumber).IsEqualTo(3);
  }

  [Test]
  [Arguments(5, 5)]
  [Arguments(2, 2)]
  [Arguments(1, 0)]
  public async Task ManifestReceptor_TheLastWindowTheBudgetAllows_SealsButAsksForNoMoreAsync(int budget, int windowNumber) {
    var (coordinator, transport, receptor) = _consumer(
      seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)],
      options: new StreamIntegrityOptions { MaxAuditWindowsPerAudit = budget });

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], windowNumber: windowNumber));

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(1_100L);
    await Assert.That(_requests(transport)).IsEmpty()
      .Because("every window of one audit counts against the budget; the next audit continues from the seal");
  }

  [Test]
  public async Task ManifestReceptor_ACappedWindowThisServiceCannotFold_AsksForNoNextWindowAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: []);
    coordinator.WindowedTypeResult = null;   // this engine cannot fold a window: nothing was compared

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(coordinator.SealAdvancedTo).IsNull();
    await Assert.That(_requests(transport)).IsEmpty()
      .Because("walking on is only worth it while windows are actually being compared");
  }

  [Test]
  public async Task ManifestReceptor_AMultiChunkCappedWindow_AsksForNoNextWindowAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], chunks: 2));

    await Assert.That(_requests(transport)).IsEmpty()
      .Because("every chunk of the answer arrives on its own; continuing from each would ask for the next window once per chunk");
  }

  [Test]
  public async Task ManifestReceptor_AWindowThatStartsAboveTheSeal_NeverAdvancesItAsync() {
    // The seal was reset (a new origin generation) while a continuation was in flight: that window starts above the
    // seal, and sealing it would certify everything below it unverified.
    var (coordinator, transport, receptor) = _consumer(seal: 0, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("the seal advances only over a window that starts at or below it");
    await Assert.That(_requests(transport).Single().SinceSequence).IsEqualTo(1_100L)
      .Because("the window is still compared, and the walk still goes on within the budget");
  }

  [Test]
  public async Task ManifestReceptor_AQuietCappedWindow_EmptyOnBothSides_SealsIt_AndContinuesAsync() {
    // A capped slice can hold no event of any audited type. Dropping that answer froze the seal at the slice's start:
    // every later audit asked for the same empty slice and never reached the history after it.
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: []);

    await receptor.HandleAsync(_cappedAnswer(coordinator, []));

    await Assert.That(coordinator.WindowedTypesSeen).Contains("Contracts.TypeX")
      .Because("an empty answer is checked against this service's whole consumed surface in the window");
    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(1_100L);
    await Assert.That(_requests(transport).Single().SinceSequence).IsEqualTo(1_100L);
  }

  [Test]
  public async Task ManifestReceptor_AQuietCappedWindow_WithLocalEvents_DoesNotSeal_ButContinuesAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, []));

    await Assert.That(coordinator.SealAdvancedTo).IsNull()
      .Because("zero origin buckets against local events in the window verifies nothing");
    await Assert.That(_requests(transport).Single().SinceSequence).IsEqualTo(1_100L);
  }

  [Test]
  public async Task ManifestReceptor_ACleanCappedWindow_WithoutTheOriginsAddress_SealsButAsksNothingAsync() {
    var (coordinator, transport, receptor) = _consumer(
      seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)], originTopicKnown: false);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(1_100L);
    await Assert.That(transport.Published).IsEmpty()
      .Because("directed or not at all: the next scheduled audit continues from the seal instead");
  }

  [Test]
  [Arguments(false, true)]
  [Arguments(true, false)]
  public async Task ManifestReceptor_ACleanCappedWindow_WithoutTheGapTrackerOrAnIdentity_SealsButAsksNothingAsync(
      bool withTracker, bool withInstanceProvider) {
    var (coordinator, transport, receptor) = _consumer(
      seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)],
      withTracker: withTracker, withInstanceProvider: withInstanceProvider);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(1_100L);
    await Assert.That(transport.Published).IsEmpty()
      .Because("with no origin address or no requester identity there is nobody to ask and no return address");
  }

  [Test]
  public async Task ManifestReceptor_TheNextWindow_AsksForTheAbsorbedNamespacesTooAsync() {
    var (coordinator, transport, receptor) = _consumer(
      seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)],
      routing: new Whizbang.Core.Routing.RoutingOptions().AbsorbNamespaces("contracts.absorbed"));

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(_requests(transport).Single().EventNamespaces).IsEquivalentTo(["contracts.absorbed"])
      .Because("the next window audits what the receive boundary keeps, as the scheduled audit does");
  }

  [Test]
  public async Task ManifestReceptor_TheNextWindow_WithNoAbsorbedNamespaces_AsksForNoneAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: [_typeDigest("Contracts.TypeX", 41, 42, 5)]);

    await receptor.HandleAsync(_cappedAnswer(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)]));

    await Assert.That(_requests(transport).Single().EventNamespaces).IsNull();
  }

  [Test]
  public async Task ManifestReceptor_AnEmptyStreamLevelAnswer_IsStillIgnoredAsync() {
    var (coordinator, transport, receptor) = _consumer(seal: 100, local: []);

    await receptor.HandleAsync(_manifest(coordinator, []) with { SinceSequence = 100, ComputedThrough = 1_100, ChunkCount = 1 });

    await Assert.That(coordinator.SealAdvancedTo).IsNull();
    await Assert.That(coordinator.WindowedUntilSeen).IsNull()
      .Because("only a type-level answer carries the window's verdict; an empty stream page has nothing to compare");
    await Assert.That(transport.Published).IsEmpty();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 71, 72, 4)],
        ComputedThrough = 900,
      },
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 500 },
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [], ComputedThrough = 100 },
    };
    var transport = new CaptureTransport();
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
    var stream = TrackedGuid.New().Value;
    var cursor = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      WindowedStreamResult = new WindowedDigestResult {
        Digests = [_digest(stream, 11, 21, 2)],
        ComputedThrough = 300,
        ResumeAfterStreamId = cursor,
      },
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {   // WindowedTypeResult stays null
      OwnTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)],
    };
    var transport = new CaptureTransport();
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
    var cursor = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new CaptureTransport();
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
    var stream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new CaptureTransport();
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
    var stream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new CaptureTransport();
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
        ResumeAfterStreamId = TrackedGuid.New().Value,
      });
    }

    var follows = transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Count(r => r is not null);
    await Assert.That(follows).IsEqualTo(2)
      .Because("past the cap the chain stops — the rest of the lane re-audits next cycle from the seal");
  }

  [Test]
  public async Task ManifestReceptor_CursorWithoutOriginTopic_SkipsFollowAsync() {
    // Directed or not at all — the same rule every other origin-bound request obeys.
    var stream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
      ResumeAfterStreamId = TrackedGuid.New().Value,
    });

    await Assert.That(transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Any(r => r is not null)).IsFalse();
  }

  [Test]
  public async Task ManifestReceptor_CursorFollowBudgets_AreEvictedOnceTheyOutgrowTheirBoundAsync() {
    // The page-budget map is a per-process static keyed by (origin, window). Windows advance every
    // audit cycle and origins come and go, so every key it holds is dead within a cycle or two —
    // but nothing removes them on that schedule: a complete answer removes its own key and an
    // incomplete one does not. In a consumer auditing many origins over a long uptime the map is
    // therefore append-only, and the bound is the only thing that keeps a diagnostic counter from
    // growing for the life of the process. Eviction is observable because a budget the map has
    // forgotten starts over.
    var stream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],   // folds match — only the paging is under test
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxManifestPagesPerAudit = 1 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    int follows() => transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Count(r => r is not null);

    async Task followAsync(long since, long through) =>
      await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
        SinceSequence = since,
        ComputedThrough = through,
        ChunkCount = 1,
        ResumeAfterStreamId = TrackedGuid.New().Value,
      });

    await followAsync(100, 300);
    await Assert.That(follows()).IsEqualTo(1);

    // The budget for this window is now spent, and stays spent while the map remembers it.
    await followAsync(100, 300);
    await Assert.That(follows()).IsEqualTo(1)
      .Because("without a live budget entry for this window the rest of the test would prove "
             + "nothing — the second follow has to be refused before eviction can be seen");

    // Enough distinct windows to carry the map past its bound whatever it already held. Each one
    // is a fresh key, so each is allowed its first page.
    const int floodWindows = 300;
    for (var i = 0; i < floodWindows; i++) {
      await followAsync(1000 + i, 5000);
    }
    await Assert.That(follows()).IsEqualTo(1 + floodWindows)
      .Because("each distinct window gets its own budget; eviction resets counters, it does not "
             + "refuse pages");

    await followAsync(100, 300);
    await Assert.That(follows()).IsEqualTo(2 + floodWindows)
      .Because("the original window's spent budget was evicted with the rest — an unbounded map "
             + "would still be refusing this page, and would still be holding every dead key "
             + "behind it for the life of the process");
  }

  // ── bulk-deficit escalation: big type-level deficits skip the drip ──────

  [Test]
  public async Task ManifestReceptor_TypeDeficitAtThreshold_SendsOneStateOnlyBulkBackfillAsync() {
    // A type-level windowed roll-up showing thousands of missing events would take the per-stream
    // path days: 500-stream pages, 25 repairs per cycle. One state-only range-bounded redelivery
    // of the whole (tenant, type) window covers it in a single request — and state-only means the
    // backfilled history builds state without re-firing trigger receptors.
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 100)],   // local: 100 events
        ComputedThrough = 300,
      },
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, BulkBackfillThresholdEvents = 1000 }, tracker: tracker);
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 4500)],   // deficit 500 < 1000
        ComputedThrough = 300,
      },
    };
    var transport = new CaptureTransport();
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
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 100)],
        ComputedThrough = 300,
      },
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, BulkBackfillThresholdEvents = 1000 }, tracker: tracker, ledger: ledger);
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

  // ── coverage round 23: fail-closed paths when required infrastructure is absent ────

  // If the origin cannot resolve its own coordinator, transport, or serializer, answering a
  // request anyway would either throw mid-pipeline or fabricate an answer nobody can trust. The
  // origin must drop the request AND say why — a silent drop here would look identical to "we
  // have nothing to report," burying a broken deployment instead of surfacing it.
  [Test]
  public async Task RequestReceptor_MissingInfrastructure_LogsAndDropsTheRequestAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("origin-svc"));
    services.AddSingleton(Options.Create(new StreamIntegrityOptions()));
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger<IntegrityManifestRequestReceptor>();
    var receptor = new IntegrityManifestRequestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(logger.Entries.Any(e => e.Message.Contains("required infrastructure is missing")))
      .IsTrue().Because("a request the origin cannot answer must be visibly dropped, not silently ignored");
  }

  // A manifest that arrives after an operator explicitly disabled the deep audit must not be
  // compared anyway — resurrecting reports and repairs behind a switch the operator turned off
  // is exactly the "half-obeyed kill switch" that makes an incident worse mid-rollback.
  [Test]
  public async Task ManifestReceptor_AuditDisabled_IgnoresIncomingManifestAsync() {
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [];   // would otherwise report a deficit for the bucket
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { AuditEnabled = false, PublishReportEvents = true }, dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("AuditEnabled=false must fully disable comparison — not just new audits, incoming manifests too");
    await Assert.That(transport.Published).IsEmpty();
  }

  // A manifest a service somehow receives FROM ITSELF (topology misconfiguration, a broadcast
  // that fans back to the sender) must never be compared — a self-loop would otherwise read as a
  // total deficit against buckets that were never actually foreign, manufacturing an alarm and a
  // repair request the service would send to itself.
  [Test]
  public async Task ManifestReceptor_OwnManifestLoopedBack_IgnoresItAsync() {
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    // OriginServiceId is the CONSUMER's own LocalServiceId, not coordinator.OriginId — a looped
    // manifest. Without the self-check this compares as a total deficit (nothing local is ever
    // recorded under an origin id nobody actually tracks) and alarms/repairs against itself.
    var selfManifest = new IntegrityManifest {
      ManifestStreamId = coordinator.LocalServiceId,
      OriginServiceId = coordinator.LocalServiceId,
      OriginServiceName = "self-svc",
      Digests = [_digest(stream, 11, 21, 2)],
    };
    await receptor.HandleAsync(selfManifest);

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("a self-looped manifest has nothing foreign to compare against — it must be ignored, not alarmed on");
    await Assert.That(transport.Published).IsEmpty();
  }

  // If this engine cannot fold the manifest's exact stream-level window, comparing against a
  // differently-scoped local fold would fabricate mismatches on any stream with prior history —
  // a false-alarm storm rather than an honest "cannot verify this cycle."
  [Test]
  public async Task ManifestReceptor_WindowedStreamCompare_EngineCannotFold_SkipsInsteadOfAlarmingAsync() {
    var coordinator = new AuditCoordinator { ForChunkReturnsNull = true };
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var logger = new CapturingLogger<IntegrityManifestReceptor>();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    var manifest = _manifest(coordinator, [_digest(stream, 11, 21, 2)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("an unfoldable window must never alarm on incomparable ranges");
    await Assert.That(transport.Published).IsEmpty()
      .Because("no repair may go out for a comparison that never actually ran");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("cannot fold the manifest's window"))).IsTrue()
      .Because("a skipped audit must be visible in logs — silence here reads identical to 'no problems found'");
  }

  // A stream that simply has not settled locally yet must never read as divergence — otherwise
  // every in-flight delivery falsely alarms and can drive a needless redelivery request for a
  // stream that is actually fine and just landed moments ago.
  [Test]
  public async Task ManifestReceptor_StreamLevel_SettleSkipsFreshBucketsAsync() {
    var coordinator = new AuditCoordinator();
    var stream = TrackedGuid.New().Value;
    // ReceivedDigests / ReceivedTableDigests both default empty — nothing locally for this bucket yet.
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    // ...but the origin's bucket changed moments ago — in-flight, not divergence.
    await receptor.HandleAsync(_manifest(coordinator, [
      _digest(stream, 11, 21, 2) with { UpdatedAt = DateTimeOffset.UtcNow },
    ]));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("a bucket updated inside the settle window must never alarm — the stream-level twin of the type-level settle skip");
    await Assert.That(transport.Published).IsEmpty();
  }

  // Without a resolvable requester identity, a resume-cursor follow-up cannot be addressed at
  // all. The rest of a wide lane's window must simply wait for the next audit cycle — it must
  // not be dropped in a way indistinguishable from "the window answered completely."
  [Test]
  public async Task ManifestReceptor_ResumeCursor_MissingRequesterIdentity_CannotFollowAsync() {
    var stream = TrackedGuid.New().Value;
    var cursor = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      ReceivedDigests = [_digest(stream, 41, 42, 5)],
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IConsumerRegistry>(new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    // No IServiceInstanceProvider registered — the follow-up cannot name a requester.
    services.AddSingleton(Options.Create(new StreamIntegrityOptions()));
    services.AddSingleton(tracker);
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    var manifest = _manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
      ResumeAfterStreamId = cursor,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Any(r => r is not null)).IsFalse()
      .Because("the follow-up cannot be addressed without a requester identity and must not be silently abandoned");
  }

  // Same hazard as the stream-level windowed skip, one level up: comparing a type-level window
  // this engine cannot actually fold would alarm on ranges that were never truly compared.
  [Test]
  public async Task ManifestReceptor_TypeLevelWindowedCompare_EngineCannotFold_SkipsInsteadOfAlarmingAsync() {
    var coordinator = new AuditCoordinator();   // WindowedTypeResult stays null — "cannot window"
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var logger = new CapturingLogger<IntegrityManifestReceptor>();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    var manifest = _manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(dispatcher.Published).IsEmpty();
    await Assert.That(transport.Published).IsEmpty();
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("cannot fold the manifest's window"))).IsTrue()
      .Because("a skipped type-level audit must be visible — a silent return here would report a false all-clear");
  }

  // Without a resolvable requester identity, a type-level mismatch cannot be escalated to a
  // stream-level drill-down at all. The mismatch must stay live for the next audit cycle rather
  // than being silently dropped in a way indistinguishable from "everything matched."
  [Test]
  public async Task ManifestReceptor_TypeLevelMismatch_NoRequesterIdentity_SkipsDrillDownAsync() {
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)]   // differs
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IConsumerRegistry>(new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    // No IServiceInstanceProvider registered — the drill-down cannot name a requester.
    services.AddSingleton(Options.Create(new StreamIntegrityOptions()));
    services.AddSingleton(new IntegrityGapTracker());
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty()
      .Because("no drill-down can be addressed without a requester identity");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("type-level mismatches only ever escalate; they never report directly");
  }

  // An unpopulated type-digest table must never read as "nothing to compare" — the honest
  // fallback is the store recompute. Treating an empty table as a clean match would mean a
  // broken digest-maintenance path audits identical to a genuinely quiet type.
  [Test]
  public async Task ManifestReceptor_TypeLevel_UnpopulatedTable_FallsBackToRecomputeAsync() {
    var coordinator = new AuditCoordinator();
    // OwnTypeDigests / ReceivedTypeDigests both default empty — the table lane is unpopulated.
    var s1 = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(s1, 99, 98, 3)];   // recompute source; folds differently than origin
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 5 }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(coordinator.TypeComputeCalls).IsEqualTo(1)
      .Because("an unpopulated type-digest table must fall back to the store recompute, never a pre-verified empty match");
    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the recompute fallback still finds and escalates the mismatch — an unpopulated table must not audit as clean");
  }

  // Directed-or-not-at-all applies to the bulk path exactly as it does to the per-stream repair
  // and the drill-down: without the origin's carried request address, broadcasting anywhere else
  // risks flooding every other service on that topic. The deficit waits for the next checkpoint.
  [Test]
  public async Task ManifestReceptor_BulkBackfillDeficit_UnknownOriginTopic_WithholdsTheBackfillAsync() {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [],   // the consumer holds nothing for this type — a pure, huge deficit
        ComputedThrough = 5000,
      },
    };
    var transport = new CaptureTransport();
    var logger = new CapturingLogger<IntegrityManifestReceptor>();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { PublishReportEvents = false, BulkBackfillThresholdEvents = 1000 },
      tracker: new IntegrityGapTracker());   // no checkpoint recorded — origin's address is unlearned
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    var manifest = _manifest(coordinator, [
      _typeDigest("Contracts.BigType", 41, 42, 2500),
    ], ManifestLevel.Types) with {
      SinceSequence = 0,
      ComputedThrough = 5000,
      ChunkCount = 1,
    };
    await receptor.HandleAsync(manifest);

    await Assert.That(transport.Published).IsEmpty()
      .Because("no bulk backfill may broadcast off any topic but the origin's own carried address");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("no origin-carried request address"))).IsTrue()
      .Because("the withheld backfill must be visible in logs, not a silent no-op");
  }

  // A stream-scoped deficit that cannot be addressed (no requester identity) must not silently
  // look like "handled" — only the wire send is withheld; the deficit re-offers on the next
  // comparison once the identity is fixed.
  [Test]
  public async Task ManifestReceptor_StreamDeficit_MissingRequesterIdentity_WithholdsTheRepairAsync() {
    var coordinator = new AuditCoordinator();   // nothing local — every origin bucket diverges
    var stream = TrackedGuid.New().Value;
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IConsumerRegistry>(new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    // No IServiceInstanceProvider registered — the repair request cannot name a requester.
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairDrainEnabled = false, PublishReportEvents = false }));
    services.AddSingleton(tracker);
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(transport.Published).IsEmpty()
      .Because("the repair cannot be addressed without a requester identity — sending nowhere would look identical to a healthy audit");
  }

  // A GRANTED bulk backfill whose origin has not yet taught its request address must be withheld
  // at the send, after the ledger has already recorded the attempt. Distinct from the drill-down
  // withhold beside it: that one is a stream-page ask, this one is the whole window's redelivery,
  // and only this path can put a range-bounded RequestRedeliveryCommand on the wire.
  [Test]
  public async Task ManifestReceptor_GrantedBulkBackfill_WithNoOriginRequestAddress_WithholdsTheSendAsync() {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult {
        Digests = [_typeDigest("Contracts.TypeX", 99, 98, 100)],   // local holds 100 of 5000
        ComputedThrough = 300,
      },
    };
    var transport = new CaptureTransport();
    var logger = new CapturingLogger<IntegrityManifestReceptor>();
    // A tracker with NO checkpoint recorded: the origin's request address is unlearned, which is
    // the normal state for the first cycle after a consumer starts or an origin is redeployed.
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
        BulkBackfillThresholdEvents = 1000,
      },
      tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    await receptor.HandleAsync(
      _manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5000)], ManifestLevel.Types) with {
        SinceSequence = 100,
        ComputedThrough = 300,
        ChunkCount = 1,
      });

    await Assert.That(transport.Published.Any(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true))
      .IsFalse()
      .Because("a redelivery of a whole 4900-event window may go to the origin's own carried "
             + "address or nowhere; the only other topic on hand is the requester's own shared "
             + "one, and publishing a backfill there fans it out to every service on it");

    // The zero stream count is what makes this the BULK site and not the stream-scoped one: the
    // stream path only ever withholds a request that names at least one stream, and it is not
    // even reachable from a type-level manifest.
    await Assert.That(logger.Entries.Any(e =>
        e.Message.Contains("Repair request to 'origin-svc' withheld (Contracts.TypeX, 0 stream(s))",
          StringComparison.Ordinal)))
      .IsTrue()
      .Because("the ledger has already burned an attempt on this bucket, so a silent withhold "
             + "would leave the deficit un-backfilled and the spent attempt unexplained");
  }

  // The stream-scoped repair has no infrastructure check before it is called — its own guard is
  // the only thing between an unaddressable repair and a NullReferenceException out of a
  // background audit. Withholding must also stay silent about the address, because the address is
  // fine here; it is this service's own identity that is missing.
  [Test]
  public async Task ManifestReceptor_GrantedStreamRepair_WithNoRequesterIdentity_WithholdsTheSendAsync() {
    var coordinator = new AuditCoordinator();   // nothing local — the origin bucket diverges
    var stream = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var logger = new CapturingLogger<IntegrityManifestReceptor>();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IConsumerRegistry>(new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    // No IServiceInstanceProvider: the repair request has no requester name to carry, so the
    // origin would have nowhere to send the redelivery back to.
    services.AddSingleton(Options.Create(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainEnabled = false,
      PublishReportEvents = true,
    }));
    services.AddSingleton(tracker);
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    // Repair was GRANTED — this is what proves the send was actually attempted rather than the
    // whole repair path being skipped, which is how an unaddressable repair looks identical to a
    // healthy audit.
    var reports = dispatcher.Published.Cast<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(1)
      .Because("the divergence itself is still real and still reported — only the wire send is "
             + "withheld");
    await Assert.That(reports[0].AutoRepairRequested).IsTrue()
      .Because("the ledger granted the repair, so the batch reached the send; without that grant "
             + "this test would pass on a path that never tried to publish anything");

    await Assert.That(transport.Published).IsEmpty()
      .Because("a repair request that cannot name its requester cannot be answered — the origin "
             + "would have no return address, so sending it is worse than withholding it");
    await Assert.That(logger.Entries.Any(e =>
        e.Message.Contains("no origin-carried request address", StringComparison.Ordinal)))
      .IsFalse()
      .Because("the origin's address was learned; blaming it would send an operator chasing the "
             + "origin's checkpoints instead of this service's own missing identity");
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
      AuditCoordinator coordinator, List<StreamDigest> digests,
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

  /// <summary>A consumer registry that consumes the named types only.</summary>
  private sealed class ConsumesOnly(params string[] typeNames) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => typeNames.Contains(messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() =>
      [.. typeNames.Select(t => new ConsumedMessageInfo(t, IsEvent: true))];
  }

  private static ServiceProvider _provider(
      AuditCoordinator coordinator, CaptureTransport transport,
      StreamIntegrityOptions? options = null, CaptureDispatcher? dispatcher = null,
      IntegrityGapTracker? tracker = null, IIntegrityRepairLedger? ledger = null,
      Whizbang.Core.Routing.RoutingOptions? routing = null, IConsumerRegistry? consumers = null,
      StreamIntegrityMetrics? metrics = null, bool withInstanceProvider = true) {
    var services = new ServiceCollection();
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    if (routing is not null) {
      services.AddSingleton(Options.Create(routing));
    }
    services.AddSingleton<IWorkCoordinator>(coordinator);
    // What this service consumes. These tests compare whatever types they name, so by default everything is consumed;
    // the #2058 tests pass a registry that consumes a subset.
    services.AddSingleton<IConsumerRegistry>(consumers ?? new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher ?? new CaptureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    if (withInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("auditor-svc"));
    }
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

  private sealed class AuditCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;
    public Guid OriginId { get; } = TrackedGuid.New().Value;
    public List<(Guid Origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, long From, long Until)> StampedWindows { get; } = [];

    public Task IntegrityStampRepairWindowsAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
        long windowFrom, long windowUntil, CancellationToken cancellationToken = default) {
      StampedWindows.Add((originServiceId, keys, windowFrom, windowUntil));
      return Task.CompletedTask;
    }
    public IReadOnlyList<StreamDigest> OwnDigests { get; set; } = [];
    public IReadOnlyList<IntegrityDomainClaim> Claims { get; set; } = [];

    public Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Claims);
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
    public int? WindowedMaxSeen;
    public long? SealAdvancedTo;
    public Guid? SealAdvancedOrigin;
    public long Seal { get; set; }
    public long? WindowedUntilSeen;
    public IReadOnlyList<string>? WindowedTypesSeen;

    public Task<long> GetIntegritySealAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
      Task.FromResult(Seal);
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
      WindowedTypesSeen = eventTypes;
      return Task.FromResult(WindowedTypeResult);
    }

    public Task<WindowedDigestResult?> ComputeStreamDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, Guid? resumeAfterStreamId, int maxDigests,
      TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      WindowedSinceSeen = sinceSequence;
      WindowedUntilSeen = untilSequence;
      WindowedMaxSeen = maxDigests;
      return Task.FromResult(WindowedStreamResult);
    }

    public IReadOnlyList<Guid>? ForChunkStreamsSeen;
    public long? ForChunkSinceSeen;
    public long? ForChunkUntilSeen;
    public int ForChunkCalls;
    public TaskCompletionSource? BlockForChunk { get; set; }
    public TaskCompletionSource ForChunkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When true, simulates an engine that cannot fold the manifest's window — the
    /// honest "I cannot answer this" signal, distinct from an empty-but-answerable fold.</summary>
    public bool ForChunkReturnsNull { get; set; }

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
      if (ForChunkReturnsNull) {
        return null;
      }
      return [.. ReceivedDigests.Where(d => streamIds.Contains(d.StreamId))];
    }

    /// <summary>#2103: what the comparison recorded as each deficit's one missing event.</summary>
    public List<(IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, IReadOnlyList<Guid?> Ids)> MissingRecorded { get; } = [];

    public Task IntegrityRecordMissingEventsAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, IReadOnlyList<Guid?> missingEventIds,
        CancellationToken cancellationToken = default) {
      MissingRecorded.Add((keys, missingEventIds));
      return Task.CompletedTask;
    }

    /// <summary>#2102: the folds of events held from the origin without an origin sequence.</summary>
    public IReadOnlyList<StreamDigest> UnsequencedDigests { get; set; } = [];
    public List<(IReadOnlyList<string> Types, IReadOnlyList<Guid>? Streams)> UnsequencedAsks { get; } = [];

    public Task<IReadOnlyList<StreamDigest>> ComputeUnsequencedReceivedDigestsAsync(
        Guid originServiceId, IReadOnlyList<string> eventTypes, IReadOnlyList<Guid>? streamIds, TimeSpan settleWindow,
        CancellationToken cancellationToken = default) {
      UnsequencedAsks.Add((eventTypes, streamIds));
      return Task.FromResult<IReadOnlyList<StreamDigest>>([.. UnsequencedDigests.Where(d =>
        eventTypes.Contains(d.EventType) && (streamIds is null ? d.StreamId == Guid.Empty : streamIds.Contains(d.StreamId)))]);
    }

    public Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnTableDigests : ReceivedTableDigests);

    public int TypeTableReads;
    public IReadOnlyList<string> OwnAuditedTypes { get; set; } = [];
    public IReadOnlyList<string>? LastOwnTypeQuery { get; private set; }

    public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(OwnAuditedTypes);

    public Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
        Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref TypeTableReads);
      if (originServiceId is null) {
        LastOwnTypeQuery = eventTypes;
      }
      return Task.FromResult(originServiceId is null ? OwnTypeDigests : ReceivedTypeDigests);
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
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
  public async Task ManifestReceptor_ReportOnly_StillPublishesReportsAsync() {
    // ReportOnly is the operator's explicit report-and-decide opt-down. Bounding reports by
    // repairs must not silence the mode whose entire purpose is reporting without repairing.
    var coordinator = new AuditCoordinator {
      ReceivedDigests = []
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
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
      .Select(i => _digest(TrackedGuid.New().Value, i, i + 1, i))
      .ToList();

    await receptor.HandleAsync(_manifest(coordinator, digests));

    await Assert.That(dispatcher.Published.OfType<IntegrityDivergenceDetected>().Any()).IsTrue()
      .Because("an operator who chose report-and-decide must still get reports");
  }

  /// <summary>Minimal capturing logger — records level/message for assertions on log output.</summary>
  private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T> {
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception)));
  }

  private sealed class NullScope : IDisposable {
    public static readonly NullScope Instance = new();
    public void Dispose() { }
  }

  // ── #2008: the local lane follows ownership; #2032: compare only what the origin owns ──

  [Test]
  public async Task RequestReceptor_OwnedDomainsRegistered_AdvertisesOnlyOwnedTypesAsync() {
    var coordinator = new AuditCoordinator {
      OwnTypeDigests = [
        _typeDigest("App.Contracts.Orders.Placed, App.Contracts", 1, 2, 3),
        _typeDigest("App.Contracts.Billing.Raised, App.Contracts", 4, 5, 6),
      ],
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, routing: new Whizbang.Core.Routing.RoutingOptions().OwnDomains("App.Contracts.Orders"));
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox", Level = ManifestLevel.Types });

    var manifest = _deserializeManifest(transport.Published.Single().Envelope);
    await Assert.That(manifest.Digests.Select(d => d.EventType)).IsEquivalentTo(["App.Contracts.Orders.Placed, App.Contracts"])
      .Because("events of a type this service does not own were received, not authored; advertising them as its own "
             + "makes consumers count them as surplus and ask this service to redeliver them (#2008)");
  }

  [Test]
  public async Task RequestReceptor_OnlyForeignTypesHeld_StaysSilentAsync() {
    var coordinator = new AuditCoordinator {
      OwnTypeDigests = [_typeDigest("App.Contracts.Billing.Raised, App.Contracts", 4, 5, 6)],
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, routing: new Whizbang.Core.Routing.RoutingOptions().OwnDomains("App.Contracts.Orders"));
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox", Level = ManifestLevel.Types });

    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task ManifestReceptor_DigestsOfATypeAnotherServiceOwns_AreNotComparedAsync() {
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [],
      Claims = [new IntegrityDomainClaim {
        Domain = "app.contracts.billing",
        OwnerServiceId = TrackedGuid.New().Value,
        OwnerServiceName = "billing-svc",
      }],
    };
    var transport = new CaptureTransport();
    var dispatcher = new CaptureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator,
      [_typeDigest("App.Contracts.Billing.Raised, App.Contracts", 4, 5, 6)], ManifestLevel.Types));

    await Assert.That(coordinator.TypeTableReads + coordinator.TypeComputeCalls).IsEqualTo(0)
      .Because("an older origin still advertising another service's type is not authoritative for it, so nothing is "
             + "folded or compared against it (#2032)");
    await Assert.That(dispatcher.Published).IsEmpty();
    await Assert.That(transport.Published).IsEmpty();

    coordinator.Claims = [];
    await receptor.HandleAsync(_manifest(coordinator,
      [_typeDigest("App.Contracts.Billing.Raised, App.Contracts", 4, 5, 6)], ManifestLevel.Types));
    await Assert.That(coordinator.TypeTableReads + coordinator.TypeComputeCalls).IsGreaterThan(0)
      .Because("the control: with no known owner the same digest is compared as before");
  }

  [Test]
  public async Task ManifestReceptor_MixedDigests_ComparesOnlyThoseNoOtherServiceOwnsAsync() {
    var coordinator = new AuditCoordinator {
      ReceivedTypeDigests = [],
      Claims = [new IntegrityDomainClaim {
        Domain = "app.contracts.billing",
        OwnerServiceId = TrackedGuid.New().Value,
        OwnerServiceName = "billing-svc",
      }],
    };
    var sp = _provider(coordinator, new CaptureTransport());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [
      _typeDigest("App.Contracts.Billing.Raised, App.Contracts", 4, 5, 6),
      _typeDigest("App.Contracts.Orders.Placed, App.Contracts", 7, 8, 9),
    ], ManifestLevel.Types));

    await Assert.That(coordinator.TypeTableReads + coordinator.TypeComputeCalls).IsGreaterThan(0)
      .Because("the digest no other service owns is still compared");
  }

  // ── #2102: events held without the origin's commit sequence ─────────────

  /// <summary>Records what the compare told the ledger: the reports with their local digests, and the forgotten keys.</summary>
  private sealed class ObservingLedger : IIntegrityRepairLedger {
    public List<(IntegrityRepairLedger.DivergenceKey Key, long LocalLo, long LocalHi)> Reports { get; } = [];
    public List<IntegrityRepairLedger.DivergenceKey> Forgotten { get; } = [];

    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi,
        long localLo, long localHi, DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
      Reports.Add((key, localLo, localHi));
      return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
        TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) {
      Forgotten.Add(key);
      return ValueTask.CompletedTask;
    }
  }

  private static (AuditCoordinator Coordinator, CaptureTransport Transport, ObservingLedger Ledger, IntegrityManifestReceptor Receptor)
      _heldFixture(StreamIntegrityOptions? options = null, StreamIntegrityMetrics? metrics = null) {
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var ledger = new ObservingLedger();
    var sp = _provider(coordinator, transport,
      options ?? new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      tracker: tracker, ledger: ledger, metrics: metrics);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    return (coordinator, transport, ledger,
      new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance));
  }

  private static readonly StreamIntegrityOptions _bulkOptions = new() {
    RepairDrainEnabled = false,
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    BulkBackfillThresholdEvents = 1000,
  };

  private static IntegrityManifest _windowed(IntegrityManifest manifest) =>
    manifest with { SinceSequence = 100, ComputedThrough = 300, ChunkCount = 1 };

  private static IEnumerable<RequestRedeliveryCommand> _redeliveries(CaptureTransport transport) =>
    transport.Published
      .Where(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true)
      .Select(p => _deserializeRedelivery(p.Envelope));

  [Test]
  public async Task WindowedStream_EventsHeldWithoutAnOriginSequence_AreNoDeficit_AndTheFalseRowClearsAsync() {
    // The observed case: the origin's window holds 7 events of the stream, the consumer holds all 7 with the origin
    // recorded and no origin sequence. The windowed fold counted none of them and recorded a deficit every audit.
    var (coordinator, transport, ledger, receptor) = _heldFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [];
    coordinator.UnsequencedDigests = [_digest(stream, 7, 8, 7)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 7, 8, 7)])));

    await Assert.That(ledger.Reports).IsEmpty()
      .Because("a consumer never reports a deficit for an event it holds, whether or not it knows its origin sequence");
    await Assert.That(_redeliveries(transport)).IsEmpty().Because("with nothing missing, nothing is redelivered");
    await Assert.That(ledger.Forgotten).Contains(
      new IntegrityRepairLedger.DivergenceKey(coordinator.OriginId, "tenant-a", "Contracts.TypeX", stream))
      .Because("the bucket matches, so a false deficit recorded before this release clears on the next comparison");
    var (types, streams) = coordinator.UnsequencedAsks.Single();
    await Assert.That(streams).IsEquivalentTo([stream]).Because("bounded by the chunk's short streams");
    await Assert.That(types).IsEquivalentTo(["Contracts.TypeX"]);
  }

  [Test]
  public async Task WindowedStream_AMissingEventBeyondTheHeldOnes_IsStillADeficit_WithTheHeldDigestAsync() {
    var (coordinator, transport, ledger, receptor) = _heldFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];
    coordinator.UnsequencedDigests = [_digest(stream, 0x40, 0x80, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x7, 0x7, 3)])));

    var (_, localLo, localHi) = ledger.Reports.Single();
    await Assert.That(localLo).IsEqualTo(0x41L)
      .Because("the recorded local digest keeps the events held without a sequence");
    await Assert.That(localHi).IsEqualTo(0x81L);
    await Assert.That(_redeliveries(transport).Single().StreamIds).IsEquivalentTo([stream])
      .Because("three events and at most two held: a genuinely missing event is still a deficit and is repaired");
  }

  [Test]
  public async Task WindowedStream_UndecidedBucket_IsNeitherReportedNorRepaired_AndItsRowIsDroppedAsync() {
    var (coordinator, transport, ledger, receptor) = _heldFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];
    coordinator.UnsequencedDigests = [_digest(stream, 0x4, 0x4, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x3, 0x3, 2)])));

    await Assert.That(ledger.Reports).IsEmpty();
    await Assert.That(_redeliveries(transport)).IsEmpty();
    await Assert.That(ledger.Forgotten).Contains(
      new IntegrityRepairLedger.DivergenceKey(coordinator.OriginId, "tenant-a", "Contracts.TypeX", stream))
      .Because("a deficit the window cannot confirm is not kept as one; the full-history compare still sees the stream");
  }

  [Test]
  public async Task WindowedStream_MatchingBuckets_NeverAskForHeldEventsAsync() {
    var (coordinator, _, _, receptor) = _heldFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 5, 6, 2)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 5, 6, 2)])));

    await Assert.That(coordinator.UnsequencedAsks).IsEmpty()
      .Because("the extra read is paid only by buckets that disagree");
  }

  [Test]
  public async Task UnwindowedStream_NeverAsksForHeldEventsAsync() {
    // A full-history fold already includes the events without a sequence.
    var (coordinator, transport, _, receptor) = _heldFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.UnsequencedDigests = [_digest(stream, 7, 8, 7)];

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 7, 8, 7)]));

    await Assert.That(coordinator.UnsequencedAsks).IsEmpty();
    await Assert.That(_redeliveries(transport).Count()).IsEqualTo(1)
      .Because("the unwindowed compare is unchanged: its local fold is all history");
  }

  [Test]
  public async Task WindowedType_HeldEventsCompletingTheWindow_AdvanceTheSealAsync() {
    var (coordinator, transport, _, receptor) = _heldFixture();
    coordinator.Seal = 100;   // #2107: the audit asked from the seal; a window above it is never sealed
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 0x1, 0x1, 1)],
      ComputedThrough = 300,
    };
    coordinator.UnsequencedDigests = [_typeDigest("Contracts.TypeX", 0x6, 0x6, 2)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 0x7, 0x7, 3)], ManifestLevel.Types)));

    await Assert.That(coordinator.SealAdvancedTo).IsEqualTo(300L)
      .Because("every event of the window is held, so the window is verified");
    await Assert.That(transport.Published).IsEmpty().Because("nothing to drill into");
    await Assert.That(coordinator.UnsequencedAsks.Single().Streams).IsNull()
      .Because("the type-level compare folds the held events per (tenant, type)");
  }

  [Test]
  public async Task WindowedType_ADeficitTheHeldEventsMayFill_IsNotBulkBackfilledAsync() {
    var (coordinator, transport, ledger, receptor) = _heldFixture(_bulkOptions);
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 0x1, 0x1, 100)],
      ComputedThrough = 300,
    };
    coordinator.UnsequencedDigests = [_typeDigest("Contracts.TypeX", 0x2, 0x2, 4900)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 0x7, 0x7, 5000)], ManifestLevel.Types)));

    await Assert.That(_redeliveries(transport)).IsEmpty()
      .Because("4,900 of the 4,900 events the window lacks may be held without a sequence; that is no bulk deficit");
    await Assert.That(ledger.Reports).IsEmpty();
    await Assert.That(transport.Published.Any(p => p.EnvelopeType?.Contains("RequestIntegrityManifest") == true)).IsTrue()
      .Because("the type still disagrees, so its streams are compared, where the held events are placed per stream");
    await Assert.That(coordinator.SealAdvancedTo).IsNull();
  }

  [Test]
  public async Task WindowedType_ADeficitBeyondTheHeldEvents_IsBulkBackfilledByWhatIsMissingAsync() {
    var (coordinator, transport, _, receptor) = _heldFixture(_bulkOptions);
    coordinator.WindowedTypeResult = new WindowedDigestResult {
      Digests = [_typeDigest("Contracts.TypeX", 0x1, 0x1, 100)],
      ComputedThrough = 300,
    };
    coordinator.UnsequencedDigests = [_typeDigest("Contracts.TypeX", 0x2, 0x2, 3500)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 0x7, 0x7, 5000)], ManifestLevel.Types)));

    await Assert.That(_redeliveries(transport).Single().StateOnly).IsTrue()
      .Because("at least 1,400 events are missing even if every held one belongs here");
  }

  [Test]
  public async Task WindowedStream_UndecidedBuckets_AreCountedOnTheUnplacedCounterAsync() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    long unplaced = 0;
    using var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        // This metrics instance only: another test's instance publishes an instrument of the same name.
        if (ReferenceEquals(instrument, metrics.UnplacedBuckets.Instrument)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref unplaced, value));
    listener.Start();
    var (coordinator, _, _, receptor) = _heldFixture(metrics: metrics);
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];
    coordinator.UnsequencedDigests = [_digest(stream, 0x4, 0x4, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x3, 0x3, 2)])));
    listener.RecordObservableInstruments();

    await Assert.That(Interlocked.Read(ref unplaced)).IsEqualTo(1L)
      .Because("an undecided bucket is not a deficit, but it is counted, so the operator sees how much the windows cannot place");
  }

  // ── #2115: an idle re-check's answer clears what now matches and never repairs ───────────

  private static (AuditCoordinator Coordinator, CaptureTransport Transport, IntegrityRepairLedger Ledger, IntegrityManifestReceptor Receptor, Func<long> Cleared)
      _recheckFixture() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
    var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, metrics.LedgerRowsRecheckCleared.Instrument)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    long observed = 0;
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed += value);
    listener.Start();
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var ledger = new IntegrityRepairLedger();
    // Burst mode: a deficit in an ordinary answer would go straight out as a repair request.
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairDrainEnabled = false, RepairMode = IntegrityRepairMode.AutoRepairCapped },
      tracker: tracker, ledger: ledger, metrics: metrics);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    return (coordinator, transport, ledger, receptor, Cleared);

    long Cleared() {
      observed = 0;
      listener.RecordObservableInstruments();
      return observed;
    }
  }

  private static async Task _reportStaleAsync(IntegrityRepairLedger ledger, Guid origin, Guid stream) =>
    _ = await ledger.TryBeginReportBatchAsync(
      [new IntegrityReportObservation(new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.TypeX", stream), 7, 7, 0, 0, IsDeficit: true)],
      _fixedNow.AddDays(-5), TimeSpan.FromMinutes(60));

  [Test]
  public async Task ManifestReceptor_ARecheckAnswer_RemovesTheRowWhoseBucketNowMatches_AndCountsItAsync() {
    // A row written under an older compare rule: the bucket matches under today's rule, so the re-check clears it
    // without waiting for the daily audit to compare it again.
    var (coordinator, _, ledger, receptor, cleared) = _recheckFixture();
    var stream = TrackedGuid.New().Value;
    await _reportStaleAsync(ledger, coordinator.OriginId, stream);
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x1, 0x1, 1)])) with { Recheck = true });

    await Assert.That(ledger.Count).IsEqualTo(0).Because("the bucket matches, so its row is removed exactly as the audit removes it");
    await Assert.That(cleared()).IsEqualTo(1L).Because("the re-check counts the rows it cleared");
  }

  [Test]
  public async Task ManifestReceptor_ARecheckAnswer_RemovesAnUndecidedRow_AndCountsItAsync() {
    var (coordinator, _, ledger, receptor, cleared) = _recheckFixture();
    var stream = TrackedGuid.New().Value;
    await _reportStaleAsync(ledger, coordinator.OriginId, stream);
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];
    coordinator.UnsequencedDigests = [_digest(stream, 0x4, 0x4, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x3, 0x3, 2)])) with { Recheck = true });

    await Assert.That(ledger.Count).IsEqualTo(0).Because("an undecided bucket is forgotten, exactly as the audit forgets it");
    await Assert.That(cleared()).IsEqualTo(1L);
  }

  [Test]
  public async Task ManifestReceptor_ARecheckAnswer_WithADeficit_NeverAsksForARepairAsync() {
    var (coordinator, transport, ledger, receptor, cleared) = _recheckFixture();
    var stream = TrackedGuid.New().Value;
    await _reportStaleAsync(ledger, coordinator.OriginId, stream);

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x3, 0x3, 2)])) with { Recheck = true });

    await Assert.That(_redeliveries(transport)).IsEmpty()
      .Because("a re-check is a compare; even with burst repair on, it never sends a repair request");
    await Assert.That(ledger.Count).IsEqualTo(1).Because("the bucket is still divergent, so its row stays and is reported again");
    await Assert.That(cleared()).IsEqualTo(0L);
  }

  [Test]
  public async Task ManifestReceptor_ARecheckAnswer_WithoutMetrics_StillComparesAsync() {
    var (coordinator, transport, ledger, receptor) = _heldFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x1, 0x1, 1)])) with { Recheck = true });

    await Assert.That(ledger.Forgotten).Contains(new IntegrityRepairLedger.DivergenceKey(coordinator.OriginId, "tenant-a", "Contracts.TypeX", stream))
      .Because("counting is optional; the matching bucket is still removed");
    await Assert.That(_redeliveries(transport)).IsEmpty();
  }

  [Test]
  public async Task ManifestReceptor_AnOrdinaryAnswer_DoesNotCountAsARecheckAsync() {
    var (coordinator, _, ledger, receptor, cleared) = _recheckFixture();
    var stream = TrackedGuid.New().Value;
    await _reportStaleAsync(ledger, coordinator.OriginId, stream);
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x1, 0x1, 1)])));

    await Assert.That(ledger.Count).IsEqualTo(0).Because("the audit heals it as always");
    await Assert.That(cleared()).IsEqualTo(0L).Because("only a re-check's answer counts on the re-check counter");
  }

  [Test]
  public async Task ManifestReceptor_ARecheckPage_FollowsItsCursorAsARecheckAsync() {
    var (coordinator, transport, _, receptor, _) = _recheckFixture();
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 0x1, 0x1, 1)];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_digest(stream, 0x1, 0x1, 1)]))
      with { Recheck = true, ResumeAfterStreamId = stream });

    var follow = transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Single(r => r is not null)!;
    await Assert.That(follow.Recheck).IsTrue()
      .Because("the rest of a re-check's window is a re-check too, so its pages never ask for a repair either");
  }

  [Test]
  public async Task RequestReceptor_EchoesTheRecheckFlag_OnItsAnswerAsync() {
    var (_, transport) = await _answerAsync(
      _windowedAsk(since: 400, until: 900, ManifestLevel.Streams) with { Recheck = true }, computedThrough: 900);

    await Assert.That(_deserializeManifest(transport.Published.Single().Envelope).Recheck).IsTrue()
      .Because("the asker compares the answer as a re-check only when the answer says it is one");
  }

  // ── #2103: one missing event is named by the id lanes ───────────────────

  private static StreamDigest _named(Guid stream, int count, Guid[] ids, string type = "Contracts.TypeX") {
    long lo = 0, hi = 0;
    foreach (var id in ids) {
      var bytes = id.ToByteArray(bigEndian: true);
      lo ^= System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
      hi ^= System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
    }
    return new StreamDigest {
      TenantScope = "tenant-a",
      EventType = type,
      StreamId = stream,
      DigestLo = lo ^ 0x55,
      DigestHi = hi ^ 0x66,
      EventCount = count,
      IdLo = lo,
      IdHi = hi,
    };
  }

  private static StreamIntegrityOptions _burst => new() {
    RepairDrainEnabled = false,
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    MaxAutoRepairRequestsPerAudit = 5000,
  };

  [Test]
  public async Task Repair_AStreamMissingOneEvent_AsksForThatEventOnlyAsync() {
    var (coordinator, transport, _, receptor) = _heldFixture(_burst);
    var stream = TrackedGuid.New().Value;
    var (a, b) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    coordinator.ReceivedDigests = [_named(stream, 1, ids: [a])];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_named(stream, 2, ids: [a, b])])));

    var request = _redeliveries(transport).Single();
    await Assert.That(request.EventIds).IsEquivalentTo([b])
      .Because("origin XOR local over the id lanes is the one event the stream is missing");
    await Assert.That(request.StreamIds).IsEquivalentTo([stream])
      .Because("the stream and window stay on the request for an owner that cannot name events");
    await Assert.That(request.FromCommitSequence).IsEqualTo(99L);
  }

  [Test]
  public async Task Repair_AStreamMissingNothing_AsksForNothingAsync() {
    var (coordinator, transport, _, receptor) = _heldFixture(_burst);
    var stream = TrackedGuid.New().Value;
    var a = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_named(stream, 1, ids: [a])];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_named(stream, 1, ids: [a])])));

    await Assert.That(_redeliveries(transport)).IsEmpty();
  }

  [Test]
  public async Task Repair_WithoutIdLanes_OrMoreThanOneMissing_AsksForTheWholeStreamAsync() {
    var (coordinator, transport, _, receptor) = _heldFixture(_burst);
    var twoMissing = TrackedGuid.New().Value;
    var oldOrigin = TrackedGuid.New().Value;
    var (a, b, c) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);
    coordinator.ReceivedDigests = [_named(twoMissing, 1, ids: [a]), _named(oldOrigin, 1, ids: [a])];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [
      _named(twoMissing, 3, ids: [a, b, c]),
      _named(oldOrigin, 2, ids: [a, b]) with { IdLo = null, IdHi = null },
    ])));

    var request = _redeliveries(transport).Single();
    await Assert.That(request.EventIds).IsNull().Because("no single event can be named, so both streams sync whole");
    await Assert.That(request.StreamIds).IsEquivalentTo([twoMissing, oldOrigin]);
  }

  [Test]
  public async Task Repair_NamedAndWholeStreams_GoInSeparateRequests_AndManyNamedEventsArePagedAsync() {
    var (coordinator, transport, _, receptor) = _heldFixture(_burst);
    var named = Enumerable.Range(0, 1200).Select(_ => (Stream: TrackedGuid.New().Value, Held: TrackedGuid.New().Value, Missing: TrackedGuid.New().Value)).ToList();
    var whole = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [.. named.Select(n => _named(n.Stream, 1, ids: [n.Held]))];

    await receptor.HandleAsync(_windowed(_manifest(coordinator,
      [.. named.Select(n => _named(n.Stream, 2, ids: [n.Held, n.Missing])), _named(whole, 3, ids: [TrackedGuid.New().Value])])));

    var requests = _redeliveries(transport).ToList();
    var byId = requests.Where(r => r.EventIds is not null).ToList();
    await Assert.That(byId.Select(r => r.EventIds!.Count).Order().ToList()).IsEquivalentTo([200, 500, 500])
      .Because("a large missing set is paged, at most 500 named events per request");
    await Assert.That(byId.All(r => r.StreamIds!.Count == r.EventIds!.Count)).IsTrue();
    await Assert.That(byId.SelectMany(r => r.EventIds!).Order().ToList()).IsEquivalentTo(named.Select(n => n.Missing).Order().ToList());
    await Assert.That(requests.Single(r => r.EventIds is null).StreamIds).IsEquivalentTo([whole])
      .Because("an id list narrows a request to those ids, so a stream that names none asks on its own");
  }

  [Test]
  public async Task Repair_AStreamOfThreeTypes_ComparesOnlyTheTypeThisServiceKeepsAsync() {
    // Buckets are per (origin, tenant, type, stream): a consumer keeps a subset of a stream's types, so a whole-stream
    // digest would mismatch and heal forever.
    var coordinator = new AuditCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport, _burst, tracker: tracker, consumers: new ConsumesOnly("Contracts.A"));
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    var stream = TrackedGuid.New().Value;
    var (heldA, missingA) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    coordinator.ReceivedDigests = [_named(stream, 1, [heldA], "Contracts.A")];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [
      _named(stream, 2, [heldA, missingA], "Contracts.A"),
      _named(stream, 4, [TrackedGuid.New().Value], "Contracts.B"),
      _named(stream, 6, [TrackedGuid.New().Value], "Contracts.C"),
    ])));

    var request = _redeliveries(transport).Single();
    await Assert.That(request.EventTypes).IsEquivalentTo(["Contracts.A"]);
    await Assert.That(request.EventIds).IsEquivalentTo([missingA])
      .Because("only the kept type's bucket is compared, and its one missing event named; B and C are never asked for");
  }

  [Test]
  public async Task DrainMode_RecordsEachDeficitsMissingEvent_OrNullAsync() {
    var (coordinator, _, _, receptor) = _heldFixture(new StreamIntegrityOptions {
      RepairDrainEnabled = true,
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
    });
    var one = TrackedGuid.New().Value;
    var two = TrackedGuid.New().Value;
    var (a, b, c) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);
    coordinator.ReceivedDigests = [_named(one, 1, ids: [a]), _named(two, 1, ids: [a])];

    await receptor.HandleAsync(_windowed(_manifest(coordinator, [_named(one, 2, ids: [a, b]), _named(two, 3, ids: [a, b, c])])));

    var (keys, ids) = coordinator.MissingRecorded.Single();
    var byStream = keys.Zip(ids).ToDictionary(k => k.First.StreamId, k => k.Second);
    await Assert.That(byStream[one]).IsEqualTo(b).Because("the drain later asks the owner for exactly this event");
    await Assert.That(byStream[two]).IsNull().Because("two missing events name none; a stale id is cleared");
  }
}
