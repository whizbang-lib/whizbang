using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// DB round-trip tests for the out-of-band cursor reporting surface of
/// <see cref="EFCoreWorkCoordinator{TDbContext}"/>: <c>ReportPerspectiveCompletionAsync</c>
/// and <c>ReportPerspectiveFailureAsync</c> (both delegate to
/// <c>complete_perspective_cursor_work</c>, mig 005), including the empty-LastEventId
/// skip guards, the transaction rollback-and-rethrow path when the SQL call fails,
/// and both branches of the post-update checkpoint diagnostic query.
/// A level-agnostic capturing logger keeps every diagnostic logging branch live.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public class EFCoreWorkCoordinatorCursorReportingTests : EFCoreTestBase {

  // --------------------------------------------------------------------------
  // ReportPerspectiveCompletionAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task ReportPerspectiveCompletionAsync_EmptyLastEventId_SkipsCheckpointUpdateAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, logger);

    await coordinator.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "P.EmptyCheckpoint",
      LastEventId = Guid.Empty,
      Status = PerspectiveProcessingStatus.Completed
    });

    // Guard fires before any SQL — no cursor row may appear.
    var cursorRows = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_perspective_cursors WHERE perspective_name = 'P.EmptyCheckpoint'");
    await Assert.That(cursorRows).IsEqualTo(0L);
    await Assert.That(logger.MessagesFor(LogLevel.Debug).Any(m => m.Contains("Skipping checkpoint update", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ReportPerspectiveCompletionAsync_ExistingCursor_AdvancesCursorAndMarksProcessedEventsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, logger);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "P.Advance";

    await _insertEventStoreRowAsync(connection, eventId, streamId);
    await _insertCursorAsync(connection, streamId, perspectiveName, lastEventId: null, status: 1);
    await _insertPerspectiveEventAsync(connection, Guid.NewGuid(), streamId, perspectiveName, eventId);

    await coordinator.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventId,
      Status = PerspectiveProcessingStatus.Completed,
      ProcessedEventIds = [eventId]
    });

    // Cursor advanced to the reported event with Completed (2) status and no error.
    var advanced = await _countAsync(connection, @"
      SELECT COUNT(*) FROM wh_perspective_cursors
      WHERE stream_id = @stream AND perspective_name = @name
        AND last_event_id = @last AND status = 2 AND error IS NULL",
      ("stream", streamId), ("name", perspectiveName), ("last", eventId));
    await Assert.That(advanced).IsEqualTo(1L);

    // Only the explicitly listed event is stamped processed.
    var stamped = await _countAsync(connection, @"
      SELECT COUNT(*) FROM wh_perspective_events
      WHERE stream_id = @stream AND perspective_name = @name
        AND event_id = @eid AND processed_at IS NOT NULL",
      ("stream", streamId), ("name", perspectiveName), ("eid", eventId));
    await Assert.That(stamped).IsEqualTo(1L);

    // Post-update diagnostic found the checkpoint — no "not found" warning fired.
    await Assert.That(logger.MessagesFor(LogLevel.Warning)).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReportPerspectiveCompletionAsync_NoCursorRow_LogsCheckpointNotFoundWarningAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, logger);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();

    // No cursor row exists — the UPDATE is a silent no-op and the diagnostic
    // read-back takes the "checkpoint not found" branch.
    await coordinator.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = streamId,
      PerspectiveName = "P.Missing",
      LastEventId = eventId,
      Status = PerspectiveProcessingStatus.Completed
    });

    var cursorRows = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_perspective_cursors WHERE stream_id = @stream", ("stream", streamId));
    await Assert.That(cursorRows).IsEqualTo(0L);
    await Assert.That(logger.MessagesFor(LogLevel.Warning).Any(m => m.Contains("Checkpoint not found", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ReportPerspectiveCompletionAsync_CursorsTableMissing_RollsBackAndRethrowsAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, logger);

    // Break the data precondition: the SQL function's UPDATE target is gone, so
    // ExecuteSqlRawAsync throws inside the coordinator-managed transaction.
    await using (var drop = connection.CreateCommand()) {
      drop.CommandText = "DROP TABLE wh_perspective_cursors CASCADE";
      await drop.ExecuteNonQueryAsync();
    }

    await Assert.That(async () => await coordinator.ReportPerspectiveCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "P.Broken",
      LastEventId = (Guid)TrackedGuid.NewMedo(),
      Status = PerspectiveProcessingStatus.Completed
    })).Throws<PostgresException>();

    // The rollback must leave the connection usable for follow-up commands.
    var probe = await _countAsync(connection, "SELECT COUNT(*) FROM wh_perspective_events");
    await Assert.That(probe).IsEqualTo(0L);
  }

  // --------------------------------------------------------------------------
  // ReportPerspectiveFailureAsync
  // --------------------------------------------------------------------------

  [Test]
  public async Task ReportPerspectiveFailureAsync_EmptyLastEventId_SkipsCheckpointUpdateAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, logger);

    await coordinator.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "P.EmptyFailure",
      LastEventId = Guid.Empty,
      Status = PerspectiveProcessingStatus.Failed,
      Error = "never persisted"
    });

    var cursorRows = await _countAsync(connection,
      "SELECT COUNT(*) FROM wh_perspective_cursors WHERE perspective_name = 'P.EmptyFailure'");
    await Assert.That(cursorRows).IsEqualTo(0L);
    await Assert.That(logger.MessagesFor(LogLevel.Debug).Any(m => m.Contains("Skipping checkpoint update", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ReportPerspectiveFailureAsync_ExistingCursor_RecordsErrorAndFailedStatusAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openConnectionAsync(dbContext);
    var logger = new CapturingLogger();
    var coordinator = _createCoordinator(dbContext, logger);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "P.Failing";

    await _insertEventStoreRowAsync(connection, eventId, streamId);
    await _insertCursorAsync(connection, streamId, perspectiveName, lastEventId: null, status: 1);

    await coordinator.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = eventId,
      Status = PerspectiveProcessingStatus.Failed,
      Error = "projection exploded"
    });

    // Failed (4) status with the error message persisted on the cursor row.
    var failed = await _countAsync(connection, @"
      SELECT COUNT(*) FROM wh_perspective_cursors
      WHERE stream_id = @stream AND perspective_name = @name
        AND last_event_id = @last AND status = 4 AND error = 'projection exploded'",
      ("stream", streamId), ("name", perspectiveName), ("last", eventId));
    await Assert.That(failed).IsEqualTo(1L);
  }

  // --------------------------------------------------------------------------
  // Helpers
  // --------------------------------------------------------------------------

  private static async Task<NpgsqlConnection> _openConnectionAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _createCoordinator(
      WorkCoordinationDbContext dbContext, CapturingLogger logger) {
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), logger);
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

  private static async Task _insertCursorAsync(
      NpgsqlConnection connection, Guid streamId, string perspectiveName, Guid? lastEventId, short status) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_cursors
        (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@stream, @name, @last_event, @status, NOW())";
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    ins.Parameters.AddWithValue("last_event", (object?)lastEventId ?? DBNull.Value);
    ins.Parameters.AddWithValue("status", status);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection connection, Guid eventWorkId, Guid streamId, string perspectiveName, Guid eventId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, status, attempts, created_at)
      VALUES (@work, @stream, @name, @eid, 0, 0, NOW())";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("name", perspectiveName);
    ins.Parameters.AddWithValue("eid", eventId);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertEventStoreRowAsync(
      NpgsqlConnection connection, Guid eventId, Guid streamId) {
    await using var ins = connection.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', 'P.Type', '{}'::jsonb, 1, NOW())";
    ins.Parameters.AddWithValue("evt", eventId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Level-agnostic logger that records every formatted message so diagnostic
  /// branches guarded by IsEnabled checks stay live during the tests.
  /// </summary>
  private sealed class CapturingLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _lock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _entries.Add((logLevel, formatter(state, exception)));
      }
    }

    public List<string> MessagesFor(LogLevel level) {
      lock (_lock) {
        return [.. _entries.Where(e => e.Level == level).Select(e => e.Message)];
      }
    }
  }
}
