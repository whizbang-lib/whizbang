// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.WorkCoordinator;

/// <summary>
/// How the coordinator reads the scalar a SQL function hands back when it is not the shape the
/// happy path expects: a NULL where a count was promised, a settled maximum that clamps a window,
/// a generation that moved, a bulk-tier fallback that carries no reason, a service identity row
/// that is missing or empty.
/// </summary>
/// <remarks>
/// <para>
/// Each count read is written <c>result is int n ? n : 0</c>. The shipped functions always answer
/// an integer, so the zero arm only runs when a function answers NULL, and nothing exercised it: a
/// later edit to a direct cast would turn a quiet NULL from a maintenance function into an
/// <see cref="InvalidCastException"/> thrown out of a maintenance tick. The NULL is produced by
/// replacing the function with one of the same arity that returns NULL, which exercises the guard
/// against the real Npgsql reader. <see cref="EFCoreTestBase"/> provisions a database per test,
/// so each replacement is invisible to every other test.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class EFCoreWorkCoordinatorScalarShapeTests : EFCoreTestBase {
  private const string FALLBACK_COUNTER = "whizbang.work_coordinator.commit_handler.fallbacks";

  // ── A NULL where a count was promised reads as zero ──────────────────────

  [Test]
  public async Task CloseDigestEpochs_WhenTheFunctionAnswersNull_ReportsZeroClosedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("close_digest_epochs", "(TIMESTAMPTZ, INTEGER, INTEGER) RETURNS INTEGER", "SELECT NULL::INTEGER");

    var closed = await _coordinator(dbContext).CloseDigestEpochsAsync(60, 10);

    await Assert.That(closed).IsEqualTo(0)
      .Because("a NULL answer closed nothing; reading it as a cast would throw out of the epoch sweep");
  }

  [Test]
  public async Task CountPerspectiveRetentionBacklog_WhenTheFunctionAnswersNull_ReportsZeroAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("count_perspective_retention_backlog", "(TEXT) RETURNS BIGINT", "SELECT NULL::BIGINT");

    var backlog = await _coordinator(dbContext).CountPerspectiveRetentionBacklogAsync("Scalar.Model");

    await Assert.That(backlog).IsEqualTo(0L);
  }

  [Test]
  public async Task CascadeDeletePerspectiveRows_WhenTheFunctionAnswersNull_ReportsZeroDeletedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("cascade_delete_perspective_rows", "(TEXT, UUID[]) RETURNS INTEGER", "SELECT NULL::INTEGER");

    var deleted = await _coordinator(dbContext).CascadeDeletePerspectiveRowsAsync("wh_per_scalar", [_newId()]);

    await Assert.That(deleted).IsEqualTo(0);
  }

  [Test]
  public async Task FoldSettledApplyPaths_WhenTheFunctionAnswersNull_ReportsZeroFoldedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("fold_settled_apply_paths", "(BIGINT, INTEGER) RETURNS INTEGER", "SELECT NULL::INTEGER");

    var folded = await _coordinator(dbContext).FoldSettledApplyPathsAsync(TimeSpan.FromMinutes(5));

    await Assert.That(folded).IsEqualTo(0);
  }

  [Test]
  public async Task FoldStreamApplyPaths_WhenTheFunctionAnswersNull_ReportsZeroFoldedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("fold_stream_apply_paths", "(UUID[]) RETURNS INTEGER", "SELECT NULL::INTEGER");

    var folded = await _coordinator(dbContext).FoldStreamApplyPathsAsync([_newId()]);

    await Assert.That(folded).IsEqualTo(0);
  }

  [Test]
  public async Task CleanupCompletedStreams_WhenTheFunctionAnswersNull_ReportsZeroEvictedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("cleanup_completed_streams", "(UUID[]) RETURNS INTEGER", "SELECT NULL::INTEGER");

    var evicted = await _coordinator(dbContext).CleanupCompletedStreamsAsync([_newId()]);

    await Assert.That(evicted).IsEqualTo(0);
  }

  [Test]
  public async Task CompletePerspectiveEvents_WhenTheFunctionAnswersNull_ReportsZeroCompletedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync("complete_perspective_events", "(UUID[], BOOLEAN) RETURNS INTEGER", "SELECT NULL::INTEGER");

    var completed = await _coordinator(dbContext).CompletePerspectiveEventsAsync([_newId()], debugMode: false);

    await Assert.That(completed).IsEqualTo(0);
  }

  [Test]
  public async Task ReapExhaustedOrphanedPerspectiveRows_ListInputAndNullAnswer_ReportsZeroReapedAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync(
      "reap_exhausted_orphaned_perspective_rows", "(UUID, UUID[], INTEGER) RETURNS INTEGER", "SELECT NULL::INTEGER");

    // A List, not an array, so the ids are copied into the array the parameter needs.
    var streamIds = new List<Guid> { _newId(), _newId() };
    var reaped = await _coordinator(dbContext).ReapExhaustedOrphanedPerspectiveRowsAsync(_newId(), streamIds, maxAttempts: 3);

    await Assert.That(reaped).IsEqualTo(0);
  }

  [Test]
  public async Task SyncPerspectiveRetention_WhenTheFunctionAnswersNull_WarnsThatNothingWasEnrolledAsync() {
    await using var dbContext = CreateDbContext();
    await _replaceFunctionAsync(
      "sync_perspective_retention",
      "(TEXT, BOOLEAN, INTEGER, INTEGER, INTEGER, TEXT) RETURNS INTEGER",
      "SELECT NULL::INTEGER");
    var logger = new CapturingLogger(enabled: true);

    await _coordinator(dbContext, logger).SyncPerspectiveRetentionAsync(
      [new PerspectiveRetentionDeclaration("Scalar.Model", Enrolled: true, 60, null, CapPerScope: 5, CapScopeKey: "t")]);

    await Assert.That(logger.Messages.Any(m => m.Contains("matched no wh_perspective_registry row", StringComparison.Ordinal))).IsTrue()
      .Because("a NULL match count is read as zero matches, which is the key-drift case the warning exists to name");
  }

  // ── A settled maximum clamps the digest window ─────────────────────────

  [Test]
  public async Task GetIntegritySettledMax_WhenTheLaneHasSettled_ReturnsTheMaximumAsync() {
    await using var dbContext = CreateDbContext();
    await _settledMaxIsAsync(41);

    var max = await _coordinator(dbContext).GetIntegritySettledMaxAsync(null, TimeSpan.FromSeconds(30));

    await Assert.That(max).IsEqualTo(41L);
  }

  [Test]
  public async Task ComputeTypeDigestsWindowed_SettledBelowTheWatermark_ReturnsAnEmptyAnswerAnchoredAtTheWatermarkAsync() {
    await using var dbContext = CreateDbContext();
    await _settledMaxIsAsync(41);

    var result = await _coordinator(dbContext).ComputeTypeDigestsWindowedAsync(
      null, null, sinceSequence: 50, untilSequence: null, TimeSpan.FromSeconds(30));

    await Assert.That(result!.Digests).IsEmpty();
    await Assert.That(result.ComputedThrough).IsEqualTo(50L)
      .Because("nothing settled beyond the asker's watermark, so the watermark must not move");
  }

  [Test]
  public async Task ComputeTypeDigestsWindowed_SettledAboveTheWatermark_ComputesThroughTheSettledMaximumAsync() {
    await using var dbContext = CreateDbContext();
    await _settledMaxIsAsync(41);

    var result = await _coordinator(dbContext).ComputeTypeDigestsWindowedAsync(
      null, ["Scalar.Event"], sinceSequence: 10, untilSequence: null, TimeSpan.FromSeconds(30));

    await Assert.That(result!.ComputedThrough).IsEqualTo(42L)
      .Because("the answer covers up to and including the settled maximum, an exclusive end of max + 1");
  }

  [Test]
  public async Task ComputeStreamDigestsWindowed_SettledBelowTheWatermark_ReturnsAnEmptyAnswerAnchoredAtTheWatermarkAsync() {
    await using var dbContext = CreateDbContext();
    await _settledMaxIsAsync(41);

    var result = await _coordinator(dbContext).ComputeStreamDigestsWindowedAsync(
      null, null, sinceSequence: 50, untilSequence: null, resumeAfterStreamId: null, maxDigests: 10, TimeSpan.FromSeconds(30));

    await Assert.That(result!.Digests).IsEmpty();
    await Assert.That(result.ComputedThrough).IsEqualTo(50L);
  }

  [Test]
  public async Task ComputeStreamDigestsWindowed_SettledAboveTheWatermark_ComputesThroughTheSettledMaximumAsync() {
    await using var dbContext = CreateDbContext();
    await _settledMaxIsAsync(41);

    var result = await _coordinator(dbContext).ComputeStreamDigestsWindowedAsync(
      null, null, sinceSequence: 10, untilSequence: 30, resumeAfterStreamId: null, maxDigests: 10, TimeSpan.FromSeconds(30));

    await Assert.That(result!.ComputedThrough).IsEqualTo(30L)
      .Because("a requested end below the settled maximum is kept: the window never reaches past what was asked");
    await Assert.That(result.ResumeAfterStreamId).IsNull();
  }

  // ── Generation guard ───────────────────────────────────────────────────

  [Test]
  public async Task EnsureIntegritySealGeneration_FirstContactThenAMovedGeneration_ProceedsThenSkipsAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var origin = _newId();

    var first = await coordinator.EnsureIntegritySealGenerationAsync(origin, 1);
    var moved = await coordinator.EnsureIntegritySealGenerationAsync(origin, 2);

    await Assert.That(first).IsTrue()
      .Because("first contact records the offered generation and the comparison proceeds");
    await Assert.That(moved).IsFalse()
      .Because("a changed generation means the origin's history legitimately moved; the round is skipped");
  }

  // ── Heartbeat ──────────────────────────────────────────────────────────

  [Test]
  public async Task RecordHeartbeat_WithMetadata_StoresItAndAnEvictedInstanceIsRefusedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);
    var instanceId = _newId();
    using var metadata = JsonDocument.Parse("""{"zone":"a"}""");

    var accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
      instanceId, "scalar-svc", "scalar-host", 7, metadata.RootElement.Clone(), LifecyclePhase: "Running", LibraryVersion: "1.2.3", StaleThresholdSeconds: 90));

    await Assert.That(accepted).IsTrue();
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT metadata->>'zone' FROM wh_service_instances WHERE instance_id = @id";
      read.Parameters.AddWithValue("id", instanceId);
      await Assert.That((string?)await read.ExecuteScalarAsync()).IsEqualTo("a")
        .Because("supplied metadata is sent as-is rather than replaced by the empty-object default");
    }

    await coordinator.EvictInstanceAsync(instanceId, _newId(), "scalar test");
    var afterEviction = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "scalar-svc", "scalar-host", 7));

    await Assert.That(afterEviction).IsFalse()
      .Because("a tombstoned instance must stop heartbeating; reading the FALSE as accepted would let it rejoin");
  }

  [Test]
  public async Task RecordInstanceState_ForARegisteredInstanceWithAVersion_ReportsTheRowWasUpdatedAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var instanceId = _newId();
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "scalar-svc", "scalar-host", 7));

    var updated = await coordinator.RecordInstanceStateAsync(instanceId, "Draining", "9.9.9");

    await Assert.That(updated).IsTrue();
  }

  // ── Bulk-tier fallback without a reason ────────────────────────────────

  [Test]
  public async Task CommitHandlerBatch_FallbackWithoutABulkError_LogsUnknownAndCountsTheFallbackAsync() {
    await using var dbContext = CreateDbContext();
    // Every row reports tier 2 (the per-handler fallback) with no bulk-tier reason.
    await _replaceFunctionAsync(
      "commit_handler_batch",
      "(p_results JSONB) RETURNS TABLE(handler_id UUID, success BOOLEAN, error_message TEXT, tier INTEGER, bulk_error TEXT)",
      "SELECT (e->>'handler_id')::uuid, TRUE, NULL::TEXT, 2, NULL::TEXT FROM jsonb_array_elements(p_results) e");
    await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(services.GetRequiredService<IMeterFactory>()));
    var logger = new CapturingLogger(enabled: true);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), logger, metrics);

    var results = await coordinator.CommitHandlerBatchAsync([new HandlerCommitRequest(
      HandlerId: _newId(),
      InstanceId: _newId(),
      ServiceName: "scalar-svc",
      HostName: "scalar-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(_newId(), Status: 4))]);

    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(logger.Messages.Any(m => m.Contains("per-handler savepoints: unknown", StringComparison.Ordinal))).IsTrue()
      .Because("a fallback with no recorded reason still warns, naming the reason as unknown rather than blank");
    await Assert.That(_readCounter(metrics, FALLBACK_COUNTER)).IsEqualTo(1L)
      .Because("the fallback counter is what an operator alerts on when a fleet quietly lives on the slow path");
  }

  // ── Broker dead-letter custody ─────────────────────────────────────────

  [Test]
  public async Task ImportBrokerDeadLetter_TheSameMessageTwice_ReportsTheSecondAsADuplicateAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var import = new Whizbang.Core.Transports.BrokerDeadLetterImport(
      MessageId: _newId(),
      StreamId: null,
      MessageType: null,
      Destination: "inbox/scalar-service-inbox",
      EnvelopeJson: """{"v":1,"p":{}}""",
      BrokerReason: "MaxDeliveryAttemptsExceeded",
      BrokerDescription: "scalar",
      EnqueuedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
      DeliveryCount: 10);

    var first = await coordinator.ImportBrokerDeadLetterAsync(import);
    var second = await coordinator.ImportBrokerDeadLetterAsync(import);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("FALSE means custody already exists and the broker copy is safe to settle; reading it as a new import would double the row");
  }

  // ── Local service identity: absent and empty ───────────────────────────

  [Test]
  public async Task GetLocalServiceId_WithNoIdentityRow_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    await _executeAsync("DELETE FROM wh_service_config");

    var serviceId = await _coordinator(dbContext).GetLocalServiceIdAsync();

    await Assert.That(serviceId).IsEqualTo(Guid.Empty)
      .Because("no row is the unknown identity, never an exception from the lookup every publish depends on");
  }

  [Test]
  public async Task GetLocalServiceId_WithANullIdentity_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    await _executeAsync("""
      ALTER TABLE wh_service_config ALTER COLUMN service_id DROP NOT NULL;
      UPDATE wh_service_config SET service_id = NULL;
      """);

    var serviceId = await _coordinator(dbContext).GetLocalServiceIdAsync();

    await Assert.That(serviceId).IsEqualTo(Guid.Empty)
      .Because("a row whose identity is NULL is read as DBNull and must mean the same unknown identity");
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static Guid _newId() => (Guid)TrackedGuid.New();

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(
      WorkCoordinationDbContext dbContext, ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>? logger = null) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions(), logger);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Drops every overload of a function and recreates it with the given signature and SQL body.</summary>
  private Task _replaceFunctionAsync(string name, string signature, string body) =>
    _executeAsync($"""
      SELECT drop_all_overloads('{name}');
      CREATE FUNCTION {name}{signature} LANGUAGE sql AS $fn$ {body} $fn$;
      """);

  private Task _settledMaxIsAsync(long max) =>
    _replaceFunctionAsync("integrity_settled_max", "(UUID, TIMESTAMPTZ, INTEGER) RETURNS BIGINT", $"SELECT {max}::BIGINT");

  private static long _readCounter(WorkCoordinatorMetrics metrics, string name) {
    var meter = metrics.GateHoldDuration.Meter;
    long total = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == name) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => total += value);
    listener.Start();
    listener.RecordObservableInstruments();
    return total;
  }

  /// <summary>Records every formatted message; <c>enabled</c> decides what IsEnabled answers.</summary>
  private sealed class CapturingLogger(bool enabled) : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<string> _messages = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<string> Messages {
      get {
        lock (_lock) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => enabled;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _messages.Add(formatter(state, exception));
      }
    }
  }
}
