// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A receiver stores one source stream's events in the producer's order, however many inbox transactions carry
/// them and whichever of those commits first (#1607).
/// </summary>
/// <remarks>
/// <para>
/// The producer wrote the stream in one transaction and published it as a composite, so every event the receiver
/// holds is a fan-out child: its id is derived from the composite's id and its ordinal (producer order), and it
/// carries no origin commit sequence. Two claims of the receiver lease the stream's rows at the same time. The claim
/// that leased the first rows (the stream's head) is the one that reaches the emit chain last, so the later events
/// would be numbered first.
/// </para>
/// <para>
/// Every interleaving is fixed by holding a transaction open: the first claim's lease is an uncommitted UPDATE on its
/// own connection while the second claim runs to commit on another, so there are no waits and no timing.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-store</docs>
[Category("Shard3")]
public class ReceivedStreamOrderSqlTests : EFCoreTestBase {
  private const int EVENT_COUNT = 49;
  private const int HEAD_COUNT = 9;
  private const string CHILD_TYPE = "Producer.FieldSetEvent, Producer";

  [Test]
  public async Task TwoClaimsOfOneStream_TheHeadsClaimCommitsLast_VersionsFollowTheProducerOrderAsync() {
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var producerOrder = _fanOutChildIds(EVENT_COUNT);
    await using (var setup = await _openAsync()) {
      await _registerInstanceAsync(setup, instanceId);
      await _storeReceivedAsync(setup, streamId, producerOrder);
    }

    // The first claim has leased the stream's head and has not reached its emit chain yet.
    await using var first = await _openAsync();
    await using var firstClaim = await first.BeginTransactionAsync();
    await _leaseAsync(first, instanceId, producerOrder[..HEAD_COUNT]);

    // The second claim leases what is left of the stream (the head is locked, so it is skipped), runs the
    // emit chain, and commits.
    await using (var second = await _openAsync()) {
      await _claimWorkAsync(second, instanceId);
    }

    // The first claim reaches its emit chain and commits last.
    await _emitInboxChainAsync(first, instanceId);
    await firstClaim.CommitAsync();

    await using var reader = await _openAsync();
    var stored = await _eventIdsInVersionOrderAsync(reader, streamId);
    await Assert.That(stored.Count).IsEqualTo(EVENT_COUNT)
      .Because("every received event of the stream is stored once");
    await Assert.That(stored).IsEquivalentTo(producerOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the receiver's versions follow the producer's order, not the order the two claims committed in");
  }

  [Test]
  public async Task TwoInstancesShareAStream_TheTailWaitsForThePeersHead_ThenFollowsItAsync() {
    var instanceId = Guid.CreateVersion7();
    var peerId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var producerOrder = _fanOutChildIds(EVENT_COUNT);
    await using var connection = await _openAsync();
    await _registerInstanceAsync(connection, instanceId);
    await _registerInstanceAsync(connection, peerId);
    await _storeReceivedAsync(connection, streamId, producerOrder);
    await _leaseAsync(connection, peerId, producerOrder[..HEAD_COUNT]);
    await _leaseAsync(connection, instanceId, producerOrder[HEAD_COUNT..]);

    await _emitInboxChainAsync(connection, instanceId);
    var whileThePeerHoldsTheHead = await _eventIdsInVersionOrderAsync(connection, streamId);

    await _emitInboxChainAsync(connection, peerId);
    await _emitInboxChainAsync(connection, instanceId);

    await Assert.That(whileThePeerHoldsTheHead).IsEmpty()
      .Because("every event this instance holds sorts after the head a live peer holds, so none of them is stored yet");
    await Assert.That(await _eventIdsInVersionOrderAsync(connection, streamId))
      .IsEquivalentTo(producerOrder, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the peer stores the head, and the next pass stores the tail after it");
  }

  [Test]
  public async Task RowsBeforeTheFirstUnheldRow_AreStored_AndOnlyTheRowsAfterItWaitAsync() {
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var ids = _fanOutChildIds(6);
    await using var connection = await _openAsync();
    await _registerInstanceAsync(connection, instanceId);
    await _storeReceivedAsync(connection, streamId, ids);
    await _leaseAsync(connection, instanceId, [ids[0], ids[1], ids[2], ids[4], ids[5]]);

    await _emitInboxChainAsync(connection, instanceId);

    await Assert.That(await _eventIdsInVersionOrderAsync(connection, streamId))
      .IsEquivalentTo(ids[..3], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the rows before the unleased one are the stream's head and are stored at once");
    await Assert.That(await _unstampedAsync(connection, [ids[4], ids[5]])).IsEqualTo(2)
      .Because("a row that waits is left for the next pass to re-read");
  }

  [Test]
  public async Task AnEarlierCommand_OrAnEarlierEventAlreadyStored_DoesNotHoldTheStreamAsync() {
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var ids = _fanOutChildIds(4);
    await using var connection = await _openAsync();
    await _registerInstanceAsync(connection, instanceId);
    await _storeReceivedAsync(connection, streamId, ids);
    // ids[0] is a command on the stream, nobody holds it; ids[1] is already stored, its row not yet stamped.
    await _executeAsync(connection, "UPDATE wh_inbox_state SET is_event = false WHERE message_id = @id", ids[0]);
    await _executeAsync(connection,
      "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) "
      + "SELECT message_id, stream_id, stream_id, 'Producer', message_type, 1, NOW() FROM wh_inbox WHERE message_id = @id", ids[1]);
    await _leaseAsync(connection, instanceId, ids[2..]);

    await _emitInboxChainAsync(connection, instanceId);

    await Assert.That(await _eventIdsInVersionOrderAsync(connection, streamId))
      .IsEquivalentTo(ids[1..], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("only an event that is not yet stored is a head to wait for");
  }

  [Test]
  public async Task AWaitingEvent_IsNotFetched_UntilItIsStoredAsync() {
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var ids = _fanOutChildIds(3);
    await using var connection = await _openAsync();
    await _registerInstanceAsync(connection, instanceId);
    await _storeReceivedAsync(connection, streamId, ids);
    // ids[2] is a command on the stream: it is dispatched whether or not the stream's events are stored.
    await _executeAsync(connection, "UPDATE wh_inbox_state SET is_event = false WHERE message_id = @id", ids[2]);
    await _leaseAsync(connection, instanceId, ids[1..]);

    await _emitInboxChainAsync(connection, instanceId);
    var whileItWaits = await _fetchAsync(connection, instanceId, streamId);

    await _leaseAsync(connection, instanceId, ids[..1]);
    await _emitInboxChainAsync(connection, instanceId);
    var onceStored = await _fetchAsync(connection, instanceId, streamId);

    await Assert.That(whileItWaits).IsEquivalentTo([ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the event waiting behind the stream's head is not stored, so it is not dispatched; the command is");
    await Assert.That(onceStored).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("once the head is held and both events are stored, the whole stream is fetched in order");
  }

  // ===== Helpers =====

  /// <summary>The ids a composite fan-out gives its children: derived from the composite and the ordinal, so they sort in producer order.</summary>
  private static Guid[] _fanOutChildIds(int count) {
    var compositeId = Guid.CreateVersion7();
    var ids = new Guid[count];
    for (var ordinal = 0; ordinal < count; ordinal++) {
      ids[ordinal] = CompositeChildIdentity.Derive(compositeId, ordinal, CHILD_TYPE);
    }
    return ids;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>Stores the received events as store_inbox_messages leaves them: unleased, one arrival time (the database's clock), no origin commit sequence.</summary>
  private static async Task _storeReceivedAsync(NpgsqlConnection connection, Guid streamId, Guid[] messageIds) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, scope,
           received_at, stream_id, is_event, source_service_id, source_commit_sequence)
        SELECT id, 'FieldSetEventHandler', @type, '{"p":{}}'::jsonb, '{}'::jsonb, '{}'::jsonb,
               NOW() - INTERVAL '30 seconds', @stream, true, @producer, 0
        FROM unnest(@ids) AS id
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts, partition_number)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0, 0 FROM m
      """;
    command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = messageIds });
    command.Parameters.AddWithValue("type", CHILD_TYPE);
    command.Parameters.AddWithValue("stream", streamId);
    command.Parameters.AddWithValue("producer", Guid.CreateVersion7());
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

  private static async Task _claimWorkAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM claim_work(@inst, 'test-svc', 'test-host', 100, 100, 100, 100)";
    command.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
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

  private static async Task<List<Guid>> _fetchAsync(NpgsqlConnection connection, Guid instanceId, Guid streamId) {
    var ids = new List<Guid>();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT message_id FROM fetch_inbox_batch(@streams, @inst, 100, NULL)";
    command.Parameters.Add(new NpgsqlParameter("streams", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { streamId } });
    command.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private static async Task<long> _unstampedAsync(NpgsqlConnection connection, Guid[] messageIds) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT count(*) FROM wh_inbox_state WHERE message_id = ANY(@ids) AND chain_emitted_at IS NULL";
    command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = messageIds });
    return (long)(await command.ExecuteScalarAsync())!;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, Guid id) {
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    command.Parameters.AddWithValue("id", instanceId);
    await command.ExecuteNonQueryAsync();
  }
}
