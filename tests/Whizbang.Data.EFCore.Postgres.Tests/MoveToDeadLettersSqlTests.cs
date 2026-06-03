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
}
