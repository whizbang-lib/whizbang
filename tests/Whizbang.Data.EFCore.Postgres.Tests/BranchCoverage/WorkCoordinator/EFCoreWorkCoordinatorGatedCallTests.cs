// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.WorkCoordinator;

/// <summary>
/// Every coordinator method that wraps its database work in the process-wide
/// <see cref="WorkCoordinatorGate"/> takes a slot before it touches the pool and gives it back
/// when it is done, whatever it returns.
/// </summary>
/// <remarks>
/// <para>
/// The gate is the defense against a runaway connection-pool draw: each gated method opens with
/// <c>_gate is null ? default : await _gate.AcquireAsync(...)</c>. Every other test constructs the
/// coordinator without a gate, so the acquiring arm of that conditional ran nowhere, and a method
/// that stopped acquiring (or acquired and never released, which starves the whole process) would
/// have gone unnoticed.
/// </para>
/// <para>
/// The proof is observable rather than assumed: the gate records the slot's hold duration, tagged
/// with the acquiring method's name, only when the slot is released. A recorded measurement under
/// the expected caller therefore proves the slot was both taken by that method and handed back,
/// and an empty holder list afterwards proves nothing leaked. Private helpers acquire under their
/// own names (<c>_integrityLedgerBoolAsync</c>, <c>_withCoordinatorCommandAsync</c>, ...) because
/// the gate captures <c>[CallerMemberName]</c> at the acquire site.
/// </para>
/// <para>
/// Each case runs against its own database (<see cref="EFCoreTestBase"/> provisions one per test).
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class EFCoreWorkCoordinatorGatedCallTests : EFCoreTestBase {
  private const string HOLD_INSTRUMENT = "whizbang.gate.hold_duration_ms";
  private const string PERSPECTIVE = "Gate.Perspective";
  private const string EVENT_TYPE = "Gate.Event, Gate.Assembly";

  private static readonly Dictionary<string, (string Caller, Func<EFCoreWorkCoordinator<WorkCoordinationDbContext>, NpgsqlConnection, Task> Invoke)> _calls = _buildCalls();

  /// <summary>The case keys, one test per gated call.</summary>
  public static IEnumerable<string> GatedCalls() => _calls.Keys.OrderBy(k => k, StringComparer.Ordinal);

  [Test]
  [MethodDataSource(nameof(GatedCalls))]
  public async Task GatedCall_TakesAGateSlotAndReleasesItAsync(string call) {
    var (expectedCaller, invoke) = _calls[call];
    using var probe = new GateProbe();
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), logger: null, metrics: probe.Metrics, gate: probe.Gate);

    await invoke(coordinator, connection);

    await Assert.That(probe.Callers).Contains(expectedCaller)
      .Because("the gate records a hold only when the slot is released, so a measurement under this "
             + "caller proves the method acquired a slot before touching the pool and gave it back");
    await Assert.That(probe.Gate.SnapshotHolders()).IsEmpty()
      .Because("a slot still held after the call returns is a leak that starves every later caller");
  }

  private static Dictionary<string, (string, Func<EFCoreWorkCoordinator<WorkCoordinationDbContext>, NpgsqlConnection, Task>)> _buildCalls() {
    var calls = new Dictionary<string, (string, Func<EFCoreWorkCoordinator<WorkCoordinationDbContext>, NpgsqlConnection, Task>)>(StringComparer.Ordinal);
    void register(string key, string caller, Func<EFCoreWorkCoordinator<WorkCoordinationDbContext>, NpgsqlConnection, Task> invoke) =>
      calls.Add(key, (caller, invoke));

    // ── Instance lifecycle ──────────────────────────────────────────────────
    register("RecordHeartbeat", "RecordHeartbeatAsync", async (c, _) => {
      var accepted = await c.RecordHeartbeatAsync(new HeartbeatRequest(_newId(), "gate-svc", "gate-host", 1));
      await Assert.That(accepted).IsTrue();
    });
    register("NotifyScheduledRetryDue", "NotifyScheduledRetryDueAsync", async (c, _) =>
      await Assert.That(await c.NotifyScheduledRetryDueAsync()).IsEqualTo(0));
    register("RecordInstanceState", "RecordInstanceStateAsync", async (c, _) =>
      await Assert.That(await c.RecordInstanceStateAsync(_newId(), "Running")).IsFalse());
    register("GetStandbyRequest", "GetStandbyRequestAsync", async (c, _) =>
      await Assert.That(await c.GetStandbyRequestAsync()).IsNull());
    register("ClearStandbyRequest", "_scalarFunctionAsync", (c, _) => c.ClearStandbyRequestAsync(_newId()));

    // ── Type definitions and classification ────────────────────────────────
    register("ReclassifyEventsEphemeral", "ReclassifyEventsEphemeralAsync", (c, _) => c.ReclassifyEventsEphemeralAsync([EVENT_TYPE]));
    register("CountSourcedEventsForTypes", "CountSourcedEventsForTypesAsync", async (c, _) =>
      await Assert.That(await c.CountSourcedEventsForTypesAsync([EVENT_TYPE])).IsEqualTo(0L));
    register("GetTypeDefinitions", "GetTypeDefinitionsAsync", (c, _) => c.GetTypeDefinitionsAsync());
    register("RegisterTypeDefinition", "RegisterTypeDefinitionAsync", async (c, _) => {
      var registration = await c.RegisterTypeDefinitionAsync(EVENT_TYPE, "00", "00", 1);
      await Assert.That(registration.IsNew).IsTrue();
    });
    register("RecordDefinitionLineage", "RecordDefinitionLineageAsync", async (c, _) => {
      var from = await c.RegisterTypeDefinitionAsync(EVENT_TYPE, "00", "00", 1);
      var to = await c.RegisterTypeDefinitionAsync(EVENT_TYPE, "01", "01", 2);
      await c.RecordDefinitionLineageAsync(from.DefinitionId, to.DefinitionId, DefinitionRelationship.SchemaUpgradedTo, "gate-migration");
    });
    register("GetStateBasedStreamIds", "GetStateBasedStreamIdsAsync", (c, _) => c.GetStateBasedStreamIdsAsync([_newId()]));
    register("SyncEphemeralTypeGrace", "SyncEphemeralTypeGraceAsync", (c, _) =>
      c.SyncEphemeralTypeGraceAsync([new EphemeralTypeGrace(EVENT_TYPE, 60)]));
    register("SyncPerspectiveRetention", "SyncPerspectiveRetentionAsync", (c, _) =>
      c.SyncPerspectiveRetentionAsync([new PerspectiveRetentionDeclaration("Gate.Model", Enrolled: true, 60, null)]));
    register("GetEphemeralPairsNeedingSnapshot", "GetEphemeralPairsNeedingSnapshotAsync", (c, _) => c.GetEphemeralPairsNeedingSnapshotAsync());

    // ── Integrity ledger and audit ─────────────────────────────────────────
    register("IntegrityTryBeginReport", "_integrityLedgerBoolAsync", (c, _) =>
      c.IntegrityTryBeginReportAsync(_key(), 1, 10, 1, 5, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    register("IntegrityTryBeginReportBatch", "_integrityLedgerBoolArrayAsync", (c, _) =>
      c.IntegrityTryBeginReportBatchAsync(_origin(), [new IntegrityReportObservation(_key(), 1, 10, 1, 5)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    register("IntegrityStampRepairWindows", "IntegrityStampRepairWindowsAsync", (c, _) =>
      c.IntegrityStampRepairWindowsAsync(_origin(), [_key()], windowFrom: 1, windowUntil: 10));
    register("IntegrityClaimRepairDrain", "IntegrityClaimRepairDrainAsync", (c, _) =>
      c.IntegrityClaimRepairDrainAsync([_origin()], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), maxAttempts: 5, limit: 10));
    register("IntegrityMarkHealedBatchWithAges", "IntegrityMarkHealedBatchWithAgesAsync", (c, _) =>
      c.IntegrityMarkHealedBatchWithAgesAsync(_origin(), [_key()]));
    register("GetIntegrityLedgerSummary", "GetIntegrityLedgerSummaryAsync", (c, _) => c.GetIntegrityLedgerSummaryAsync(5));
    register("AdvanceIntegrityCheckpoint", "AdvanceIntegrityCheckpointAsync", (c, _) => c.AdvanceIntegrityCheckpointAsync());
    register("CountReceivedFromOrigin", "CountReceivedFromOriginAsync", (c, _) => c.CountReceivedFromOriginAsync(_origin(), 0, 10));
    register("TryClaimIntegrityAuditCycle", "_tryClaimWatermarkAsync", async (c, _) =>
      await Assert.That(await c.TryClaimIntegrityAuditCycleAsync(TimeSpan.FromMinutes(5))).IsTrue());
    register("GetIntegrityOriginGeneration", "_withCoordinatorCommandAsync", async (c, _) =>
      await Assert.That(await c.GetIntegrityOriginGenerationAsync()).IsEqualTo(0L));

    // ── Maintenance ────────────────────────────────────────────────────────
    register("GetTablesNeedingRewrite", "GetTablesNeedingRewriteAsync", (c, _) => c.GetTablesNeedingRewriteAsync());
    register("RewriteTable", "RewriteTableAsync", (c, _) => c.RewriteTableAsync("wh_settings"));
    register("RequestTableRewrite", "RequestTableRewriteAsync", (c, _) => c.RequestTableRewriteAsync("wh_settings"));
    register("ClearTableRewriteRequest", "ClearTableRewriteRequestAsync", (c, _) => c.ClearTableRewriteRequestAsync("wh_settings"));
    register("PruneAncientEphemeralPointers", "PruneAncientEphemeralPointersAsync", (c, _) => c.PruneAncientEphemeralPointersAsync());
    register("CloseDigestEpochs", "CloseDigestEpochsAsync", (c, _) => c.CloseDigestEpochsAsync(60, 10));
    register("FindStuckOutboxRows", "_findStuckRowsAsync", async (c, _) =>
      await Assert.That(await c.FindStuckOutboxRowsAsync(5, 10)).IsEmpty());
    register("CleanupCompletedStreams", "CleanupCompletedStreamsAsync", (c, _) => c.CleanupCompletedStreamsAsync([_newId()]));

    // ── Streams and event bodies ───────────────────────────────────────────
    register("CloseStream", "CloseStreamAsync", async (c, conn) => {
      var (streamId, _) = await _seedEventAsync(conn);
      var result = await c.CloseStreamAsync(streamId, throughVersion: 1);
      await Assert.That(result.Status).IsNotNull();
    });
    register("GetArchivedEvents", "GetArchivedEventsAsync", async (c, _) =>
      await Assert.That(await c.GetArchivedEventsAsync(_newId())).IsEmpty());
    register("GetConsumingPerspectiveNames", "GetConsumingPerspectiveNamesAsync", async (c, _) =>
      await Assert.That(await c.GetConsumingPerspectiveNamesAsync(_newId(), 1)).IsEmpty());
    register("GetEventVersion", "GetEventVersionAsync", async (c, conn) => {
      var (_, eventId) = await _seedEventAsync(conn);
      await Assert.That(await c.GetEventVersionAsync(eventId)).IsEqualTo(1L);
    });
    register("GetEphemeralBodiesAboutToReap", "GetEphemeralBodiesAboutToReapAsync", (c, _) => c.GetEphemeralBodiesAboutToReapAsync());
    register("HoldEphemeralDestruction", "HoldEphemeralDestructionAsync", async (c, conn) => {
      var eventId = _newId();
      await c.HoldEphemeralDestructionAsync([eventId], DateTimeOffset.UtcNow.AddHours(1));
      await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_destruction_hold WHERE event_id = @id", eventId)).IsEqualTo(1L);
    });
    register("RecordDestructionFailure", "RecordDestructionFailureAsync", (c, _) =>
      c.RecordDestructionFailureAsync([_newId()], DateTimeOffset.UtcNow.AddHours(1), maxRetries: 3));
    register("SelectRedeliveryEvents", "SelectRedeliveryEventsAsync", async (c, _) =>
      await Assert.That(await c.SelectRedeliveryEventsAsync(new RedeliveryRequest { StreamIds = [_newId()] })).IsEmpty());

    // ── Work flow: claim, complete, commit, fail, release ──────────────────
    register("CompleteOutboxPublished", "CompleteOutboxPublishedAsync", async (c, _) =>
      await Assert.That(await c.CompleteOutboxPublishedAsync([_newId()], debugMode: false)).IsEqualTo(0));
    register("CompletePerspective", "CompletePerspectiveAsync", (c, _) =>
      c.CompletePerspectiveAsync(cursors: [], eventWorkIds: [_newId()], debugMode: false));
    register("FlushCompletions", "FlushCompletionsAsync", (c, _) =>
      c.FlushCompletionsAsync(new FlushCompletionsRequest(OutboxIds: [_newId()])));
    register("ResolveSyncInquiries", "ResolveSyncInquiriesAsync", (c, _) =>
      c.ResolveSyncInquiriesAsync([new SyncInquiry { StreamId = _newId(), PerspectiveName = PERSPECTIVE }]));
    register("GetAppliedEventStatus", "GetAppliedEventStatusAsync", async (c, _) =>
      await c.GetAppliedEventStatusAsync(new AppliedEventInquiry(PERSPECTIVE, _newId())));
    register("ClaimWork", "ClaimWorkAsync", (c, _) =>
      c.ClaimWorkAsync(new ClaimWorkRequest(_newId(), "gate-svc", "gate-host", 1)));
    register("CommitHandlerResult", "CommitHandlerResultAsync", (c, _) => c.CommitHandlerResultAsync(_commitRequest()));
    register("CommitHandlerBatch", "CommitHandlerBatchAsync", async (c, _) =>
      await Assert.That(await c.CommitHandlerBatchAsync([_commitRequest()])).Count().IsEqualTo(1));
    register("ReportFailures", "ReportFailuresAsync", (c, _) =>
      c.ReportFailuresAsync(WorkCategory.Outbox, [new MessageFailure {
        MessageId = _newId(),
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "gate failure"
      }]));
    register("RenewLeases", "RenewLeasesAsync", async (c, _) =>
      await Assert.That(await c.RenewLeasesAsync(WorkCategory.Outbox, [_newId()])).IsEqualTo(0));
    register("ReleaseUnprocessedInbox", "ReleaseUnprocessedInboxAsync", async (c, _) =>
      await Assert.That(await c.ReleaseUnprocessedInboxAsync(_newId(), [_newId()])).IsEqualTo(0));
    register("ReleaseUnstartedLeases", "ReleaseUnstartedLeasesAsync", (c, _) =>
      c.ReleaseUnstartedLeasesAsync(_newId(), [_newId()], []));
    register("ImportBrokerDeadLetter", "ImportBrokerDeadLetterAsync", async (c, _) => {
      var imported = await c.ImportBrokerDeadLetterAsync(new BrokerDeadLetterImport(
        MessageId: _newId(),
        StreamId: _newId(),
        MessageType: "Gate.Message, Gate.Assembly",
        Destination: "inbox/gate-service-inbox",
        EnvelopeJson: """{"v":1,"p":{}}""",
        BrokerReason: null,
        BrokerDescription: null,
        EnqueuedAt: null,
        DeliveryCount: null));
      await Assert.That(imported).IsTrue();
    });

    return calls;
  }

  private static Guid _newId() => (Guid)TrackedGuid.New();

  private static Guid _origin() => Guid.Parse("66666666-6666-6666-6666-666666666666");

  private static IntegrityRepairLedger.DivergenceKey _key() =>
    new(_origin(), "tenant-gate", "Gate.Event", Guid.Parse("77777777-7777-7777-7777-777777777777"));

  private static HandlerCommitRequest _commitRequest() => new(
    HandlerId: _newId(),
    InstanceId: _newId(),
    ServiceName: "gate-svc",
    HostName: "gate-host",
    ProcessId: 1,
    PartitionCount: 10000,
    InboxCompletion: new HandlerInboxCompletion(_newId(), Status: 4));

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task<(Guid StreamId, Guid EventId)> _seedEventAsync(NpgsqlConnection connection) {
    var streamId = _newId();
    var eventId = _newId();
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', 'Gate.Event', '{}'::jsonb, 1, NOW())
      """;
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();
    return (streamId, eventId);
  }

  private static async Task<long> _countAsync(NpgsqlConnection connection, string sql, Guid id) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("id", id);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// A real gate wired to its own metrics, plus a listener that records the caller tag of every
  /// released slot. Scoped to this probe's meter, so concurrent tests cannot leak into it.
  /// </summary>
  private sealed class GateProbe : IDisposable {
    private readonly ServiceProvider _services;
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<string> _callers = new();

    public GateProbe() {
      _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
      Metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(_services.GetRequiredService<IMeterFactory>()));
      Gate = new WorkCoordinatorGate(4, NullLogger<WorkCoordinatorGate>.Instance, metrics: Metrics);
      var meter = Metrics.GateHoldDuration.Meter;
      _listener.InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == HOLD_INSTRUMENT) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<double>((_, _, tags, _) => {
        foreach (var tag in tags) {
          if (tag.Key == "caller" && tag.Value is string caller) {
            _callers.Enqueue(caller);
          }
        }
      });
      _listener.Start();
    }

    public WorkCoordinatorMetrics Metrics { get; }

    public WorkCoordinatorGate Gate { get; }

    public IReadOnlyList<string> Callers => [.. _callers];

    public void Dispose() {
      _listener.Dispose();
      Gate.Dispose();
      _services.Dispose();
    }
  }
}
