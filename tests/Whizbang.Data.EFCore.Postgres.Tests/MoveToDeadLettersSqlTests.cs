using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.502 slice C.1 — regression locks for <c>wh_dead_letters</c> + <c>move_to_dead_letters()</c>.
///
/// <para>
/// The DLQ persistence layer's foundation: atomic insert-into-DLQ + delete-from-source. These
/// tests lock that the move is genuinely atomic (no partial state across the failure path),
/// that all three source tables (wh_outbox / wh_inbox / wh_perspective_events) are supported,
/// and that the function is idempotent under retry (already-DLQ'd source returns NULL no-op).
/// </para>
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class MoveToDeadLettersSqlTests : EFCoreTestBase {

  [Test]
  public async Task MoveToDeadLetters_OutboxRow_MovesIntoDlqAndDeletesSourceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var instanceId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId, streamId, attempts: 7);

    var resultId = await _callMoveAsync(conn, dlqId, "wh_outbox", messageId,
      failureReason: 8, errorText: "test throttle exhausted", instanceId: instanceId,
      generation: "0.502.0-alpha.1+test");

    await Assert.That(resultId).IsEqualTo(dlqId)
      .Because("function should return the dead_letter_id on successful move");

    // Source row is gone
    await Assert.That(await _outboxRowExistsAsync(conn, messageId)).IsFalse()
      .Because("atomic move must DELETE from source after inserting into wh_dead_letters");

    // DLQ row is there with correct shape
    var dlq = await _readDlqRowAsync(conn, dlqId);
    await Assert.That(dlq.SourceTable).IsEqualTo("wh_outbox");
    await Assert.That(dlq.SourceId).IsEqualTo(messageId);
    await Assert.That(dlq.StreamId).IsEqualTo(streamId);
    await Assert.That(dlq.FailureReason).IsEqualTo(8);
    await Assert.That(dlq.AttemptsWhenDlq).IsEqualTo(7);
    await Assert.That(dlq.DeadLetteredBy).IsEqualTo(instanceId);
    await Assert.That(dlq.Generation).IsEqualTo("0.502.0-alpha.1+test");
    await Assert.That(dlq.RecoveryStatus).IsEqualTo(0).Because("default Pending status");
    await Assert.That(dlq.RecoveryAttempts).IsEqualTo(0);
  }

  [Test]
  public async Task MoveToDeadLetters_InboxRow_MovesIntoDlqAndDeletesSourceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 11);

    var resultId = await _callMoveAsync(conn, dlqId, "wh_inbox", messageId,
      failureReason: 2, errorText: "transport exception",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "g1");

    await Assert.That(resultId).IsEqualTo(dlqId);
    await Assert.That(await _inboxRowExistsAsync(conn, messageId)).IsFalse();

    var dlq = await _readDlqRowAsync(conn, dlqId);
    await Assert.That(dlq.SourceTable).IsEqualTo("wh_inbox");
    await Assert.That(dlq.AttemptsWhenDlq).IsEqualTo(11);
    await Assert.That(dlq.FailureReason).IsEqualTo(2);
  }

  [Test]
  public async Task MoveToDeadLetters_PerspectiveEventRow_MovesIntoDlqAndDeletesSourceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventAsync(conn, workId, streamId, "Test.Projection", eventId, attempts: 3);

    var resultId = await _callMoveAsync(conn, dlqId, "wh_perspective_events", workId,
      failureReason: 4, errorText: "validation error",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "g2");

    await Assert.That(resultId).IsEqualTo(dlqId);
    await Assert.That(await _perspectiveEventExistsAsync(conn, workId)).IsFalse();

    var dlq = await _readDlqRowAsync(conn, dlqId);
    await Assert.That(dlq.SourceTable).IsEqualTo("wh_perspective_events");
    await Assert.That(dlq.PerspectiveName).IsEqualTo("Test.Projection");
    await Assert.That(dlq.AttemptsWhenDlq).IsEqualTo(3);
  }

  [Test]
  public async Task MoveToDeadLetters_AlreadyMovedRow_ReturnsNullAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var nonExistentMessageId = (Guid)TrackedGuid.NewMedo();

    var resultId = await _callMoveAsync(conn, dlqId, "wh_outbox", nonExistentMessageId,
      failureReason: 8, errorText: "would be racy",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "g3");

    await Assert.That(resultId).IsNull()
      .Because("idempotent — when the source row was already removed, return NULL no-op");
  }

  [Test]
  public async Task MoveToDeadLetters_UnknownSourceTable_RaisesExceptionAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();

    await Assert.That(async () => await _callMoveAsync(conn, dlqId, "wh_nonexistent", messageId,
      failureReason: 99, errorText: "x",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "g"))
      .Throws<PostgresException>()
      .Because("unsupported source table must fail loudly, not silently no-op");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private sealed record DlqRow(
    string SourceTable, Guid SourceId, Guid? StreamId, int FailureReason,
    int AttemptsWhenDlq, Guid? DeadLetteredBy, string Generation,
    int RecoveryStatus, int RecoveryAttempts, string? PerspectiveName);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<Guid?> _callMoveAsync(
      NpgsqlConnection conn, Guid dlqId, string sourceTable, Guid sourceId,
      int failureReason, string errorText, Guid instanceId, string generation) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT move_to_dead_letters(@dlq, @tbl, @src, @reason, @err, @inst, @gen)";
    cmd.Parameters.AddWithValue("dlq", dlqId);
    cmd.Parameters.AddWithValue("tbl", sourceTable);
    cmd.Parameters.AddWithValue("src", sourceId);
    cmd.Parameters.AddWithValue("reason", failureReason);
    cmd.Parameters.AddWithValue("err", errorText);
    cmd.Parameters.AddWithValue("inst", instanceId);
    cmd.Parameters.AddWithValue("gen", generation);
    var result = await cmd.ExecuteScalarAsync();
    return result switch {
      null => null,
      DBNull => null,
      _ => (Guid)result,
    };
  }

  private static async Task<DlqRow> _readDlqRowAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT source_table, source_id, stream_id, failure_reason,
             attempts_when_dlq, dead_lettered_by, generation,
             recovery_status, recovery_attempts, perspective_name
      FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", dlqId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException($"No wh_dead_letters row for {dlqId}");
    }
    return new DlqRow(
      reader.GetString(0),
      reader.GetGuid(1),
      reader.IsDBNull(2) ? null : reader.GetGuid(2),
      reader.GetInt32(3),
      reader.GetInt32(4),
      reader.IsDBNull(5) ? null : reader.GetGuid(5),
      reader.GetString(6),
      reader.GetInt32(7),
      reader.GetInt32(8),
      reader.IsDBNull(9) ? null : reader.GetString(9));
  }

  private static async Task<bool> _outboxRowExistsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return await cmd.ExecuteScalarAsync() is not null;
  }

  private static async Task<bool> _inboxRowExistsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM wh_inbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return await cmd.ExecuteScalarAsync() is not null;
  }

  private static async Task<bool> _perspectiveEventExistsAsync(NpgsqlConnection conn, Guid workId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM wh_perspective_events WHERE event_work_id = @id";
    cmd.Parameters.AddWithValue("id", workId);
    return await cmd.ExecuteScalarAsync() is not null;
  }

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, @att,
              NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("att", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertInboxRowAsync(NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number)
      VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, @att, NOW(),
              @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("att", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task _insertPerspectiveEventAsync(
      NpgsqlConnection conn, Guid eventWorkId, Guid streamId, string perspectiveName, Guid eventId, int attempts) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at)
      VALUES (@work, @stream, @persp, @event, 0, 0, @att, NOW())";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("att", attempts);
    await ins.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task MoveToDeadLetters_PerspectiveRowWithStoredError_CapturesTerminalErrorInErrorTextAsync() {
    // Forensic gap observed live: thousands of perspective dead-letters carried ONLY the
    // "attempts=N > max=M" wrapper; the terminal apply exception (stored on
    // wh_perspective_events.error) was discarded, and once pod logs rotated the root cause was
    // unrecoverable. The move must preserve the source row's stored error.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var workId = (Guid)TrackedGuid.NewMedo();
    await _insertPerspectiveEventWithErrorAsync(conn, workId, (Guid)TrackedGuid.NewMedo(),
      "Test.Projection", (Guid)TrackedGuid.NewMedo(), attempts: 12,
      error: "System.InvalidOperationException: the actual root cause");

    _ = await _callMoveAsync(conn, dlqId, "wh_perspective_events", workId,
      failureReason: 5, errorText: "PerspectiveWorker dead-lettered perspective event: attempts=12 > max=10",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "g3");

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT error_text FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", dlqId);
    var errorText = (string)(await cmd.ExecuteScalarAsync())!;
    await Assert.That(errorText).Contains("the actual root cause")
      .Because("the stored terminal apply error must survive into the DLQ row");
    await Assert.That(errorText).Contains("attempts=12 > max=10")
      .Because("the promotion wrapper stays too — it carries the attempts context");
  }

  private static async Task _insertPerspectiveEventWithErrorAsync(
      NpgsqlConnection conn, Guid eventWorkId, Guid streamId, string perspectiveName, Guid eventId,
      int attempts, string error) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at, error)
      VALUES (@work, @stream, @persp, @event, 0, 0, @att, NOW(), @err)";
    ins.Parameters.AddWithValue("work", eventWorkId);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("persp", perspectiveName);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.AddWithValue("err", error);
    await ins.ExecuteNonQueryAsync();
  }

  // ============================================================================
  // Issue #518 — the retry budget must survive re-dead-lettering.
  //
  // move_to_dead_letters mints a NEW dead_letter_id every time a message fails, and the
  // recovery worker's exhaustion check reads recovery_attempts off THAT row. So a message
  // that fails again after recovery starts from zero, HoldForReview can never engage, and
  // one poison message cycles forever (observed: 257 dead-letters of a single message in
  // 15 minutes, 46k rows from 7.6k distinct messages). The budget must key on the MESSAGE.
  // ============================================================================

  [Test]
  public async Task MoveToDeadLetters_SameMessageDeadLetteredTwice_CarriesRecoveryBudgetForwardAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    // First failure → row A, then recovery spends one attempt on it.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 11);
    var firstDlqId = (Guid)TrackedGuid.NewMedo();
    _ = await _callMoveAsync(conn, firstDlqId, "wh_inbox", messageId,
      failureReason: 5, errorText: "attempts=11 > max=10",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "gen-1");
    await using (var spend = conn.CreateCommand()) {
      spend.CommandText = "UPDATE wh_dead_letters SET recovery_attempts = 1, recovery_status = 3, recovered_at = NOW() WHERE dead_letter_id = @id";
      spend.Parameters.AddWithValue("id", firstDlqId);
      await spend.ExecuteNonQueryAsync();
    }

    // Recovery re-emitted it; it fails AGAIN → row B.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 11);
    var secondDlqId = (Guid)TrackedGuid.NewMedo();
    _ = await _callMoveAsync(conn, secondDlqId, "wh_inbox", messageId,
      failureReason: 5, errorText: "attempts=11 > max=10",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "gen-1");

    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT recovery_attempts FROM wh_dead_letters WHERE dead_letter_id = @id";
    read.Parameters.AddWithValue("id", secondDlqId);
    var carried = (int)(await read.ExecuteScalarAsync())!;

    await Assert.That(carried).IsGreaterThanOrEqualTo(1)
      .Because("the retry budget belongs to the MESSAGE, not to one dead-letter row — a fresh "
             + "row starting at zero makes HoldForReviewAfterExhaustion unreachable and lets a "
             + "poison message cycle forever (issue #518)");
  }

  [Test]
  public async Task MoveToDeadLetters_FirstFailureOfAMessage_StartsBudgetAtZeroAsync() {
    // Guard: carrying history forward must not penalise a message failing for the first time.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertInboxRowAsync(conn, messageId, (Guid)TrackedGuid.NewMedo(), attempts: 11);
    var dlqId = (Guid)TrackedGuid.NewMedo();

    _ = await _callMoveAsync(conn, dlqId, "wh_inbox", messageId,
      failureReason: 5, errorText: "first failure",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "gen-1");

    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT recovery_attempts FROM wh_dead_letters WHERE dead_letter_id = @id";
    read.Parameters.AddWithValue("id", dlqId);
    await Assert.That((int)(await read.ExecuteScalarAsync())!).IsEqualTo(0)
      .Because("a message with no prior dead-letter history gets its full retry budget");
  }

  [Test]
  public async Task MoveToDeadLetters_InboxRowWithStoredError_CapturesItInErrorTextAsync() {
    // Symmetric to the perspective-branch capture: when the inbox row carries the terminal
    // error, it must survive into the DLQ row instead of being replaced by the attempts wrapper.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertInboxRowWithErrorAsync(conn, messageId, (Guid)TrackedGuid.NewMedo(),
      attempts: 11, error: "System.InvalidOperationException: the real inbox cause");
    var dlqId = (Guid)TrackedGuid.NewMedo();

    _ = await _callMoveAsync(conn, dlqId, "wh_inbox", messageId,
      failureReason: 5, errorText: "InboxDispatchWorker dead-lettered: attempts=11 > max=10",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "gen-1");

    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT error_text FROM wh_dead_letters WHERE dead_letter_id = @id";
    read.Parameters.AddWithValue("id", dlqId);
    var errorText = (string)(await read.ExecuteScalarAsync())!;
    await Assert.That(errorText).Contains("the real inbox cause")
      .Because("losing the terminal exception makes root cause unrecoverable once pod logs rotate");
    await Assert.That(errorText).Contains("attempts=11 > max=10");
  }

  private static async Task _insertInboxRowWithErrorAsync(
      NpgsqlConnection conn, Guid messageId, Guid streamId, int attempts, string error) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at, stream_id, partition_number, error)
      VALUES (@msg, 'h', 'Test.Event', '{}'::jsonb, '{}'::jsonb, 0, @att, NOW(), @stream, 0, @err)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("att", attempts);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("err", error);
    await ins.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task MoveToDeadLetters_NewGeneration_RestoresRetryBudgetAsync() {
    // The budget is cumulative WITHIN a generation but resets on a new one — generation-tagged
    // auto-replay ("we shipped a fix, replay the casualties") depends on a previously-exhausted
    // message getting a fresh chance after a deploy.
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 11);
    var oldGenDlq = (Guid)TrackedGuid.NewMedo();
    _ = await _callMoveAsync(conn, oldGenDlq, "wh_inbox", messageId,
      failureReason: 5, errorText: "exhausted on the old build",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "whizbang/1.0.0");
    await using (var spend = conn.CreateCommand()) {
      spend.CommandText = "UPDATE wh_dead_letters SET recovery_attempts = 5 WHERE dead_letter_id = @id";
      spend.Parameters.AddWithValue("id", oldGenDlq);
      await spend.ExecuteNonQueryAsync();
    }

    // A NEW build deploys and the message fails again.
    await _insertInboxRowAsync(conn, messageId, streamId, attempts: 11);
    var newGenDlq = (Guid)TrackedGuid.NewMedo();
    _ = await _callMoveAsync(conn, newGenDlq, "wh_inbox", messageId,
      failureReason: 5, errorText: "first failure on the new build",
      instanceId: (Guid)TrackedGuid.NewMedo(), generation: "whizbang/2.0.0");

    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT recovery_attempts FROM wh_dead_letters WHERE dead_letter_id = @id";
    read.Parameters.AddWithValue("id", newGenDlq);
    await Assert.That((int)(await read.ExecuteScalarAsync())!).IsEqualTo(0)
      .Because("a new build generation is a new chance — carrying the old build's exhausted "
             + "budget forward would silently disable generation-tagged auto-replay");
  }
}

