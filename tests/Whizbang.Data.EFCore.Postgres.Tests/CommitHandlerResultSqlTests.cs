using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for the new <c>commit_handler_result</c> SQL function — the atomic transactional bundle
/// that combines an inbox handler's completion with the new outbox/inbox messages it emitted.
/// This is the only true transactional unit in the work-pump decomposition.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
public class CommitHandlerResultSqlTests : EFCoreTestBase {

  /// <summary>
  /// Function must exist in the public schema with a single jsonb parameter.
  /// </summary>
  [Test]
  public async Task CommitHandlerResult_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT 1 FROM pg_proc
        WHERE proname = 'commit_handler_result'
          AND pronamespace = 'public'::regnamespace
      );";

    var exists = (bool)(await command.ExecuteScalarAsync())!;

    await Assert.That(exists).IsTrue();
  }

  /// <summary>
  /// When called with an inbox_completion + new_outbox_messages, it must atomically:
  /// (1) mark the inbox row processed_at = NOW(), (2) insert the new outbox rows.
  /// Both effects must be visible after the call returns.
  /// </summary>
  [Test]
  public async Task CommitHandlerResult_HappyPath_MarksInboxProcessedAndStoresOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxMessageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var emittedOutboxMessageId = Guid.NewGuid();

    // Pre-insert the inbox row that the handler is "completing" (claimed by this instance).
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", inboxMessageId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    // Build the request payload: complete this inbox + emit one new outbox message.
    var request = $$"""
      {
        "instance_id": "{{instanceId}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "inbox_completion": {
          "MessageId": "{{inboxMessageId}}",
          "Status": 4
        },
        "new_outbox_messages": [{
          "MessageId": "{{emittedOutboxMessageId}}",
          "Destination": "out-topic",
          "MessageType": "EmittedEvent",
          "EnvelopeType": null,
          "Envelope": {},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": false
        }]
      }
      """;

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Assert inbox row is now processed.
    await using (var verify = connection.CreateCommand()) {
      verify.CommandText = "SELECT processed_at IS NOT NULL FROM wh_inbox WHERE message_id = @msg";
      verify.Parameters.AddWithValue("msg", inboxMessageId);
      var processed = (bool)(await verify.ExecuteScalarAsync())!;
      await Assert.That(processed).IsTrue();
    }

    // Assert new outbox row exists.
    await using (var verify = connection.CreateCommand()) {
      verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @msg";
      verify.Parameters.AddWithValue("msg", emittedOutboxMessageId);
      var count = (long)(await verify.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(1L);
    }
  }
}
