using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// v0.502 slice C.7 foundation — regression locks for the recovery SQL surface:
/// <c>fetch_dead_letters_due</c>, <c>recover_dead_letter</c>,
/// <c>mark_dead_letter_holding</c>, <c>mark_dead_letter_permanently_failed</c>,
/// <c>schedule_next_dead_letter_attempt</c>, <c>reset_dead_letters_for_generation</c>.
/// </summary>
/// <docs>operations/dead-letter-queue/recovery</docs>
public class DeadLetterRecoverySqlTests : EFCoreTestBase {

  [Test]
  public async Task FetchDue_PendingRow_WithNullNextRecoveryAt_IsReturnedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var dlqId = await _moveToDlqAsync(conn, sourceTable: "wh_outbox");

    var due = await _fetchDueAsync(conn, max: 10);

    await Assert.That(due).Contains(dlqId)
      .Because("a Pending row with NULL next_recovery_at is ready immediately");
  }

  [Test]
  public async Task FetchDue_HoldForReview_IsExcludedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var dlqId = await _moveToDlqAsync(conn, sourceTable: "wh_outbox");
    await _callMarkHoldingAsync(conn, dlqId);

    var due = await _fetchDueAsync(conn, max: 10);

    await Assert.That(due).DoesNotContain(dlqId)
      .Because("HoldForReview rows must never enter the recovery loop");
  }

  [Test]
  public async Task FetchDue_PermanentlyFailed_IsExcludedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var dlqId = await _moveToDlqAsync(conn, sourceTable: "wh_outbox");
    await _callMarkPermanentlyFailedAsync(conn, dlqId);

    var due = await _fetchDueAsync(conn, max: 10);

    await Assert.That(due).DoesNotContain(dlqId);
  }

  [Test]
  public async Task FetchDue_FutureNextRecoveryAt_IsExcludedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var dlqId = await _moveToDlqAsync(conn, sourceTable: "wh_outbox");
    await _callScheduleNextAsync(conn, dlqId, DateTimeOffset.UtcNow.AddHours(1));

    var due = await _fetchDueAsync(conn, max: 10);

    await Assert.That(due).DoesNotContain(dlqId)
      .Because("rows scheduled for the future shouldn't be returned yet");
  }

  [Test]
  public async Task RecoverDeadLetter_OutboxRow_InsertsBackIntoOutboxAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var (dlqId, originalMessageId) = await _moveToDlqWithMessageIdAsync(conn, sourceTable: "wh_outbox");

    var recovered = await _callRecoverAsync(conn, dlqId);

    await Assert.That(recovered).IsTrue();
    await Assert.That(await _outboxRowExistsAsync(conn, originalMessageId)).IsTrue()
      .Because("recovery re-inserts the row into wh_outbox with the original message_id");
    var status = await _readRecoveryStatusAsync(conn, dlqId);
    await Assert.That(status).IsEqualTo(3).Because("recovered_at is set + status=Recovered");
  }

  [Test]
  public async Task RecoverDeadLetter_AlreadyRecovered_ReturnsFalseAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var (dlqId, _) = await _moveToDlqWithMessageIdAsync(conn, sourceTable: "wh_outbox");
    var first = await _callRecoverAsync(conn, dlqId);
    var second = await _callRecoverAsync(conn, dlqId);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("re-recovering a recovered row must no-op (idempotent under retry)");
  }

  [Test]
  public async Task ResetForGeneration_NeverRetriedOnCurrentGeneration_SchedulesRowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var dlqId = await _moveToDlqAsync(conn, sourceTable: "wh_outbox", generation: "v0.501");
    await _callScheduleNextAsync(conn, dlqId, DateTimeOffset.UtcNow.AddHours(1));  // would be future

    var count = await _callResetForGenerationAsync(conn, "v0.502");

    await Assert.That(count).IsGreaterThanOrEqualTo(1)
      .Because("the row's generation v0.501 hasn't seen v0.502 yet → schedule for replay");

    // After the reset, the row's next_recovery_at is back to <= NOW so fetch_due returns it.
    var due = await _fetchDueAsync(conn, max: 10);
    await Assert.That(due).Contains(dlqId);
  }

  [Test]
  public async Task ResetForGeneration_AlreadyRetriedOnCurrentGeneration_NoOpAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var dlqId = await _moveToDlqAsync(conn, sourceTable: "wh_outbox", generation: "v0.502");

    // First reset includes this row.
    await _callResetForGenerationAsync(conn, "v0.502");
    // Second reset should NOT include it again — guarantees exactly-once replay per generation.
    var count = await _callResetForGenerationAsync(conn, "v0.502");

    await Assert.That(count).IsEqualTo(0)
      .Because("once a generation has replayed a row, it must not replay again");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<Guid> _moveToDlqAsync(
      NpgsqlConnection conn, string sourceTable, string generation = "v0.502") {
    var (dlqId, _) = await _moveToDlqWithMessageIdAsync(conn, sourceTable, generation);
    return dlqId;
  }

  private static async Task<(Guid DlqId, Guid OriginalMessageId)> _moveToDlqWithMessageIdAsync(
      NpgsqlConnection conn, string sourceTable, string generation = "v0.502") {
    var dlqId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();

    if (sourceTable == "wh_outbox") {
      await using var ins = conn.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
           created_at, stream_id, partition_number)
        VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", messageId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    await using var move = conn.CreateCommand();
    move.CommandText = "SELECT move_to_dead_letters(@dlq, @tbl, @src, @reason, @err, @inst, @gen)";
    move.Parameters.AddWithValue("dlq", dlqId);
    move.Parameters.AddWithValue("tbl", sourceTable);
    move.Parameters.AddWithValue("src", messageId);
    move.Parameters.AddWithValue("reason", 5);
    move.Parameters.AddWithValue("err", "test");
    move.Parameters.AddWithValue("inst", (Guid)TrackedGuid.NewMedo());
    move.Parameters.AddWithValue("gen", generation);
    await move.ExecuteNonQueryAsync();
    return (dlqId, messageId);
  }

  private static async Task<List<Guid>> _fetchDueAsync(NpgsqlConnection conn, int max) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT dead_letter_id FROM fetch_dead_letters_due(NOW(), @max)";
    cmd.Parameters.AddWithValue("max", max);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<bool> _callRecoverAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recover_dead_letter(@id)";
    cmd.Parameters.AddWithValue("id", dlqId);
    var result = await cmd.ExecuteScalarAsync();
    return result is bool b && b;
  }

  private static async Task _callMarkHoldingAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT mark_dead_letter_holding(@id)";
    cmd.Parameters.AddWithValue("id", dlqId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callMarkPermanentlyFailedAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT mark_dead_letter_permanently_failed(@id)";
    cmd.Parameters.AddWithValue("id", dlqId);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _callScheduleNextAsync(NpgsqlConnection conn, Guid dlqId, DateTimeOffset nextAt) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT schedule_next_dead_letter_attempt(@id, @at)";
    cmd.Parameters.AddWithValue("id", dlqId);
    cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = nextAt });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _callResetForGenerationAsync(NpgsqlConnection conn, string generation) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT reset_dead_letters_for_generation(@gen)";
    cmd.Parameters.AddWithValue("gen", generation);
    var result = await cmd.ExecuteScalarAsync();
    return result is int n ? n : 0;
  }

  private static async Task<bool> _outboxRowExistsAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1 FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return await cmd.ExecuteScalarAsync() is not null;
  }

  private static async Task<int> _readRecoveryStatusAsync(NpgsqlConnection conn, Guid dlqId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT recovery_status FROM wh_dead_letters WHERE dead_letter_id = @id";
    cmd.Parameters.AddWithValue("id", dlqId);
    var result = await cmd.ExecuteScalarAsync();
    return result is int n ? n : -1;
  }
}
