using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for <see cref="DapperPostgresPerspectiveCheckpointCompleter"/>.
/// Drives the INSERT ... ON CONFLICT DO UPDATE cursor completion path against a real
/// Postgres database, covering the main completion flow, the empty-list and Guid.Empty
/// guards, the ON CONFLICT update (which clears error / rewind columns), the debug-log
/// branch, and the transaction rollback-on-failure path.
/// </summary>
[NotInParallel("DapperPerspectiveCheckpointCompleterTests")]
public class DapperPostgresPerspectiveCheckpointCompleterTests : PostgresTestBase {

  private const string PERSPECTIVE_NAME =
      "Whizbang.Data.Dapper.Postgres.Tests.Perspectives.CheckpointCompleterPerspective";

  /// <summary>Captures every emitted log entry so the debug-branch test can assert it fired.</summary>
  private sealed class RecordingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    private readonly bool _debugEnabled;

    public RecordingLogger(bool debugEnabled) {
      _debugEnabled = debugEnabled;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug || _debugEnabled;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>Seeds a wh_event_store row so cursor last_event_id points at a real event.</summary>
  private static async Task _seedEventStoreRowAsync(
      NpgsqlConnection conn, Guid eventId, Guid streamId, int version) {
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type,
         event_data, metadata, scope, created_at)
      VALUES (@id, @stream, @stream, 'TestAgg', @version, 'Test.Checkpointed, Test',
              '{""amount"": 1}'::jsonb, '{}'::jsonb, '{}'::jsonb, NOW())", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("version", version);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(Guid streamId, Guid eventId)> _seedStreamWithEventAsync(
      NpgsqlConnection conn, int version = 1) {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _seedEventStoreRowAsync(conn, eventId, streamId, version);
    return (streamId, eventId);
  }

  private static async Task<(short? status, Guid? lastEventId, string? error, Guid? rewindTrigger)> _readCursorAsync(
      NpgsqlConnection conn, Guid streamId, string perspectiveName) {
    await using var cmd = new NpgsqlCommand(@"
      SELECT status, last_event_id, error, rewind_trigger_event_id
      FROM wh_perspective_cursors
      WHERE stream_id = @stream AND perspective_name = @persp", conn);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("persp", perspectiveName);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (null, null, null, null);
    }
    var status = reader.GetInt16(0);
    var lastEventId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
    var error = reader.IsDBNull(2) ? null : reader.GetString(2);
    var rewind = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
    return (status, lastEventId, error, rewind);
  }

  [Test]
  public async Task Constructor_WithNullConnectionString_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new DapperPostgresPerspectiveCheckpointCompleter(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CompleteAsync_WithNullCompletions_ThrowsArgumentNullExceptionAsync() {
    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);

    await Assert.That(async () => await completer.CompleteAsync(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CompleteAsync_WithEmptyList_ReturnsWithoutTouchingDatabaseAsync() {
    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);

    // Empty list short-circuits before opening a connection — must not throw and must not
    // create any cursor rows.
    await completer.CompleteAsync([]);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_perspective_cursors", conn);
    var count = (long)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteAsync_WithSingleCompletion_InsertsCursorRowAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (streamId, eventId) = await _seedStreamWithEventAsync(conn);

    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);
    await completer.CompleteAsync([
      new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = PERSPECTIVE_NAME,
        LastEventId = eventId,
        Status = PerspectiveProcessingStatus.Completed
      }
    ]);

    var (status, lastEventId, error, rewind) = await _readCursorAsync(conn, streamId, PERSPECTIVE_NAME);
    await Assert.That(status).IsEqualTo((short)PerspectiveProcessingStatus.Completed);
    await Assert.That(lastEventId).IsEqualTo(eventId);
    await Assert.That(error).IsNull();
    await Assert.That(rewind).IsNull();
  }

