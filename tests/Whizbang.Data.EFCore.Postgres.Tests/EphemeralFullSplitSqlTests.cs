using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the FULL pointer/body split (E1 #13b4-2, migration 077): EVERY event's (payload, metadata) —
/// Sourced and Ephemeral alike — is written to <c>wh_event_body</c>, and the <c>wh_event_store</c>
/// pointer's inline columns are always NULL. Covers the SQL emit chain, the C# direct-append path, and
/// the idempotent backfill that moves pre-077 sourced inline bodies into the body table. Reads flow
/// through the body-first COALESCE (#13b4-1), so round-trips must be unchanged.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralFullSplitSqlTests : EFCoreTestBase {
  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _execAsync(NpgsqlConnection connection, string sql, params (string, object)[] ps) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) {
      cmd.Parameters.AddWithValue(n, v);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T?> _scalarAsync<T>(NpgsqlConnection connection, string sql, params (string, object)[] ps) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (n, v) in ps) {
      cmd.Parameters.AddWithValue(n, v);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DBNull or null ? default : (T)result;
  }

  private static async Task _commitSourcedOrderAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, Guid orderId) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{typeof(OrderCreatedEvent).FullName}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": "{{orderId}}", "CustomerName": "SplitSourced"}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await _execAsync(connection, "SELECT commit_handler_result(@req::jsonb)", ("req", request));
  }

  [Test]
  public async Task Emit_SourcedEvent_BodyOffloadedInlineNull_RoundTripsAsync() {
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);

    var streamId = Guid.NewGuid();
    var eventId = (Guid)TrackedGuid.NewMedo();   // MessageId requires UUIDv7
    var orderId = Guid.NewGuid();
    await _commitSourcedOrderAsync(connection, eventId, streamId, orderId);

    // Full split: the SOURCED body lives in wh_event_body; the pointer's inline columns are NULL.
    await Assert.That(await _scalarAsync<long>(connection,
        "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", eventId))).IsEqualTo(1L)
      .Because("Post-077 the emit chain offloads EVERY body, sourced included.");
    await Assert.That(await _scalarAsync<bool>(connection,
        "SELECT (event_data IS NULL AND metadata IS NULL) FROM wh_event_store WHERE event_id = @id", ("id", eventId))).IsTrue()
      .Because("The pointer is narrow: inline body columns are always NULL post-077.");

    // Round-trip through the body-aware reader: the payload is intact.
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }
    await Assert.That(envelopes.Count).IsEqualTo(1);
    await Assert.That(((OrderCreatedEvent)envelopes[0].Payload).OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task AppendAsync_WritesPointerAndBodyRow_RoundTripsAsync() {
    // The C# direct-append path must uphold the same invariant as the SQL emit chain.
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var orderId = Guid.NewGuid();
    await eventStore.AppendAsync(streamId, new OrderCreatedEvent { OrderId = orderId, CustomerName = "SplitAppend" });

    var eventId = await _scalarAsync<Guid>(connection,
      "SELECT event_id FROM wh_event_store WHERE stream_id = @sid", ("sid", streamId));
    await Assert.That(await _scalarAsync<long>(connection,
        "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", eventId))).IsEqualTo(1L)
      .Because("AppendAsync writes the body to wh_event_body post-077.");
    await Assert.That(await _scalarAsync<bool>(connection,
        "SELECT (event_data IS NULL AND metadata IS NULL) FROM wh_event_store WHERE event_id = @id", ("id", eventId))).IsTrue()
      .Because("AppendAsync leaves the pointer's inline columns NULL post-077.");

    var envelopes = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var env in eventStore.ReadAsync<OrderCreatedEvent>(streamId, (Guid?)null)) {
      envelopes.Add(env);
    }
    await Assert.That(envelopes.Count).IsEqualTo(1);
    await Assert.That(envelopes[0].Payload.OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task BackfillEventBodies_MovesLegacyInlineRow_IdempotentAsync() {
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);

    // A legacy pre-077 row: sourced body still INLINE, no wh_event_body row.
    var eventId = (Guid)TrackedGuid.NewMedo();
    var streamId = Guid.NewGuid();
    await _execAsync(connection,
      "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, event_data, metadata, version, created_at, flags) " +
      "VALUES (@id, @sid, @sid, 'Legacy', 'Whizbang.Tests.LegacyInlineEvent', '{\"V\": 1}'::jsonb, '{\"MessageId\": \"" + eventId + "\", \"Hops\": []}'::jsonb, 1, NOW(), 0)",
      ("id", eventId), ("sid", streamId));

    var moved = await _scalarAsync<long>(connection, "SELECT wh_backfill_event_bodies()");
    await Assert.That(moved).IsEqualTo(1L).Because("One legacy inline body is moved.");
    await Assert.That(await _scalarAsync<long>(connection,
        "SELECT count(*) FROM wh_event_body WHERE event_id = @id", ("id", eventId))).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<bool>(connection,
        "SELECT (event_data IS NULL AND metadata IS NULL) FROM wh_event_store WHERE event_id = @id", ("id", eventId))).IsTrue()
      .Because("The inline body is nulled only after its copy is durable in wh_event_body.");
    await Assert.That(await _scalarAsync<string>(connection,
        "SELECT (event_data->>'V') FROM wh_event_body WHERE event_id = @id", ("id", eventId))).IsEqualTo("1")
      .Because("The body content survives the move.");

    // Idempotent: a second run moves nothing and changes nothing.
    var second = await _scalarAsync<long>(connection, "SELECT wh_backfill_event_bodies()");
    await Assert.That(second).IsEqualTo(0L);
  }
}
