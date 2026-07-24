using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Audit gap #1 regression locks. The legacy <c>process_work_batch</c> ran completions/failures
/// in a single transaction; the new path splits them across <c>OutboxCompletionFlushWorker</c> and
/// <c>FailureFlushWorker</c>. These tests pin the SQL-level invariants so cross-worker interleaves
/// cannot leave a row in a bad state regardless of which call lands first.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class CompletionInterleaveSqlTests : EFCoreTestBase {

  [Test]
  public async Task SuccessThenFailure_ForSameOutboxMessage_FinalStateIsDeletedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _insertOutboxRowAsync(conn, msgId, streamId);

    // Success arrives first (DELETE in production mode).
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT complete_outbox_published(@ids, FALSE)";
      cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { msgId } });
      _ = await cmd.ExecuteScalarAsync();
    }

    // Late failure arrives — must be a no-op (row already gone).
    var failuresJson = $$"""
      [{"MessageId":"{{msgId}}","CompletedStatus":1,"Error":"transient transport error","FailureReason":2}]
      """;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT process_outbox_failures(@p::jsonb, NOW())";
      cmd.Parameters.AddWithValue("p", failuresJson);
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @id";
    verify.Parameters.AddWithValue("id", msgId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L)
      .Because("Success-then-Failure: success deleted the row; late failure must no-op, not resurrect.");
  }

  [Test]
  public async Task FailureThenSuccess_ForSameOutboxMessage_FinalStateIsDeletedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _insertOutboxRowAsync(conn, msgId, streamId);

    // Failure first — increments attempts, sets backoff, but DOES NOT delete or set processed_at.
    var failuresJson = $$"""
      [{"MessageId":"{{msgId}}","CompletedStatus":1,"Error":"transient transport error","FailureReason":2}]
      """;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT process_outbox_failures(@p::jsonb, NOW())";
      cmd.Parameters.AddWithValue("p", failuresJson);
      _ = await cmd.ExecuteScalarAsync();
    }

    // Success arrives — must DELETE the row even though attempts/backoff were set.
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT complete_outbox_published(@ids, FALSE)";
      cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { msgId } });
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @id";
    verify.Parameters.AddWithValue("id", msgId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L)
      .Because("Failure-then-Success: success must override the failure stamp and delete the row.");
  }

  [Test]
  public async Task SuccessThenFailure_DebugMode_RetainsRowWithPublishedAtSetAsync() {
    // Same interleave but in debug mode: success retains the row with published_at; the late
    // failure UPDATE only fires WHERE the row still exists, so it can ALSO update the same row.
    // The published_at filter in eligible_outbox keeps it out of future claims regardless.
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    await _insertOutboxRowAsync(conn, msgId, streamId);

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT complete_outbox_published(@ids, TRUE)";
      cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { msgId } });
      _ = await cmd.ExecuteScalarAsync();
    }

    var failuresJson = $$"""
      [{"MessageId":"{{msgId}}","CompletedStatus":1,"Error":"late","FailureReason":2}]
      """;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT process_outbox_failures(@p::jsonb, NOW())";
      cmd.Parameters.AddWithValue("p", failuresJson);
      _ = await cmd.ExecuteScalarAsync();
    }

    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT published_at IS NOT NULL FROM wh_outbox WHERE message_id = @id";
    verify.Parameters.AddWithValue("id", msgId);
    var publishedAtSet = (bool)(await verify.ExecuteScalarAsync())!;
    await Assert.That(publishedAtSet).IsTrue()
      .Because("Debug-mode success set published_at; late failure UPDATE must not unset it.");
  }

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid messageId, Guid streamId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
    ins.Parameters.AddWithValue("msg", messageId);
    ins.Parameters.AddWithValue("stream", streamId);
    await ins.ExecuteNonQueryAsync();
  }
}
