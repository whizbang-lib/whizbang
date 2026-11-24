// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The live reads hand a stream to dispatch and apply in its stream order, carried on each row, never by id (#2551).
/// <c>fetch_inbox_batch</c> ranks a stream's received events by their stored version (the receive order) and its
/// commands by their arrival (the position <c>store_inbox_messages</c> gives each one in the order it was handed them),
/// each kind dealt into the arrival slots it holds, and returns that rank as <see cref="InboxBatchRow.StreamPosition"/>.
/// <c>get_stream_events</c> returns each event's version as <see cref="StreamEventData.Version"/>.
/// </summary>
/// <remarks>
/// Every case is arranged so the ids sort against the expected order: ordering by id would fail it.
/// </remarks>
/// <docs>fundamentals/event-store/version-ordering#dispatch-order</docs>
[Category("Shard3")]
public class FetchInboxBatchStreamOrderSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Producer.OrderAdjusted, Producer";
  private const string COMMAND_TYPE = "Producer.AdjustOrder, Producer";

  [Test]
  public async Task ReceivedEvents_AreFetchedInTheirStoredOrder_WithTheirStreamPositionAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    // The origin wrote ids[1] first (sequence 3); ids[0] after it (sequence 7).
    await _storeInboxAsync(conn, instance, _message(ids[0], stream, isEvent: true, origin, 7), _message(ids[1], stream, isEvent: true, origin, 3));
    await _emitInboxChainAsync(conn, instance);

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([ids[1], ids[0]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a received event stream is dispatched in the order it was stored, its origin's order, never by id");
    await Assert.That(rows.Select(r => r.StreamPosition)).IsEquivalentTo([1L, 2L], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each row carries its stream position, so the drain never has to re-derive the order");
  }

  [Test]
  public async Task Commands_AreFetchedInTheOrderTheyArrived_NotByIdAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(3);
    // One delivery of three commands on one stream, handed over against their id order.
    await _storeInboxAsync(conn, instance,
      _message(ids[2], stream, isEvent: false), _message(ids[0], stream, isEvent: false), _message(ids[1], stream, isEvent: false));

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([ids[2], ids[0], ids[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a command has no stream version; it is dispatched in the order it arrived, never by id");
    await Assert.That(rows.Select(r => r.StreamPosition)).IsEquivalentTo([1L, 2L, 3L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task AStreamsEventsAndCommands_EachKindKeepsItsOrder_InTheArrivalSlotsItHoldsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(3);
    // Arrivals: the event the origin wrote second (sequence 2, lowest id), then a command, then the event it wrote first
    // (sequence 1). The events take the event slots (first and third) in the origin's order; the command keeps its slot.
    var second = ids[0];
    var command = ids[1];
    var first = ids[2];
    await _storeInboxAsync(conn, instance, _message(second, stream, isEvent: true, origin, 2));
    await _storeInboxAsync(conn, instance, _message(command, stream, isEvent: false));
    await _storeInboxAsync(conn, instance, _message(first, stream, isEvent: true, origin, 1));
    await _emitInboxChainAsync(conn, instance);

    var rows = await _coordinator(dbContext).FetchInboxBatchAsync([stream], instance, 100, null);

    await Assert.That(rows.Select(r => r.MessageId)).IsEquivalentTo([first, command, second], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the events are in their stored order, the command in its arrival slot, and no id decides anything");
  }

  [Test]
  public async Task GetStreamEvents_CarriesEachEventsVersion_InStreamOrderAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var instance = await _registerInstanceAsync(conn);
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(3);
    // Versions run against the ids: the highest id is version 1. Aged past the stamp grace, so unstamped rows are read.
    for (var i = 0; i < 3; i++) {
      await _insertEventAsync(conn, ids[2 - i], stream, version: i + 1);
      await _insertLeasedWorkAsync(conn, stream, ids[2 - i], instance);
    }

    var events = await _coordinator(dbContext).GetStreamEventsAsync(instance, [stream]);

    await Assert.That(events.Select(e => e.EventId)).IsEquivalentTo([ids[2], ids[1], ids[0]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an unstamped event is read in stream order, its version, never by id");
    await Assert.That(events.Select(e => e.Version)).IsEquivalentTo(new long?[] { 1, 2, 3 }, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the read carries each event's version, so the apply never has to re-derive the order");
  }

  // ===== Helpers =====

  private static Guid[] _orderedIds(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).Order()];

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static string _message(Guid id, Guid stream, bool isEvent, Guid? origin = null, long sequence = 0) => $$"""
    {
      "MessageId": "{{id}}",
      "HandlerName": "ProbeHandler",
      "MessageType": "{{(isEvent ? EVENT_TYPE : COMMAND_TYPE)}}",
      "EnvelopeType": "MessageEnvelope",
      "Envelope": {"p": {"Probe": 1}, "h": []},
      "Metadata": {},
      "Scope": null,
      "StreamId": "{{stream}}",
      "IsEvent": {{(isEvent ? "true" : "false")}},
      "Flags": 0,
      "SourceServiceId": "{{origin ?? Guid.Empty}}",
      "SourceCommitSequence": {{sequence}}
    }
    """;

  /// <summary>One delivery, stored by store_inbox_messages, then leased to the instance as a claim leases it.</summary>
  private static async Task _storeInboxAsync(NpgsqlConnection conn, Guid instance, params string[] messages) {
    await _storeAsync(conn, instance, messages);
    await using var lease = conn.CreateCommand();
    lease.CommandText = """
      UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes'
      WHERE message_id IN (SELECT (e ->> 'MessageId')::uuid FROM jsonb_array_elements(@p::jsonb) AS e)
      """;
    lease.Parameters.AddWithValue("inst", instance);
    lease.Parameters.AddWithValue("p", "[" + string.Join(",", messages) + "]");
    await lease.ExecuteNonQueryAsync();
  }

  private static async Task _storeAsync(NpgsqlConnection conn, Guid instance, string[] messages) {
    await using var store = conn.CreateCommand();
    store.CommandText = "SELECT * FROM store_inbox_messages(@p::jsonb, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    store.Parameters.AddWithValue("p", "[" + string.Join(",", messages) + "]");
    store.Parameters.AddWithValue("inst", instance);
    await using var reader = await store.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
  }

  private static async Task _emitInboxChainAsync(NpgsqlConnection conn, Guid instance) {
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    emit.Parameters.AddWithValue("inst", instance);
    _ = await emit.ExecuteScalarAsync();
  }

  private static async Task _insertEventAsync(NpgsqlConnection conn, Guid eventId, Guid stream, int version) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      VALUES (@eid, @sid, @sid, 'TestAggregate', @ver, 'TestEvent', NULL, NOW())
      """;
    ins.Parameters.AddWithValue("eid", eventId);
    ins.Parameters.AddWithValue("sid", stream);
    ins.Parameters.AddWithValue("ver", version);
    await ins.ExecuteNonQueryAsync();
    await using var body = conn.CreateCommand();
    body.CommandText = "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@eid, '{}'::jsonb, '{}'::jsonb)";
    body.Parameters.AddWithValue("eid", eventId);
    await body.ExecuteNonQueryAsync();
  }

  private static async Task _insertLeasedWorkAsync(NpgsqlConnection conn, Guid stream, Guid eventId, Guid instance) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_perspective_events
        (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
      VALUES (gen_random_uuid(), @stream, 'Projection.Test', @event, @inst, NOW() + INTERVAL '5 minutes', 0, 0, 0,
              NOW() - INTERVAL '1 minute')
      """;
    ins.Parameters.AddWithValue(nameof(stream), stream);
    ins.Parameters.AddWithValue("event", eventId);
    ins.Parameters.AddWithValue("inst", instance);
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<Guid> _registerInstanceAsync(NpgsqlConnection conn) {
    var instanceId = (Guid)TrackedGuid.New();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("id", instanceId);
    await cmd.ExecuteNonQueryAsync();
    return instanceId;
  }
}
