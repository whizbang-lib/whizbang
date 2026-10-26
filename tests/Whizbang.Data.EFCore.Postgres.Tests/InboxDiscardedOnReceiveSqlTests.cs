// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A received message the consumer decided to skip is stored like every other arrival, already settled with why it was
/// skipped, so the skip is durable and inspectable and the message is never dispatched or copied (#1720).
/// </summary>
/// <remarks>
/// The consumer skipped such messages before <c>store_inbox_messages</c>: no row anywhere. Storing them as ordinary
/// arrivals would let the claim copy a directed or echoed event into this service's event store and dispatch it, so
/// the store writes the row already processed (Stored and Published), naming the reason in its error column, and
/// keeps it out of the claim, the copy and the doorbell.
/// </remarks>
/// <docs>messaging/transports/transport-consumer#batch-handler</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class InboxDiscardedOnReceiveSqlTests : EFCoreTestBase {
  private const int STORED_AND_PUBLISHED = 1 | 4;

  [Test]
  public async Task AMessageDiscardedOnReceive_IsStoredSettled_WithItsReason_AndNeverClaimedOrCopiedAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    var instanceId = Guid.CreateVersion7();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await _registerInstanceAsync(connection, instanceId);

    var returned = await _storeAsync(connection, messageId, streamId, discardedOnReceive: "owned_echo");
    await _claimWorkAsync(connection, instanceId);

    await Assert.That(await _scalarAsync<bool>(connection, "SELECT EXISTS (SELECT 1 FROM wh_inbox WHERE message_id = @id)", messageId))
      .IsTrue().Because("every received message is stored, the skipped ones included");
    await Assert.That(await _scalarAsync<bool>(connection,
      "SELECT processed_at IS NOT NULL FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsTrue().Because("the skip is settled with the row, so nothing claims or dispatches it");
    await Assert.That(await _scalarAsync<int>(connection, "SELECT status FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsEqualTo(STORED_AND_PUBLISHED);
    await Assert.That(await _scalarAsync<string>(connection, "SELECT error FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsEqualTo("discarded on receive: owned_echo")
      .Because("the row says why it was skipped");
    await Assert.That(await _scalarAsync<bool>(connection,
      "SELECT instance_id IS NULL AND lease_expiry IS NULL FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsTrue().Because("the claim takes no settled row");
    await Assert.That(await _scalarAsync<bool>(connection, "SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @id)", messageId))
      .IsFalse().Because("a skipped event is not copied into this service's event store");
    await Assert.That(returned).IsFalse().Because("a settled row is not new work");
    await Assert.That(await _scalarAsync<int>(connection,
      "SELECT observation_count FROM wh_message_deduplication WHERE message_id = @id", messageId)).IsEqualTo(1)
      .Because("the arrival is still observed, so a redelivery is counted as one");
  }

  // ===== Helpers =====

  /// <summary>Stores one received event the way the consumer's batch does; returns whether the store reported it new.</summary>
  private static async Task<bool> _storeAsync(NpgsqlConnection connection, Guid messageId, Guid streamId, string discardedOnReceive) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM store_inbox_messages(
          jsonb_build_array(jsonb_build_object(
            'MessageId', @id, 'HandlerName', 'ShipmentHandler',
            'EnvelopeType', 'Whizbang.Core.Observability.MessageEnvelope`1[[Upstream.Contracts.OrderShipped, Upstream.Contracts]], Whizbang.Core',
            'MessageType', 'Upstream.Contracts.OrderShipped, Upstream.Contracts',
            'Envelope', jsonb_build_object('id', @id, 'p', '{}'::jsonb), 'Metadata', '{}'::jsonb,
            'StreamId', @stream, 'IsEvent', true, 'Priority', 150,
            'DiscardedOnReceive', @reason)),
          NULL, NULL, NOW(), 100) s
        WHERE s.message_id = @id AND s.was_newly_created)
      """;
    command.Parameters.AddWithValue("id", messageId);
    command.Parameters.AddWithValue("stream", streamId);
    command.Parameters.AddWithValue("reason", discardedOnReceive);
    return (bool)(await command.ExecuteScalarAsync())!;
  }

  private static async Task _registerInstanceAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'discard-svc', 'discard-host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    command.Parameters.AddWithValue("id", instanceId);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task _claimWorkAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM claim_work(@inst, 'discard-svc', 'discard-host', 100, 100, 100, 100)";
    command.Parameters.AddWithValue("inst", instanceId);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      // drain
    }
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection connection, string sql, Guid id) {
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    return (T)(await command.ExecuteScalarAsync())!;
  }
}
