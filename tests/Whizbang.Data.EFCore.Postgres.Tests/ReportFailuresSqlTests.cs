using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>report_failures</c> — category-aware batched failure reporter.
/// Routes to the appropriate underlying process_*_failures sub-function based on
/// p_category. Coalesced flush from the C# FailureFlushWorker.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
[Category("Shard2")]
public class ReportFailuresSqlTests : EFCoreTestBase {

  [Test]
  public async Task ReportFailures_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='report_failures' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task ReportFailures_OutboxCategory_IncrementsAttemptsAndSetsErrorAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var msgId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(), @stream, 0)";
      ins.Parameters.AddWithValue("msg", msgId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    var failuresJson = $$"""
      [{"MessageId": "{{msgId}}", "CompletedStatus": 8, "Error": "transport publish exploded", "FailureReason": 2}]
      """;

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT report_failures('outbox', @failures::jsonb)";
      call.Parameters.AddWithValue("failures", failuresJson);
      _ = await call.ExecuteScalarAsync();
    }

    // Phase H step 8 — claim_orphaned_* is sole attempt counter; failures don't bump.
    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT attempts, error FROM wh_outbox WHERE message_id = @msg";
    verify.Parameters.AddWithValue("msg", msgId);
    await using var reader = await verify.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    var attempts = reader.GetInt32(0);
    var error = reader.IsDBNull(1) ? null : reader.GetString(1);
    await Assert.That(attempts).IsEqualTo(0);
    await Assert.That(error).IsEqualTo("transport publish exploded");
  }

  [Test]
  public async Task ReportFailures_UnknownCategory_RaisesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT report_failures('not-a-real-category', '[{\"MessageId\":\"00000000-0000-0000-0000-000000000000\",\"CompletedStatus\":8,\"Error\":\"x\",\"FailureReason\":1}]'::jsonb)";
    var threw = false;
    try {
      _ = await call.ExecuteScalarAsync();
    } catch (PostgresException) {
      threw = true;
    }
    await Assert.That(threw).IsTrue();
  }
}
