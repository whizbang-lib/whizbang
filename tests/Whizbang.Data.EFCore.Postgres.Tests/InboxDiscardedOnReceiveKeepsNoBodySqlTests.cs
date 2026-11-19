// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A message the consumer discards on receive is stored settled without its body: the row keeps what deduplication and
/// diagnostics need (its id, type, stream, scope, reason and timestamps) and an empty envelope and metadata, and a
/// later delivery of the same id is still recognized as a duplicate.
/// </summary>
/// <remarks>
/// Since #1720 such a row kept the full envelope and metadata. Nothing reads them: the row is settled, so it is never
/// claimed, copied, dispatched or dead-lettered, and deduplication is answered by <c>wh_message_deduplication</c>. On a
/// busy subscriber the bodies were most of the inbox's size until the completed-inbox purge removed them.
/// </remarks>
/// <docs>messaging/transports/transport-consumer#batch-handler</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/368_DiscardedArrivalsKeepNoBody.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class InboxDiscardedOnReceiveKeepsNoBodySqlTests : EFCoreTestBase {
  private const string PAYLOAD_MARKER = "a-body-the-discarded-row-must-not-keep";

  [Test]
  [Arguments(InboxReceiveDiscard.NO_LOCAL_CONSUMER)]
  [Arguments(InboxReceiveDiscard.FOREIGN_TARGET)]
  [Arguments(InboxReceiveDiscard.OWNED_ECHO)]
  [Arguments(InboxReceiveDiscard.SELF_ECHO)]
  [Arguments(InboxReceiveDiscard.UNKNOWN_EVENT_TYPE)]
  public async Task AMessageDiscardedOnReceive_IsStoredWithoutItsBody_AndIsStillADuplicateOnRedeliveryAsync(string reason) {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var first = await _storeAsync(connection, messageId, streamId, reason);
    var redelivered = await _storeAsync(connection, messageId, streamId, reason);

    await Assert.That(await _scalarAsync<string>(connection, "SELECT event_data::text FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo("{}").Because("a discarded message is stored without its envelope");
    await Assert.That(await _scalarAsync<string>(connection, "SELECT metadata::text FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo("{}").Because("a discarded message is stored without its metadata");
    await Assert.That(await _scalarAsync<string>(connection,
      "SELECT message_type || '|' || stream_id::text || '|' || (scope ->> 't') FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo($"Upstream.Contracts.OrderShipped, Upstream.Contracts|{streamId}|tenant-a")
      .Because("the type, stream and scope stay for diagnostics and the reconciliation that keys by them");
    await Assert.That(await _scalarAsync<string>(connection, "SELECT error FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsEqualTo("discarded on receive: " + reason).Because("the row still says why it was skipped");
    await Assert.That(await _scalarAsync<bool>(connection,
      "SELECT processed_at IS NOT NULL FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsTrue().Because("the row is still settled on arrival");
    await Assert.That(first).IsFalse().Because("a settled row is not new work");
    await Assert.That(redelivered).IsFalse().Because("a redelivery of a discarded message is a duplicate");
    await Assert.That(await _scalarAsync<int>(connection,
      "SELECT observation_count FROM wh_message_deduplication WHERE message_id = @id", messageId))
      .IsEqualTo(2).Because("deduplication counts the redelivery against the first arrival");
    await Assert.That(await _scalarAsync<long>(connection, "SELECT COUNT(*) FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo(1L).Because("the redelivery stores nothing a second time");
  }

  [Test]
  public async Task AMessageDiscardedOnReceive_RedeliveredForProcessing_IsStillADuplicate_AndNotDispatchedAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await _storeAsync(connection, messageId, streamId, InboxReceiveDiscard.NO_LOCAL_CONSUMER);
    var redelivered = await _storeAsync(connection, messageId, streamId, discardedOnReceive: null);

    await Assert.That(redelivered).IsFalse()
      .Because("a delivery this service would process is still a duplicate of the discarded arrival, so the empty body is never dispatched");
    await Assert.That(await _scalarAsync<string>(connection, "SELECT event_data::text FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo("{}").Because("the duplicate leaves the stored row as it was");
  }

  [Test]
  public async Task AnOrdinaryArrival_KeepsItsBodyAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    var stored = await _storeAsync(connection, messageId, streamId, discardedOnReceive: null);

    await Assert.That(stored).IsTrue();
    await Assert.That(await _scalarAsync<string>(connection, "SELECT event_data -> 'p' ->> 'note' FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo(PAYLOAD_MARKER).Because("a message the service processes is dispatched from its stored envelope");
    await Assert.That(await _scalarAsync<string>(connection, "SELECT metadata ->> 'note' FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo(PAYLOAD_MARKER);
  }

  [Test]
  public async Task ADiscardedRedelivery_OfAProcessedMessage_LeavesItsStoredBodyAloneAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await _storeAsync(connection, messageId, streamId, discardedOnReceive: null);
    await _storeAsync(connection, messageId, streamId, InboxReceiveDiscard.FOREIGN_TARGET);

    await Assert.That(await _scalarAsync<string>(connection, "SELECT event_data -> 'p' ->> 'note' FROM wh_inbox WHERE message_id = @id", messageId))
      .IsEqualTo(PAYLOAD_MARKER).Because("only a first arrival that is discarded is stored without its body");
    await Assert.That(await _scalarAsync<bool>(connection,
      "SELECT error IS NULL FROM wh_inbox_state WHERE message_id = @id", messageId))
      .IsTrue();
  }

  // ===== Helpers =====

  /// <summary>Stores one received event with a real body, as the consumer's batch does; returns whether it was new.</summary>
  private static async Task<bool> _storeAsync(NpgsqlConnection connection, Guid messageId, Guid streamId, string? discardedOnReceive) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM store_inbox_messages(
          jsonb_build_array(jsonb_strip_nulls(jsonb_build_object(
            'MessageId', @id, 'HandlerName', 'ShipmentHandler',
            'EnvelopeType', 'Whizbang.Core.Observability.MessageEnvelope`1[[Upstream.Contracts.OrderShipped, Upstream.Contracts]], Whizbang.Core',
            'MessageType', 'Upstream.Contracts.OrderShipped, Upstream.Contracts',
            'Envelope', jsonb_build_object('id', @id, 'p', jsonb_build_object('note', @marker)),
            'Metadata', jsonb_build_object('note', @marker, 'Hops', jsonb_build_array(jsonb_build_object('sn', 'Upstream'))),
            'Scope', jsonb_build_object('t', 'tenant-a'),
            'StreamId', @stream, 'IsEvent', true, 'Priority', 150,
            'DiscardedOnReceive', @reason))),
          NULL, NULL, NOW(), 100) s
        WHERE s.message_id = @id AND s.was_newly_created)
      """;
    command.Parameters.AddWithValue("id", messageId);
    command.Parameters.AddWithValue("stream", streamId);
    command.Parameters.AddWithValue("marker", PAYLOAD_MARKER);
    command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)discardedOnReceive ?? DBNull.Value });
    return (bool)(await command.ExecuteScalarAsync())!;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection connection, string sql, Guid id) {
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    return (T)(await command.ExecuteScalarAsync())!;
  }
}
