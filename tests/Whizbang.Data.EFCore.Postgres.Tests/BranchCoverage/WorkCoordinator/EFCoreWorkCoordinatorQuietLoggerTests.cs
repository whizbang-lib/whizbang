// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
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
/// The coordinator's degrade-and-log paths and diagnostic logging behave the same when the host
/// supplies no logger, or a logger with the relevant level turned off.
/// </summary>
/// <remarks>
/// <para>
/// Every catch that degrades instead of throwing logs through <c>_logger?.</c>, and every
/// diagnostic is guarded by <c>_logger?.IsEnabled(level) == true</c>. The existing tests drive
/// those paths with an always-enabled capturing logger, so the null-logger and level-off arms ran
/// nowhere. Those arms matter: the coordinator is constructed without a logger in several hosts,
/// and a null-conditional edited into a plain call would turn a degraded ledger call into a
/// <see cref="NullReferenceException"/> thrown from inside its own catch, which is exactly the
/// failure the catch exists to prevent.
/// </para>
/// <para>
/// Faults are produced the way the sibling degradation tests produce them: by dropping the
/// function or table the call depends on, in the per-test database.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class EFCoreWorkCoordinatorQuietLoggerTests : EFCoreTestBase {
  private const string ORPHAN_EVENT_TYPE = "Whizbang.Tests.QuietOrphanEvent";
  private static readonly string[] _onePerspective = ["P.One"];
  private static readonly Guid _originId = Guid.Parse("88888888-8888-8888-8888-888888888888");

  // ── Integrity ledger degradation with no logger ─────────────────────────

  [Test]
  public async Task IntegrityTryBeginReport_LedgerUnavailableAndNoLogger_StillFailsOpenAsync() {
    await using var dbContext = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_try_begin_report");

    var began = await _coordinator(dbContext, logger: null).IntegrityTryBeginReportAsync(
      _key(), 1, 10, 1, 5, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(began).IsTrue()
      .Because("the fail-open direction does not depend on a logger being present");
  }

  [Test]
  public async Task IntegrityTryBeginReportBatch_BatchFunctionMissingAndNoLogger_ReturnsNullAsync() {
    await using var dbContext = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_try_begin_report_batch");

    var results = await _coordinator(dbContext, logger: null).IntegrityTryBeginReportBatchAsync(
      _originId, [new IntegrityReportObservation(_key(), 1, 10, 1, 5)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(results).IsNull()
      .Because("null is the fall-back-to-single-keys signal with or without a logger");
  }

  [Test]
  public async Task IntegrityStampRepairWindows_LedgerUnavailableAndNoLogger_DoesNotThrowAsync() {
    await using var dbContext = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_stamp_repair_windows");

    await _coordinator(dbContext, logger: null).IntegrityStampRepairWindowsAsync(_originId, [_key()], 1, 10);

    // Reaching here is the assertion: the stamp is best-effort and must never fail its caller.
    await Assert.That(await _functionCountAsync("wh_integrity_stamp_repair_windows")).IsEqualTo(0L);
  }

  [Test]
  public async Task IntegrityClaimRepairDrain_LedgerUnavailableAndNoLogger_DispatchesNothingAsync() {
    await using var dbContext = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_claim_repair_drain");

    var items = await _coordinator(dbContext, logger: null).IntegrityClaimRepairDrainAsync(
      [_originId], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), maxAttempts: 5, limit: 10);

    await Assert.That(items).IsEmpty();
  }

  [Test]
  public async Task IntegrityMarkHealedBatchWithAges_BatchFunctionMissingAndNoLogger_ReturnsNullAsync() {
    await using var dbContext = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_mark_healed_batch");

    var ages = await _coordinator(dbContext, logger: null).IntegrityMarkHealedBatchWithAgesAsync(_originId, [_key()]);

    await Assert.That(ages).IsNull();
  }

  [Test]
  public async Task GetIntegrityLedgerSummary_LedgerUnavailableAndNoLogger_ReturnsTheEmptySnapshotAsync() {
    await using var dbContext = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_ledger_summary");

    var snapshot = await _coordinator(dbContext, logger: null).GetIntegrityLedgerSummaryAsync(5);

    await Assert.That(snapshot).IsEqualTo(LedgerGaugeSnapshot.Empty);
  }

  // ── Orphaned lifecycle reconciliation with a quiet or absent logger ────

  [Test]
  public async Task GetOrphanedLifecycleEvents_UndeserializableRowWithQuietAndAbsentLoggers_SkipsTheRowSilentlyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _seedOrphanAsync(connection);
    var quiet = new CapturingLogger(enabled: false);

    var withQuiet = await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, _sabotagedOptions(SabotageMode.ThrowUnrelated), quiet).GetOrphanedLifecycleEventsAsync(_orphanMap(), TimeSpan.FromHours(1));
    var withNone = await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, _sabotagedOptions(SabotageMode.ThrowUnrelated)).GetOrphanedLifecycleEventsAsync(_orphanMap(), TimeSpan.FromHours(1));

    await Assert.That(withQuiet).IsEmpty();
    await Assert.That(withNone).IsEmpty();
    await Assert.That(quiet.Messages).IsEmpty()
      .Because("the warning is guarded by IsEnabled(Warning); a logger with warnings off must receive nothing");
  }

  [Test]
  public async Task GetOrphanedLifecycleEvents_QueryFailsWithQuietAndAbsentLoggers_ReturnsEmptyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    await _seedOrphanAsync(connection);
    await using (var drop = connection.CreateCommand()) {
      drop.CommandText = "DROP TABLE wh_lifecycle_completions CASCADE";
      await drop.ExecuteNonQueryAsync();
    }
    var quiet = new CapturingLogger(enabled: false);

    var withQuiet = await _coordinator(dbContext, quiet).GetOrphanedLifecycleEventsAsync(_orphanMap(), TimeSpan.FromHours(1));
    var withNone = await _coordinator(dbContext, logger: null).GetOrphanedLifecycleEventsAsync(_orphanMap(), TimeSpan.FromHours(1));

    await Assert.That(withQuiet).IsEmpty();
    await Assert.That(withNone).IsEmpty();
    await Assert.That(quiet.Messages).IsEmpty();
  }

  [Test]
  public async Task GetOrphanedLifecycleEvents_ScopeUnparseableAndNoLogger_ReturnsTheEventWithoutHopsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var (_, eventId) = await _seedOrphanAsync(connection, scope: """{"t":"tenant-quiet"}""");

    // With JsonElement unresolvable, the payload falls back to a direct parse, and the scope
    // parse fails into its catch, which logs through the null-logger fallback.
    var orphans = await _coordinator(dbContext, logger: null, _sabotagedOptions(SabotageMode.ReturnNull))
      .GetOrphanedLifecycleEventsAsync(_orphanMap(), TimeSpan.FromHours(1));

    await Assert.That(orphans).Count().IsEqualTo(1);
    await Assert.That(orphans[0].EventId).IsEqualTo(eventId);
    await Assert.That(orphans[0].Envelope.Hops).IsEmpty()
      .Because("a scope that cannot be parsed yields no security hop, and the absent logger must not turn that into a throw");
  }

  // ── Cursor reporting with a quiet or absent logger ─────────────────────

  [Test]
  public async Task ReportPerspectiveCompletion_ExistingCursorWithQuietAndAbsentLoggers_AdvancesWithoutLoggingAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var quiet = new CapturingLogger(enabled: false);

    foreach (var logger in new CapturingLogger?[] { quiet, null }) {
      var streamId = _newId();
      var eventId = _newId();
      await _insertEventStoreRowAsync(connection, eventId, streamId);
      await _insertCursorAsync(connection, streamId, "P.Quiet");

      await _coordinator(dbContext, logger).ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = "P.Quiet",
        LastEventId = eventId,
        Status = PerspectiveProcessingStatus.Completed,
        ProcessedEventIds = [eventId]
      });

      await Assert.That(await _countAsync(connection,
        "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @stream AND last_event_id = @last AND status = 2",
        ("stream", streamId), ("last", eventId))).IsEqualTo(1L);
    }
    await Assert.That(quiet.Messages).IsEmpty()
      .Because("every diagnostic on this path is guarded by IsEnabled; none may reach a logger with its levels off");
  }

  [Test]
  public async Task ReportPerspectiveCompletion_NoCursorWithQuietAndAbsentLoggers_CompletesWithoutLoggingAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var quiet = new CapturingLogger(enabled: false);

    foreach (var logger in new CapturingLogger?[] { quiet, null }) {
      var streamId = _newId();
      await _coordinator(dbContext, logger).ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = "P.Missing",
        LastEventId = _newId(),
        Status = PerspectiveProcessingStatus.Completed
      });

      await Assert.That(await _countAsync(connection,
        "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @stream", ("stream", streamId))).IsEqualTo(0L);
    }
    await Assert.That(quiet.Messages).IsEmpty();
  }

  [Test]
  public async Task ReportPerspectiveCompletionAndFailure_EmptyLastEventWithQuietAndAbsentLoggers_SkipWithoutLoggingAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var quiet = new CapturingLogger(enabled: false);

    foreach (var logger in new CapturingLogger?[] { quiet, null }) {
      var coordinator = _coordinator(dbContext, logger);
      await coordinator.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
        StreamId = _newId(),
        PerspectiveName = "P.EmptyQuiet",
        LastEventId = Guid.Empty,
        Status = PerspectiveProcessingStatus.Completed
      });
      await coordinator.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
        StreamId = _newId(),
        PerspectiveName = "P.EmptyQuiet",
        LastEventId = Guid.Empty,
        Status = PerspectiveProcessingStatus.Failed,
        Error = "quiet failure"
      });
    }

    await Assert.That(await _countAsync(connection,
      "SELECT count(*) FROM wh_perspective_cursors WHERE perspective_name = 'P.EmptyQuiet'")).IsEqualTo(0L);
    await Assert.That(quiet.Messages).IsEmpty();
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static Guid _newId() => (Guid)TrackedGuid.New();

  private static IntegrityRepairLedger.DivergenceKey _key() =>
    new(_originId, "tenant-quiet", "Quiet.Event", Guid.Parse("99999999-9999-9999-9999-999999999999"));

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(
      WorkCoordinationDbContext dbContext,
      ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>>? logger,
      JsonSerializerOptions? options = null) =>
    new(dbContext, options ?? JsonContextRegistry.CreateCombinedOptions(), logger);

  private static Dictionary<string, IReadOnlyList<string>> _orphanMap() =>
    new() { [ORPHAN_EVENT_TYPE] = _onePerspective };

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private async Task _dropFunctionAsync(string name) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT drop_all_overloads('{name}')";
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<long> _functionCountAsync(string name) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM pg_proc WHERE proname = @name";
    cmd.Parameters.AddWithValue(nameof(name), name);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _countAsync(
      NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(Guid StreamId, Guid EventId)> _seedOrphanAsync(NpgsqlConnection connection, string scope = "{}") {
    var streamId = _newId();
    var eventId = _newId();
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
        VALUES (@evt, @stream, @stream, 'agg', @type, @scope::jsonb, 1, NOW());
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@evt, '{}'::jsonb, '{}'::jsonb)
        """;
      ins.Parameters.AddWithValue("evt", eventId);
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("type", ORPHAN_EVENT_TYPE);
      ins.Parameters.AddWithValue(nameof(scope), scope);
      await ins.ExecuteNonQueryAsync();
    }
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = """
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
        VALUES (@stream, 'P.One', @last_event, 1, NOW())
        """;
      ins.Parameters.AddWithValue("stream", streamId);
      ins.Parameters.AddWithValue("last_event", eventId);
      await ins.ExecuteNonQueryAsync();
    }
    return (streamId, eventId);
  }

  private static async Task _insertEventStoreRowAsync(NpgsqlConnection connection, Guid eventId, Guid streamId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', 'P.Type', '{}'::jsonb, 1, NOW())
      """;
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertCursorAsync(NpgsqlConnection connection, Guid streamId, string perspectiveName) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@stream, @name, NULL, 1, NOW())
      """;
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    await ins.ExecuteNonQueryAsync();
  }

  private static JsonSerializerOptions _sabotagedOptions(SabotageMode mode) {
    var combined = JsonContextRegistry.CreateCombinedOptions();
    var inner = combined.TypeInfoResolver
      ?? throw new InvalidOperationException("Combined options must expose a TypeInfoResolver.");
    return new JsonSerializerOptions(combined) {
      TypeInfoResolver = new JsonElementSabotagingResolver(inner, mode)
    };
  }

  private enum SabotageMode {
    /// <summary>The resolver returns null, so GetTypeInfo(JsonElement) throws NotSupportedException.</summary>
    ReturnNull,
    /// <summary>The resolver throws an InvalidOperationException neither fallback recognizes.</summary>
    ThrowUnrelated
  }

  private sealed class JsonElementSabotagingResolver(IJsonTypeInfoResolver inner, SabotageMode mode) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
      if (type != typeof(JsonElement)) {
        return inner.GetTypeInfo(type, options);
      }
      return mode == SabotageMode.ReturnNull
        ? null
        : throw new InvalidOperationException("Test resolver refused JsonElement resolution.");
    }
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
