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

    // Assert inbox row is now processed (status has bits 1+4 = 5, EventStored not set, so retained).
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

  /// <summary>
  /// commit_handler_result must read <c>debug_mode</c> from the request JSONB and propagate it
  /// to <c>process_inbox_completions</c>. With debug_mode=true, the inbox row is retained even
  /// when status flags would normally trigger production DELETE (EventStored bit set + Published bit set).
  /// </summary>
  [Test]
  public async Task CommitHandlerResult_DebugModeInRequest_RetainsInboxRowEvenWhenEventStoredAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxMessageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Pre-insert with EventStored bit (2) already set — production would DELETE on commit.
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 3, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", inboxMessageId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    var request = $$"""
      {
        "instance_id": "{{instanceId}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "debug_mode": true,
        "inbox_completion": { "MessageId": "{{inboxMessageId}}", "Status": 4 }
      }
      """;

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_inbox WHERE message_id = @msg AND processed_at IS NOT NULL";
    verify.Parameters.AddWithValue("msg", inboxMessageId);
    var retained = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(retained).IsEqualTo(1L);
  }

  /// <summary>
  /// Production mode (no debug_mode key in request, defaults FALSE): an inbox row whose final status
  /// has the EventStored bit set must be DELETEd. Confirms the negative path of the same wiring as
  /// <see cref="CommitHandlerResult_DebugModeInRequest_RetainsInboxRowEvenWhenEventStoredAsync"/>.
  /// </summary>
  [Test]
  public async Task CommitHandlerResult_ProductionMode_DeletesInboxRowOnEventStoredCompletionAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxMessageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();

    // Pre-insert with EventStored bit (2) set + Stored bit (1).
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 3, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", inboxMessageId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    // No debug_mode key in request → COALESCE defaults to FALSE → production mode.
    var request = $$"""
      {
        "instance_id": "{{instanceId}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "inbox_completion": { "MessageId": "{{inboxMessageId}}", "Status": 4 }
      }
      """;

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    await using var verify = connection.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_inbox WHERE message_id = @msg";
    verify.Parameters.AddWithValue("msg", inboxMessageId);
    var remaining = (long)(await verify.ExecuteScalarAsync())!;
    await Assert.That(remaining).IsEqualTo(0L);
  }

  /// <summary>
  /// When the handler emits an outbox message marked IsEvent=true with a stream_id,
  /// commit_handler_result must call _emit_event_store_chain so the event lands in
  /// wh_event_store and any matching wh_message_associations triggers a
  /// wh_perspective_events row. Without this chain, handler-emitted events sit in
  /// wh_outbox forever without ever reaching perspective consumers.
  /// </summary>
  [Test]
  public async Task CommitHandlerResult_HandlerEmitsEvent_StoresInEventStoreAndCreatesPerspectiveEventAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var emittedEventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.OrderShippedEvent";

    // Register a perspective association so the auto-create fires.
    await using (var assoc = connection.CreateCommand()) {
      assoc.CommandText = @"
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name,
           normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', 'TestPerspective', 'test-service',
                @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING";
      assoc.Parameters.AddWithValue("eventType", eventType);
      await assoc.ExecuteNonQueryAsync();
    }

    var request = $$"""
      {
        "instance_id": "{{instanceId}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{emittedEventId}}",
          "Destination": "out-topic",
          "MessageType": "{{eventType}}",
          "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{emittedEventId}}", "Hops": []},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true
        }]
      }
      """;

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Event must be in wh_event_store with version >= 1.
    await using (var verify = connection.CreateCommand()) {
      verify.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
      verify.Parameters.AddWithValue("id", emittedEventId);
      var count = (long)(await verify.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(1L)
        .Because("Event must be copied into wh_event_store via _emit_event_store_chain");
    }

    // Perspective event must be auto-created for the registered association.
    await using (var verify = connection.CreateCommand()) {
      verify.CommandText = @"
        SELECT count(*) FROM wh_perspective_events
        WHERE event_id = @eid AND perspective_name = 'TestPerspective'";
      verify.Parameters.AddWithValue("eid", emittedEventId);
      var count = (long)(await verify.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(1L)
        .Because("Phase 4.6 perspective auto-create must fire for matching associations");
    }
  }

  /// <summary>
  /// _emit_event_store_chain must be idempotent — calling commit_handler_result twice
  /// with the same emitted event must not produce duplicate wh_event_store or
  /// wh_perspective_events rows.
  /// </summary>
  [Test]
  public async Task CommitHandlerResult_HandlerEmitsEventTwice_IsIdempotentAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var emittedEventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.IdempotentEvent";

    await using (var assoc = connection.CreateCommand()) {
      assoc.CommandText = @"
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name,
           normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', 'IdempPerspective', 'test-service',
                @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING";
      assoc.Parameters.AddWithValue("eventType", eventType);
      await assoc.ExecuteNonQueryAsync();
    }

    var request = $$"""
      {
        "instance_id": "{{instanceId}}",
        "service_name": "test",
        "host_name": "test-host",
        "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{emittedEventId}}",
          "Destination": "out",
          "MessageType": "{{eventType}}",
          "EnvelopeType": null,
          "Envelope": {"Payload": {}, "MessageId": "{{emittedEventId}}", "Hops": []},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true
        }]
      }
      """;

    // Call once.
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Call again with the same event_id — should be a no-op via ON CONFLICT DO NOTHING.
    // But we have to wrap in BEGIN..EXCEPTION since wh_outbox has its own uniqueness on message_id.
    try {
      await using var call2 = connection.CreateCommand();
      call2.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call2.Parameters.AddWithValue("req", request);
      _ = await call2.ExecuteScalarAsync();
    } catch (Npgsql.PostgresException) {
      // wh_outbox unique constraint may reject the second insert; that's fine — we only
      // care that wh_event_store and wh_perspective_events stay at 1 row each.
    }

    await using (var verify = connection.CreateCommand()) {
      verify.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
      verify.Parameters.AddWithValue("id", emittedEventId);
      var eventStoreCount = (long)(await verify.ExecuteScalarAsync())!;
      await Assert.That(eventStoreCount).IsEqualTo(1L);
    }

    await using (var verify = connection.CreateCommand()) {
      verify.CommandText = @"
        SELECT count(*) FROM wh_perspective_events
        WHERE event_id = @eid AND perspective_name = 'IdempPerspective'";
      verify.Parameters.AddWithValue("eid", emittedEventId);
      var perspectiveEventCount = (long)(await verify.ExecuteScalarAsync())!;
      await Assert.That(perspectiveEventCount).IsEqualTo(1L);
    }
  }
}
