// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A received stream is versioned in its origin's order, never by id (#2551). An event received from another service
/// carries its origin and its position there (the origin's commit sequence). The receiving store numbers each origin's
/// events in that order and keeps the arrival order between origins, the rule a replay folds by (ReplayEventOrder,
/// #2257), so what is stored and what a replay folds agree. Only an event that carries no origin position falls back to
/// its id, placed among its origin's positioned events the way the replay places it (#2379).
/// </summary>
/// <remarks>
/// Every case is arranged so the ids sort against the expected order: numbering by id would fail it.
/// </remarks>
/// <docs>fundamentals/event-store/version-ordering#receive-order</docs>
[Category("Shard3")]
public class ReceiveOrderVersioningSqlTests : EFCoreTestBase {
  private const string TYPE = "Producer.OrderAdjusted, Producer";

  [Test]
  public async Task OneClaim_AnOriginsEvents_AreVersionedByTheOriginsSequence_NotByIdAsync() {
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    await using var connection = await _openAsync();
    var instance = await _registerInstanceAsync(connection);
    // The origin committed ids[1] first (sequence 3) and ids[0] after it (sequence 7).
    await _receiveAsync(connection, stream, new Row(ids[0], origin, 7, 0), new Row(ids[1], origin, 3, 0));
    await _leaseAsync(connection, instance, ids);

    await _emitInboxChainAsync(connection, instance);

    await Assert.That(await _eventIdsInVersionOrderAsync(connection, stream))
      .IsEquivalentTo([ids[1], ids[0]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the origin's commit sequence is its stream order; the ids sort the other way and decide nothing");
  }

  [Test]
  public async Task AnUnpositionedEvent_TakesTheReplaysPlace_AmongItsOriginsPositionedEventsAsync() {
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(5);
    await using var connection = await _openAsync();
    var instance = await _registerInstanceAsync(connection);
    // Positioned: p1 (sequence 1, the highest id) then p2 (sequence 2, the lowest id). Unpositioned: u, whose id sorts
    // before p1's, so the replay places it before p1 (#2379). By id alone the order would be p2, u, p1.
    var p1 = ids[4];
    var p2 = ids[0];
    var u = ids[2];
    await _receiveAsync(connection, stream, new Row(p1, origin, 1, 0), new Row(p2, origin, 2, 0), new Row(u, origin, 0, 0));
    await _leaseAsync(connection, instance, [p1, p2, u]);

    await _emitInboxChainAsync(connection, instance);

    await Assert.That(await _eventIdsInVersionOrderAsync(connection, stream))
      .IsEquivalentTo([u, p1, p2], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an event with no origin position goes before the first of its origin's positioned events whose id "
        + "sorts after its own, the order a replay folds it in");
  }

  [Test]
  public async Task TwoOrigins_KeepTheirArrivalSlots_EachDealtInItsOwnOrderAsync() {
    var originA = (Guid)TrackedGuid.New();
    var originB = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(3);
    await using var connection = await _openAsync();
    var instance = await _registerInstanceAsync(connection);
    // Arrivals: a5 (origin A, sequence 5) first, b (origin B) second, a2 (origin A, sequence 2) last. A's slots are the
    // first and the third; A's events fill them in A's order, so a2 takes the first and a5 the third.
    var a5 = ids[0];
    var a2 = ids[1];
    var b = ids[2];
    await _receiveAsync(connection, stream, new Row(a5, originA, 5, 0), new Row(b, originB, 100, 10), new Row(a2, originA, 2, 20));
    await _leaseAsync(connection, instance, ids);

    await _emitInboxChainAsync(connection, instance);

    await Assert.That(await _eventIdsInVersionOrderAsync(connection, stream))
      .IsEquivalentTo([a2, b, a5], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each origin's events are in its own order, and the interleaving of origins is the arrival order");
  }

  [Test]
  public async Task AnOriginsEarlierEvent_HeldElsewhere_HoldsBackTheLaterOne_WhateverTheIdsAsync() {
    var origin = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    await using var connection = await _openAsync();
    var instance = await _registerInstanceAsync(connection);
    var peer = await _registerInstanceAsync(connection);
    // later: the origin's sequence 2 with the lower id, leased here; earlier: sequence 1 with the higher id, leased to a
    // live peer that has not stored it yet.
    var later = ids[0];
    var earlier = ids[1];
    await _receiveAsync(connection, stream, new Row(later, origin, 2, 0), new Row(earlier, origin, 1, 0));
    await _leaseAsync(connection, instance, [later]);
    await _leaseAsync(connection, peer, [earlier]);

    await _emitInboxChainAsync(connection, instance);
    var whileThePeerHoldsTheHead = await _eventIdsInVersionOrderAsync(connection, stream);
    await _emitInboxChainAsync(connection, peer);
    await _emitInboxChainAsync(connection, instance);

    await Assert.That(whileThePeerHoldsTheHead).IsEmpty()
      .Because("the origin committed the peer's event first, so this instance's event waits for it, whatever its id");
    await Assert.That(await _eventIdsInVersionOrderAsync(connection, stream))
      .IsEquivalentTo([earlier, later], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the peer stores the origin's first event, and the next pass stores the later one after it");
  }

  [Test]
  public async Task ALaterArrivalHeldElsewhere_DoesNotHoldBackAnEarlierArrivalOfAnotherOriginAsync() {
    var originA = (Guid)TrackedGuid.New();
    var originB = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    await using var connection = await _openAsync();
    var instance = await _registerInstanceAsync(connection);
    var peer = await _registerInstanceAsync(connection);
    // mine arrived first (origin A) with the higher id; theirs arrived after it (origin B) with the lower id.
    var mine = ids[1];
    var theirs = ids[0];
    await _receiveAsync(connection, stream, new Row(mine, originA, 9, 0), new Row(theirs, originB, 1, 10));
    await _leaseAsync(connection, instance, [mine]);
    await _leaseAsync(connection, peer, [theirs]);

    await _emitInboxChainAsync(connection, instance);

    await Assert.That(await _eventIdsInVersionOrderAsync(connection, stream))
      .IsEquivalentTo([mine], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("between origins the arrival order stands; a later arrival of another origin is not this event's head");
  }

  // ===== Helpers =====

  private readonly record struct Row(Guid Id, Guid Origin, long Sequence, int ArrivedSecondsLater);

  private static Guid[] _orderedIds(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).Order()];

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>
  /// Stores received events as store_inbox_messages leaves them, unleased, each with its origin, its origin position
  /// (0 when it carries none) and its arrival time on the database's clock.
  /// </summary>
  private static async Task _receiveAsync(NpgsqlConnection connection, Guid streamId, params Row[] rows) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope,
           received_at, stream_id, is_event, source_service_id, source_commit_sequence)
        SELECT r.id, 'OrderAdjustedHandler', @type, '{"p":{}}'::jsonb, '{}'::jsonb, '{}'::jsonb,
               NOW() - INTERVAL '60 seconds' + r.later * INTERVAL '1 second', @stream, true, r.origin, r.seq
        FROM unnest(@ids, @origins, @seqs, @laters) AS r(id, origin, seq, later)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0 FROM m
      """;
    command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.Id).ToArray() });
    command.Parameters.Add(new NpgsqlParameter("origins", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.Origin).ToArray() });
    command.Parameters.Add(new NpgsqlParameter("seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(r => r.Sequence).ToArray() });
    command.Parameters.Add(new NpgsqlParameter("laters", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => r.ArrivedSecondsLater).ToArray() });
    command.Parameters.AddWithValue("type", TYPE);
    command.Parameters.AddWithValue("stream", streamId);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>The lease step of a claim, as claim_orphaned_inbox writes it.</summary>
  private static async Task _leaseAsync(NpgsqlConnection connection, Guid instanceId, Guid[] messageIds) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      UPDATE wh_inbox_state
      SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes', attempts = attempts + 1
      WHERE message_id = ANY(@ids)
      """;
    command.Parameters.AddWithValue("inst", instanceId);
    command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = messageIds });
    await command.ExecuteNonQueryAsync();
  }

  private static async Task _emitInboxChainAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    command.Parameters.AddWithValue("inst", instanceId);
    await command.ExecuteScalarAsync();
  }

  private static async Task<List<Guid>> _eventIdsInVersionOrderAsync(NpgsqlConnection connection, Guid streamId) {
    var ids = new List<Guid>();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    command.Parameters.AddWithValue("s", streamId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<Guid> _registerInstanceAsync(NpgsqlConnection connection) {
    var instanceId = (Guid)TrackedGuid.New();
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    command.Parameters.AddWithValue("id", instanceId);
    await command.ExecuteNonQueryAsync();
    return instanceId;
  }
}