  [Test]
  public async Task CompleteAsync_WithMultipleCompletions_InsertsAllRowsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var a = await _seedStreamWithEventAsync(conn);
    var b = await _seedStreamWithEventAsync(conn);
    var c = await _seedStreamWithEventAsync(conn);

    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);
    await completer.CompleteAsync([
      new PerspectiveCursorCompletion { StreamId = a.streamId, PerspectiveName = PERSPECTIVE_NAME, LastEventId = a.eventId, Status = PerspectiveProcessingStatus.Completed },
      new PerspectiveCursorCompletion { StreamId = b.streamId, PerspectiveName = PERSPECTIVE_NAME, LastEventId = b.eventId, Status = PerspectiveProcessingStatus.Completed },
      new PerspectiveCursorCompletion { StreamId = c.streamId, PerspectiveName = PERSPECTIVE_NAME, LastEventId = c.eventId, Status = PerspectiveProcessingStatus.Completed }
    ]);

    foreach (var (streamId, eventId) in new[] { a, b, c }) {
      var (status, lastEventId, _, _) = await _readCursorAsync(conn, streamId, PERSPECTIVE_NAME);
      await Assert.That(status).IsEqualTo((short)PerspectiveProcessingStatus.Completed);
      await Assert.That(lastEventId).IsEqualTo(eventId);
    }
  }

  [Test]
  public async Task CompleteAsync_WithEmptyGuidLastEventId_SkipsThatCompletionAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (goodStream, goodEvent) = await _seedStreamWithEventAsync(conn);
    var skippedStream = (Guid)TrackedGuid.NewMedo();

    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);
    await completer.CompleteAsync([
      // Guid.Empty LastEventId — the completer must skip this row (no cursor written).
      new PerspectiveCursorCompletion { StreamId = skippedStream, PerspectiveName = PERSPECTIVE_NAME, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed },
      new PerspectiveCursorCompletion { StreamId = goodStream, PerspectiveName = PERSPECTIVE_NAME, LastEventId = goodEvent, Status = PerspectiveProcessingStatus.Completed }
    ]);

    var skipped = await _readCursorAsync(conn, skippedStream, PERSPECTIVE_NAME);
    await Assert.That(skipped.status).IsNull();

    var good = await _readCursorAsync(conn, goodStream, PERSPECTIVE_NAME);
    await Assert.That(good.status).IsEqualTo((short)PerspectiveProcessingStatus.Completed);
    await Assert.That(good.lastEventId).IsEqualTo(goodEvent);
  }

  [Test]
  public async Task CompleteAsync_WithAllEmptyGuidCompletions_WritesNoRowsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var streamId = (Guid)TrackedGuid.NewMedo();

    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);
    // Non-empty list but every completion is skipped — the transaction commits with no inserts.
    await completer.CompleteAsync([
      new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = PERSPECTIVE_NAME, LastEventId = Guid.Empty, Status = PerspectiveProcessingStatus.Completed }
    ]);

    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_perspective_cursors", conn);
    var count = (long)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(count).IsEqualTo(0L);
  }

  [Test]
  public async Task CompleteAsync_WithExistingCursor_UpdatesAndClearsErrorAndRewindColumnsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (streamId, firstEvent) = await _seedStreamWithEventAsync(conn, version: 1);
    var secondEvent = (Guid)TrackedGuid.NewMedo();
    await _seedEventStoreRowAsync(conn, secondEvent, streamId, version: 2);

    // Seed a pre-existing cursor stuck in Processing, pointing at the first event, with a stale
    // error message and rewind flags set. The ON CONFLICT DO UPDATE arm must overwrite status /
    // last_event_id and NULL out error + all three rewind columns.
    await using (var seed = new NpgsqlCommand(@"
      INSERT INTO wh_perspective_cursors
        (stream_id, perspective_name, last_event_id, status, processed_at, error,
         rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at)
      VALUES (@stream, @persp, @first, @status, NOW() - INTERVAL '1 hour', @err,
              @first, NOW() - INTERVAL '1 hour', NOW() - INTERVAL '1 hour')", conn)) {
      seed.Parameters.AddWithValue("stream", streamId);
      seed.Parameters.AddWithValue("persp", PERSPECTIVE_NAME);
      seed.Parameters.AddWithValue("first", firstEvent);
      seed.Parameters.AddWithValue("status", (short)PerspectiveProcessingStatus.Processing);
      seed.Parameters.AddWithValue("err", "prior worker crashed mid-processing");
      await seed.ExecuteNonQueryAsync();
    }

    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);
    await completer.CompleteAsync([
      new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = PERSPECTIVE_NAME,
        LastEventId = secondEvent,
        Status = PerspectiveProcessingStatus.Completed
      }
    ]);

    var (status, lastEventId, error, rewind) = await _readCursorAsync(conn, streamId, PERSPECTIVE_NAME);
    await Assert.That(status).IsEqualTo((short)PerspectiveProcessingStatus.Completed);
    await Assert.That(lastEventId).IsEqualTo(secondEvent);
    await Assert.That(error).IsNull();
    await Assert.That(rewind).IsNull();

    // Confirm the remaining rewind columns are cleared too.
    await using var rewindCheck = new NpgsqlCommand(@"
      SELECT rewind_flagged_at, rewind_first_flagged_at
      FROM wh_perspective_cursors WHERE stream_id = @stream AND perspective_name = @persp", conn);
    rewindCheck.Parameters.AddWithValue("stream", streamId);
    rewindCheck.Parameters.AddWithValue("persp", PERSPECTIVE_NAME);
    await using var reader = await rewindCheck.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.IsDBNull(0)).IsTrue();
    await Assert.That(reader.IsDBNull(1)).IsTrue();
  }

  [Test]
  public async Task CompleteAsync_WithDebugLoggingEnabled_EmitsPersistedLogAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (streamId, eventId) = await _seedStreamWithEventAsync(conn);

    var logger = new RecordingLogger<DapperPostgresPerspectiveCheckpointCompleter>(debugEnabled: true);
    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString, logger);

    await completer.CompleteAsync([
      new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = PERSPECTIVE_NAME, LastEventId = eventId, Status = PerspectiveProcessingStatus.Completed }
    ]);

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("checkpoint"))).IsTrue();
  }

  [Test]
  public async Task CompleteAsync_WithDebugLoggingDisabled_DoesNotEmitLogAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (streamId, eventId) = await _seedStreamWithEventAsync(conn);

    var logger = new RecordingLogger<DapperPostgresPerspectiveCheckpointCompleter>(debugEnabled: false);
    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString, logger);

    await completer.CompleteAsync([
      new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = PERSPECTIVE_NAME, LastEventId = eventId, Status = PerspectiveProcessingStatus.Completed }
    ]);

    await Assert.That(logger.Entries.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CompleteAsync_WhenInsertFails_RollsBackAndRethrowsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var (goodStream, goodEvent) = await _seedStreamWithEventAsync(conn);

    // perspective_name is NOT NULL; passing null makes the second command throw inside the
    // transaction, exercising the catch → RollbackAsync → rethrow path. The first (valid)
    // insert must be rolled back, so NO cursor rows exist afterward.
    var completer = new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString);
    await Assert.That(async () => await completer.CompleteAsync([
      new PerspectiveCursorCompletion { StreamId = goodStream, PerspectiveName = PERSPECTIVE_NAME, LastEventId = goodEvent, Status = PerspectiveProcessingStatus.Completed },
      new PerspectiveCursorCompletion { StreamId = goodStream, PerspectiveName = null!, LastEventId = goodEvent, Status = PerspectiveProcessingStatus.Completed }
    ])).Throws<Exception>();

    await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM wh_perspective_cursors", conn);
    var count = (long)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(count).IsEqualTo(0L);
  }
}
