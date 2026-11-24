// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A stream's versions follow the order its events were emitted in, never the order of their ids (#2551). An id is time
/// ordered and says nothing about stream position: a derived id sorts at its cause, so the effect a cause's local
/// receptor emits (derived from the cause, ordinal 0) can sort before the cause itself (ordinal 3 of the handling that
/// emitted it). Written in one batch, the cause must still take the lower version.
/// </summary>
/// <docs>fundamentals/event-store/version-ordering#emission-order</docs>
/// <docs>fundamentals/dispatcher/message-cascade#every-dispatch-path-derives</docs>
[Category("Shard5")]
public class EmissionOrderVersioningSqlTests : EFCoreTestBase {
  private const string TOPIC = "emission-order-topic";
  private const string TYPE = "Probe.Contracts.OrderAdjusted, Probe.Contracts";
  private const string SERVICE = "orders";

  [Test]
  public async Task Store_ACauseAndItsDerivedEffectInOneBatch_TheCauseTakesTheLowerVersionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var (cause, effect) = _causeAndEffect();

    await coordinator.StoreOutboxMessagesAsync([_event(streamId, cause), _event(streamId, effect)], partitionCount: 10000);

    var versions = await _versionsAsync(await _openAsync(dbContext), streamId);
    await Assert.That(versions).IsEquivalentTo([(cause, 1L), (effect, 2L)], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the cause was emitted first; a stream is versioned in emission order, not by id");
  }

  [Test]
  public async Task CommitHandlerResult_ACauseAndItsDerivedEffect_TheCauseTakesTheLowerVersionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var conn = await _openAsync(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var instanceId = (Guid)TrackedGuid.New();
    var inboxId = (Guid)TrackedGuid.New();
    await _seedLeasedInboxRowAsync(conn, inboxId, instanceId);
    var (cause, effect) = _causeAndEffect();

    await coordinator.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: inboxId,
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 4),
      NewOutboxMessages: [_event(streamId, cause), _event(streamId, effect)]));

    var versions = await _versionsAsync(conn, streamId);
    await Assert.That(versions).IsEquivalentTo([(cause, 1L), (effect, 2L)], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a handler's commit stores what it emitted in the order it emitted it, not by id");
  }

  [Test]
  public async Task EmitChain_EventsListedAgainstTheirIdOrder_AreVersionedInTheOrderListedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var instanceId = (Guid)TrackedGuid.New();
    // Emitted in the reverse of their id order: the chain numbers the stream by the position each event is listed at.
    Guid[] emitted = [.. new[] { (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New() }.OrderDescending()];
    foreach (var id in emitted) {
      await _queueOutboxEventAsync(conn, id, streamId, instanceId);
    }

    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
      emit.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = emitted });
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    await Assert.That(await _versionsAsync(conn, streamId))
      .IsEquivalentTo([(emitted[0], 1L), (emitted[1], 2L), (emitted[2], 3L)], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the batch's order is the order its events were emitted in; the ids sort the other way and decide nothing");
  }

  [Test]
  public async Task EmitChain_AnIdListedTwice_KeepsItsFirstPositionAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var instanceId = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    await _queueOutboxEventAsync(conn, first, streamId, instanceId);
    await _queueOutboxEventAsync(conn, second, streamId, instanceId);

    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
      emit.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { second, first, second } });
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
    }

    await Assert.That(await _versionsAsync(conn, streamId))
      .IsEquivalentTo([(second, 1L), (first, 2L)], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an event listed twice is one event, at the first place it was listed, and the stream stays gap-free");
  }

  private static async Task _queueOutboxEventAsync(NpgsqlConnection conn, Guid id, Guid streamId, Guid instanceId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
         created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
      VALUES (@msg, 'topic', @type, 'TestEnv', '{"p":1}'::jsonb, '{}'::jsonb, '{}'::jsonb, 1, 0,
              NOW(), @stream, 0, @inst, NOW() + INTERVAL '5 minutes', true)
      """;
    ins.Parameters.AddWithValue("msg", id);
    ins.Parameters.AddWithValue("type", TYPE);
    ins.Parameters.AddWithValue("stream", streamId);
    ins.Parameters.AddWithValue("inst", instanceId);
    await ins.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// A cause at ordinal 3 of a handling, and the effect its local receptor emitted, derived from the cause at ordinal 0:
  /// both share the handling's first 80 bits, so the effect's id sorts before the cause's.
  /// </summary>
  private static (Guid Cause, Guid Effect) _causeAndEffect() {
    var handled = (Guid)TrackedGuid.New();
    var cause = EmissionIdentity.Derive(handled, SERVICE, "ShipOrderHandler", TYPE, ordinal: 3);
    var effect = EmissionIdentity.Derive(cause, SERVICE, handlerName: null, TYPE, ordinal: 0);
    if (string.CompareOrdinal(effect.ToString(), cause.ToString()) >= 0) {
      throw new InvalidOperationException("precondition: the derived effect's id must sort before its cause's");
    }
    return (cause, effect);
  }

  private static OutboxMessage _event(Guid streamId, Guid id) =>
    CreateTestOutboxMessage(id, TOPIC, streamId, isEvent: true) with { MessageType = TYPE };

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _seedLeasedInboxRowAsync(NpgsqlConnection conn, Guid inboxId, Guid instanceId) {
    await using var ins = conn.CreateCommand();
    ins.CommandText = @"
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
        VALUES (@msg, 'ProbeHandler', 'ProbeCommand', '{}', '{}', NOW(), @stream)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         instance_id, lease_expiry, partition_number)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0,
             @inst, NOW() + INTERVAL '60 seconds', 0
      FROM m";
    ins.Parameters.AddWithValue("msg", inboxId);
    ins.Parameters.AddWithValue("inst", instanceId);
    ins.Parameters.AddWithValue("stream", (Guid)TrackedGuid.New());
    await ins.ExecuteNonQueryAsync();
  }

  private static async Task<List<(Guid EventId, long Version)>> _versionsAsync(NpgsqlConnection conn, Guid streamId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id, version FROM wh_event_store WHERE stream_id = @stream ORDER BY version";
    cmd.Parameters.AddWithValue("stream", streamId);
    var rows = new List<(Guid, long)>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
    }
    return rows;
  }
}
