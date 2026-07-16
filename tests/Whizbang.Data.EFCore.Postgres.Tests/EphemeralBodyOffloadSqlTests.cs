using System;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 072's ephemeral body offload in the emit chain
/// (<c>_emit_event_store_chain</c>). An ephemeral event (EventFlags.Ephemeral, (flags &amp; 8) = 8)
/// stores NULL inline body on <c>wh_event_store</c> and offloads the real body to <c>wh_event_body</c>;
/// a Sourced event keeps its body inline and never touches <c>wh_event_body</c>. Verified against a
/// real Postgres so the migration's SQL (parsed with check_function_bodies=on) is exercised end-to-end.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralBodyOffloadSqlTests : EFCoreTestBase {
  private static string _commitRequest(Guid instanceId, Guid eventId, Guid streamId, string eventType, int flags) => $$"""
    {
      "instance_id": "{{instanceId}}",
      "service_name": "test",
      "host_name": "test-host",
      "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}",
        "Destination": "out-topic",
        "MessageType": "{{eventType}}",
        "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": {{flags}}
      }]
    }
    """;

  [Test]
  public async Task EmitChain_EphemeralEvent_OffloadsBodyToEventBody_NullInlineAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var eventId = Guid.NewGuid();
    var request = _commitRequest(Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.PresencePingEvent", flags: 8);

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Pointer row exists with the ephemeral flag, but the inline body is NULL (offloaded).
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT flags, (event_data IS NULL), (metadata IS NULL) FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await using var r = await v.ExecuteReaderAsync();
      await Assert.That(await r.ReadAsync()).IsTrue().Because("The ephemeral event must still get a wh_event_store pointer row.");
      await Assert.That(r.GetInt32(0)).IsEqualTo(8).Because("flags carries EventFlags.Ephemeral.");
      await Assert.That(r.GetBoolean(1)).IsTrue().Because("Inline event_data is NULL — the body is offloaded.");
      await Assert.That(r.GetBoolean(2)).IsTrue().Because("Inline metadata is NULL — offloaded with the body.");
    }

    // The real body lives in wh_event_body.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT (event_data->>'OrderId') FROM wh_event_body WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      var orderId = (string?)await v.ExecuteScalarAsync();
      await Assert.That(orderId).IsEqualTo("42").Because("The ephemeral body (the extracted Payload) is stored in wh_event_body.");
    }
  }

  [Test]
  public async Task EmitChain_SourcedEvent_KeepsBodyInline_NoEventBodyRowAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var eventId = Guid.NewGuid();
    var request = _commitRequest(Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.OrderPlacedEvent", flags: 0);

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Sourced: inline body present, and NOTHING in wh_event_body.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT (event_data->>'OrderId') FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      var inline = (string?)await v.ExecuteScalarAsync();
      await Assert.That(inline).IsEqualTo("42").Because("Sourced events keep their body inline in wh_event_store — the durable path is untouched.");
    }

    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_event_body WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      var count = (long)(await v.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(0L).Because("Sourced events never write to wh_event_body.");
    }
  }

  [Test]
  public async Task FetchEventsByIds_EphemeralEvent_CoalescesOffloadedBodyBackAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var eventId = Guid.NewGuid();
    var request = _commitRequest(Guid.NewGuid(), eventId, Guid.NewGuid(), "Whizbang.Tests.PresencePingEvent", flags: 8);
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // fetch_events_by_ids must COALESCE the offloaded body back from wh_event_body (inline is NULL).
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT (out_event_data::jsonb ->> 'OrderId') FROM fetch_events_by_ids(ARRAY[@id]::uuid[])";
      v.Parameters.AddWithValue("id", eventId);
      var orderId = (string?)await v.ExecuteScalarAsync();
      await Assert.That(orderId).IsEqualTo("42")
        .Because("The read path COALESCEs the ephemeral body from wh_event_body — an ephemeral event is consumable end-to-end.");
    }
  }

  [Test]
  public async Task GetStreamEvents_EphemeralEvent_CoalescesOffloadedBodyBackAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    const string eventType = "Whizbang.Tests.TypingIndicatorEvent";

    // Register a perspective association so commit_handler_result creates a wh_perspective_events row.
    await using (var assoc = connection.CreateCommand()) {
      assoc.CommandText = @"
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name,
           normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @t, 'perspective', 'TypingPerspective', 'test-service', @t, NOW(), NOW())
        ON CONFLICT DO NOTHING";
      assoc.Parameters.AddWithValue("t", eventType);
      await assoc.ExecuteNonQueryAsync();
    }

    var request = _commitRequest(instanceId, eventId, streamId, eventType, flags: 8);
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Stamp commit_sequence so get_stream_events' unstamped-row grace gate lets the row through immediately.
    await using (var stamp = connection.CreateCommand()) {
      stamp.CommandText = "UPDATE wh_event_store SET commit_sequence = 1 WHERE event_id = @id";
      stamp.Parameters.AddWithValue("id", eventId);
      await stamp.ExecuteNonQueryAsync();
    }

    // get_stream_events (the perspective-apply read) must return the COALESCE'd ephemeral body.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = @"
        SELECT (out_event_data::jsonb ->> 'OrderId')
        FROM get_stream_events(@inst, ARRAY[@sid]::uuid[], NOW(), 300)
        WHERE out_event_id = @id";
      v.Parameters.AddWithValue("inst", instanceId);
      v.Parameters.AddWithValue("sid", streamId);
      v.Parameters.AddWithValue("id", eventId);
      var orderId = (string?)await v.ExecuteScalarAsync();
      await Assert.That(orderId).IsEqualTo("42")
        .Because("The perspective-apply read path COALESCEs the ephemeral body from wh_event_body.");
    }
  }
}
