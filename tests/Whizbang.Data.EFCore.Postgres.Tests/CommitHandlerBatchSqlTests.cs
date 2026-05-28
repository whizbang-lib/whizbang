using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for the new <c>commit_handler_batch</c> SQL function — SAVEPOINT-per-handler
/// batched commit. The throughput multiplier: N handler results in one round-trip,
/// one fsync, with per-handler success/failure isolation via PL/pgSQL SAVEPOINTs.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
public class CommitHandlerBatchSqlTests : EFCoreTestBase {

  /// <summary>
  /// Function must exist with signature (jsonb) RETURNING TABLE per-handler status.
  /// </summary>
  [Test]
  public async Task CommitHandlerBatch_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT 1 FROM pg_proc
        WHERE proname = 'commit_handler_batch'
          AND pronamespace = 'public'::regnamespace
      );";

    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  /// <summary>
  /// Three handler results, all succeed → all three reported success, all three inbox
  /// rows marked processed in a single round-trip and single fsync.
  /// </summary>
  [Test]
  public async Task CommitHandlerBatch_AllSucceed_ReportsAllSuccessAndAppliesAllAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var msgIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

    foreach (var msgId in msgIds) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", msgId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var resultsJson = $$"""
      [
        {"handler_id": "{{Guid.NewGuid()}}", "instance_id": "{{instanceId}}",
         "inbox_completion": {"MessageId": "{{msgIds[0]}}", "Status": 4}, "new_outbox_messages": []},
        {"handler_id": "{{Guid.NewGuid()}}", "instance_id": "{{instanceId}}",
         "inbox_completion": {"MessageId": "{{msgIds[1]}}", "Status": 4}, "new_outbox_messages": []},
        {"handler_id": "{{Guid.NewGuid()}}", "instance_id": "{{instanceId}}",
         "inbox_completion": {"MessageId": "{{msgIds[2]}}", "Status": 4}, "new_outbox_messages": []}
      ]
      """;

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT handler_id, success, error_message FROM commit_handler_batch(@req::jsonb)";
    cmd.Parameters.AddWithValue("req", resultsJson);

    var results = new List<(Guid handlerId, bool success, string? error)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        results.Add((
          reader.GetGuid(0),
          reader.GetBoolean(1),
          reader.IsDBNull(2) ? null : reader.GetString(2)));
      }
    }

    await Assert.That(results.Count).IsEqualTo(3);
    await Assert.That(results.All(r => r.success)).IsTrue();
    await Assert.That(results.All(r => r.error == null)).IsTrue();

    // Verify all three inbox rows are now processed.
    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_inbox WHERE message_id = ANY(@ids) AND processed_at IS NOT NULL";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = msgIds });
    var processedCount = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(processedCount).IsEqualTo(3L);
  }

  /// <summary>
  /// Three handlers, the middle one fails (invalid JSON triggers exception inside its
  /// SAVEPOINT). The other two must succeed and their effects must be visible. The
  /// failure must be reported in the result with success=false and a non-null error_message.
  /// This proves SAVEPOINT isolation actually works.
  /// </summary>
  [Test]
  public async Task CommitHandlerBatch_OneHandlerFails_OthersSucceedSavepointIsolationAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var msgIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

    foreach (var msgId in msgIds) {
      await using var ins = connection.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", msgId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var failingHandlerId = Guid.NewGuid();
    // Middle handler emits an outbox row with a malformed UUID → triggers exception inside its SAVEPOINT.
    var resultsJson = $$"""
      [
        {"handler_id": "{{Guid.NewGuid()}}", "instance_id": "{{instanceId}}",
         "inbox_completion": {"MessageId": "{{msgIds[0]}}", "Status": 4}, "new_outbox_messages": []},
        {"handler_id": "{{failingHandlerId}}", "instance_id": "{{instanceId}}",
         "inbox_completion": {"MessageId": "{{msgIds[1]}}", "Status": 4},
         "new_outbox_messages": [{"MessageId": "not-a-valid-uuid", "Destination": "x", "MessageType": "x",
           "Envelope": {}, "Metadata": {}, "Scope": null, "StreamId": null, "IsEvent": false}]},
        {"handler_id": "{{Guid.NewGuid()}}", "instance_id": "{{instanceId}}",
         "inbox_completion": {"MessageId": "{{msgIds[2]}}", "Status": 4}, "new_outbox_messages": []}
      ]
      """;

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT handler_id, success, error_message FROM commit_handler_batch(@req::jsonb)";
    cmd.Parameters.AddWithValue("req", resultsJson);

    var results = new List<(Guid handlerId, bool success, string? error)>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        results.Add((
          reader.GetGuid(0),
          reader.GetBoolean(1),
          reader.IsDBNull(2) ? null : reader.GetString(2)));
      }
    }

    await Assert.That(results.Count).IsEqualTo(3);
    var (handlerId, success, error) = results.SingleOrDefault(r => r.handlerId == failingHandlerId);
    await Assert.That(success).IsFalse();
    await Assert.That(error).IsNotNull();
    await Assert.That(results.Where(r => r.handlerId != failingHandlerId).All(r => r.success)).IsTrue();

    // Verify SAVEPOINT isolation: handlers 0 and 2 applied; handler 1 (failing) did NOT.
    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT message_id, processed_at IS NOT NULL FROM wh_inbox WHERE message_id = ANY(@ids) ORDER BY message_id";
    verify.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = msgIds });
    var states = new Dictionary<Guid, bool>();
    await using (var reader = await verify.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        states[reader.GetGuid(0)] = reader.GetBoolean(1);
      }
    }
    await Assert.That(states[msgIds[0]]).IsTrue();    // handler 0 succeeded
    await Assert.That(states[msgIds[1]]).IsFalse();   // handler 1 failed → savepoint rolled back
    await Assert.That(states[msgIds[2]]).IsTrue();    // handler 2 succeeded
  }
}
