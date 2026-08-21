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
[Category("Shard4")]
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

    // Pointer row exists with the ephemeral flag. (078: the inline body columns no longer exist —
    // the pointer is structurally narrow; the body lives only in wh_event_body.)
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT flags FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await using var r = await v.ExecuteReaderAsync();
      await Assert.That(await r.ReadAsync()).IsTrue().Because("The ephemeral event must still get a wh_event_store pointer row.");
      await Assert.That(r.GetInt32(0)).IsEqualTo(8).Because("flags carries EventFlags.Ephemeral.");
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
  public async Task EmitChain_SourcedEvent_BodyOffloaded_PointerInlineNullAsync() {
    // Full split (#13b4-2 / 077): SOURCED bodies are offloaded to wh_event_body too — the pointer's
    // inline columns are always NULL. (Pre-077 sourced stayed inline; 077 unified the storage.)
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

    // Sourced: body in wh_event_body, pointer inline NULL.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT (event_data->>'OrderId') FROM wh_event_body WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      var bodyValue = (string?)await v.ExecuteScalarAsync();
      await Assert.That(bodyValue).IsEqualTo("42").Because("Post-077 every body — sourced included — lives in wh_event_body.");
    }

    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await Assert.That((long)(await v.ExecuteScalarAsync())!).IsEqualTo(1L)
        .Because("The narrow pointer row exists; post-078 there are no inline body columns at all.");
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

  // ── Composition with the blob/claim-check body offload (transport-wire layer) ──────────────────────
  // The two offloads are orthogonal: blob offload substitutes/rehydrates on the transport wire and never
  // touches the event store; ephemeral offload routes the FULL body to wh_event_body at the storage layer.
  // These lock that invariant at the event-store layer — where a rehydrated (formerly-offloaded) body lands,
  // and that the event store is size-independent (so the size-triggered blob offload cannot be in its path).

  [Test]
  public async Task EmitChainInbox_EphemeralEvent_OffloadsBodyToEventBodyAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    // The inbox path is where a transport-received, rehydrated body lands. Store + lease an ephemeral
    // inbox event, then run the inbox emit chain (the receive-side of _emit_event_store_chain).
    var inbox = $$"""
      [{
        "MessageId": "{{eventId}}",
        "HandlerName": "TestHandler",
        "MessageType": "Whizbang.Tests.RemotePresenceEvent",
        "EnvelopeType": "MessageEnvelope",
        "Envelope": {"p": {"OrderId": 42}, "h": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": 8
      }]
      """;

    await using (var store = connection.CreateCommand()) {
      store.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      store.Parameters.AddWithValue("p", inbox);
      store.Parameters.AddWithValue("inst", instanceId);
      await using var r = await store.ExecuteReaderAsync();
      while (await r.ReadAsync()) { /* drain */ }
    }
    // store_inbox_messages stores UNLEASED (immediately claimable); lease it to this instance — as
    // claim_orphaned_inbox does in production — so the inbox emit chain's scan picks it up.
    await using (var lease = connection.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @id";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("id", eventId);
      await lease.ExecuteNonQueryAsync();
    }
    await using (var emit = connection.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    await using (var v = connection.CreateCommand()) {
      v.CommandText = @"
        SELECT (eb.event_data ->> 'OrderId')
        FROM wh_event_store es LEFT JOIN wh_event_body eb ON eb.event_id = es.event_id
        WHERE es.event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await using var r = await v.ExecuteReaderAsync();
      await Assert.That(await r.ReadAsync()).IsTrue().Because("The inbox emit chain must store the ephemeral event's pointer.");
      await Assert.That(r.GetString(0)).IsEqualTo("42").Because("The (rehydrated) full body is offloaded to wh_event_body on the receive path.");
    }
  }

  [Test]
  public async Task LargeBody_EphemeralEvent_StoredAndReadBackFull_NotTruncatedByOffloadAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    // A body far larger than any transport claim-check threshold. The event store must store it FULL —
    // proving the size-triggered blob offload is NOT in the event-store path.
    const int bigLen = 300_000;
    var big = new string('x', bigLen);
    var eventId = Guid.NewGuid();
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}",
        "service_name": "test", "host_name": "test-host", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out-topic",
          "MessageType": "Whizbang.Tests.BigEphemeralEvent", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42, "blob": "{{big}}"}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{Guid.NewGuid()}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Inline NULL, full body offloaded to wh_event_body and read back COALESCE'd at full length.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT length(out_event_data::jsonb ->> 'blob') FROM fetch_events_by_ids(ARRAY[@id]::uuid[])";
      v.Parameters.AddWithValue("id", eventId);
      var len = (int)(await v.ExecuteScalarAsync())!;
      await Assert.That(len).IsEqualTo(bigLen)
        .Because("The ephemeral event store keeps the full large body (offload is transport-only; the storage layer never truncates by size).");
    }
  }

  [Test]
  public async Task LargeBody_SourcedEvent_OffloadedFull_NotTruncatedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    const int bigLen = 300_000;
    var big = new string('y', bigLen);
    var eventId = Guid.NewGuid();
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}",
        "service_name": "test", "host_name": "test-host", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out-topic",
          "MessageType": "Whizbang.Tests.BigSourcedEvent", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42, "blob": "{{big}}"}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{Guid.NewGuid()}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }

    // Full split (077): the large Sourced body lands in wh_event_body at FULL length — no truncation.
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT length(event_data::jsonb ->> 'blob') FROM wh_event_body WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      var len = (int)(await v.ExecuteScalarAsync())!;
      await Assert.That(len).IsEqualTo(bigLen)
        .Because("A large Sourced body is offloaded to wh_event_body at full length — the durable content is never truncated by the split.");
    }
    await using (var v = connection.CreateCommand()) {
      v.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await Assert.That((long)(await v.ExecuteScalarAsync())!).IsEqualTo(1L).Because("The narrow pointer row exists — no inline copy of the large body is possible post-078.");
    }
  }
}
