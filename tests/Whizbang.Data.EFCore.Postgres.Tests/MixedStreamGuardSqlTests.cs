// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// WHIZ133 (#1546): a stream never holds both ephemeral and Sourced events. One ephemeral event marks the
/// whole stream state-based, so every rebuild and rewind would then skip the stream's Sourced history
/// without a word. The step that copies outbox and inbox events into the event store refuses the event that
/// would mix them, in either order: a direct commit (the caller awaits the store) fails loudly with
/// <see cref="MixedStreamEventException"/>, and every background path dead-letters only the offending row,
/// so the other streams in the same write are stored as if nothing happened.
/// </summary>
/// <docs>operations/diagnostics/whiz133</docs>
[Category("Shard5")]
public class MixedStreamGuardSqlTests : EFCoreTestBase {
  private const string TOPIC = "mixed-stream-topic";
  private const string EPHEMERAL_TYPE = "Probe.Contracts.UserIsTyping, Probe.Contracts";
  private const string SOURCED_TYPE = "Probe.Contracts.OrderPlaced, Probe.Contracts";
  private const string COMPACTED_TYPE = "Probe.Contracts.OrderSnapshot, Probe.Contracts";
  private const int MIXED_STREAM_REASON = 22;

  // ---------------------------------------------------------------------------------------------
  // Background writes (the batch flushes, the handler commit): the offending row is dead-lettered.
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Store_SourcedEventOnAnEphemeralStream_DeadLettersTheSourcedEventAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var ephemeral = _ephemeral(streamId);
    await coordinator.StoreOutboxMessagesAsync([ephemeral], partitionCount: 10000);

    var sourced = _sourced(streamId);
    await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEquivalentTo([ephemeral.MessageId])
      .Because("a Sourced event on an ephemeral stream would be skipped by every rebuild and rewind");
    await Assert.That(await _outboxCountAsync(conn, sourced.MessageId)).IsEqualTo(0L)
      .Because("the refused row is moved out of the outbox, so it is neither published nor retried");
    var dead = await _deadLetterAsync(conn, sourced.MessageId);
    await Assert.That(dead).IsNotNull();
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_outbox");
    await Assert.That(dead.FailureReason).IsEqualTo(MIXED_STREAM_REASON);
    await Assert.That(dead.ErrorText).StartsWith("WHIZ133")
      .Because("the failure names its diagnostic so an operator can look it up");
    await Assert.That(dead.ErrorText).Contains(streamId.ToString());
    await Assert.That(dead.ErrorText).Contains("Probe.Contracts.OrderPlaced");
    await Assert.That(dead.ErrorText).Contains("ephemeral events");
    await Assert.That(dead.ErrorText).Contains("own stream");
  }

  [Test]
  public async Task Store_EphemeralEventOnASourcedStream_DeadLettersTheEphemeralEventAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var sourced = _sourced(streamId);
    await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000);

    var ephemeral = _ephemeral(streamId);
    await coordinator.StoreOutboxMessagesAsync([ephemeral], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEquivalentTo([sourced.MessageId])
      .Because("one ephemeral event would mark the Sourced stream state-based and hide its history from rebuilds");
    var dead = await _deadLetterAsync(conn, ephemeral.MessageId);
    await Assert.That(dead).IsNotNull();
    await Assert.That(dead!.FailureReason).IsEqualTo(MIXED_STREAM_REASON);
    await Assert.That(dead.ErrorText).Contains("Probe.Contracts.UserIsTyping");
    await Assert.That(dead.ErrorText).Contains("Sourced events");
  }

  [Test]
  public async Task Store_FirstWriteMixesBothKinds_KeepsTheFirstKindAndDeadLettersTheOtherAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    // Version order is message id order, so the first event of the stream decides its kind.
    var ids = _orderedIds(3);
    var first = _ephemeral(streamId, ids[0]);
    var sourced = _sourced(streamId, ids[1]);
    var third = _ephemeral(streamId, ids[2]);

    await coordinator.StoreOutboxMessagesAsync([sourced, third, first], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    var expected = new List<(Guid EventId, long Version)> { (first.MessageId, 1L), (third.MessageId, 2L) };
    await Assert.That(await _versionsAsync(conn, streamId)).IsEquivalentTo(expected)
      .Because("the refused event takes no version, so the stream stays gap-free");
    await Assert.That(await _deadLetterAsync(conn, sourced.MessageId)).IsNotNull();
  }

  [Test]
  public async Task Store_SourcedEventOnACompactedStream_IsStoredAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var compacted = _sourced(streamId) with { MessageType = COMPACTED_TYPE, Flags = EventFlags.Compacted };
    await coordinator.StoreOutboxMessagesAsync([compacted], partitionCount: 10000);

    var sourced = _sourced(streamId);
    await coordinator.StoreOutboxMessagesAsync([sourced], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEquivalentTo([compacted.MessageId, sourced.MessageId])
      .Because("a compacted stream is a Sourced stream with a carry-forward head; it keeps receiving Sourced events");
    await Assert.That(await _deadLetterAsync(conn, sourced.MessageId)).IsNull();
  }

  [Test]
  public async Task Store_RefusedEventInABatch_LeavesTheOtherStreamsStoredAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var ephemeralStream = (Guid)TrackedGuid.New();
    var otherStream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_ephemeral(ephemeralStream)], partitionCount: 10000);

    var refused = _sourced(ephemeralStream);
    var unrelated = _sourced(otherStream);
    var unrelatedEphemeral = _ephemeral(ephemeralStream);
    await coordinator.StoreOutboxMessagesAsync([refused, unrelated, unrelatedEphemeral], partitionCount: 10000);

    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, otherStream)).IsEquivalentTo([unrelated.MessageId])
      .Because("a refusal is one row's failure; it must not fail the other streams in the same write");
    await Assert.That(await _storedIdsAsync(conn, ephemeralStream)).Contains(unrelatedEphemeral.MessageId);
    await Assert.That(await _outboxCountAsync(conn, unrelated.MessageId)).IsEqualTo(1L);
    await Assert.That(await _deadLetterAsync(conn, refused.MessageId)).IsNotNull();
  }

  [Test]
  public async Task CommitHandlerResult_HandlerEmitsASourcedEventOnAnEphemeralStream_CommitsAndDeadLettersTheEventAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_ephemeral(streamId)], partitionCount: 10000);
    var conn = await _openAsync(dbContext);
    var instanceId = (Guid)TrackedGuid.New();
    var inboxId = (Guid)TrackedGuid.New();
    await _seedLeasedInboxRowAsync(conn, inboxId, instanceId);

    var emitted = _sourced(streamId);
    await coordinator.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: inboxId,
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 4),
      NewOutboxMessages: [emitted]));

    await Assert.That(await _deadLetterAsync(conn, emitted.MessageId)).IsNotNull()
      .Because("the handler commit is a background write: the refused event is dead-lettered, not retried");
    await using var processed = conn.CreateCommand();
    processed.CommandText = "SELECT count(*) FROM wh_inbox_state WHERE message_id = @id AND processed_at IS NULL";
    processed.Parameters.AddWithValue("id", inboxId);
    await Assert.That((long)(await processed.ExecuteScalarAsync())!).IsEqualTo(0L)
      .Because("the handled message itself still completes; only the refused event fails");
  }

  // ---------------------------------------------------------------------------------------------
  // Direct commits (the caller awaits the store): the refusal surfaces to the caller.
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task Store_DirectCommitOfASourcedEventOnAnEphemeralStream_ThrowsMixedStreamEventExceptionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_ephemeral(streamId)], partitionCount: 10000);
    var sourced = _sourced(streamId) with { RaiseOnMixedStream = true };
    var unrelated = _sourced((Guid)TrackedGuid.New()) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([sourced, unrelated], partitionCount: 10000))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(thrown.EventId).IsEqualTo(sourced.MessageId);
    await Assert.That(thrown.Message).StartsWith("WHIZ133");
    await Assert.That(thrown.Message).Contains("Probe.Contracts.OrderPlaced");
    await Assert.That(thrown.Message).Contains("ephemeral events");
    await Assert.That(thrown.Message).Contains("own stream");
    var conn = await _openAsync(dbContext);
    await Assert.That(await _outboxCountAsync(conn, unrelated.MessageId)).IsEqualTo(0L)
      .Because("a direct commit is all or nothing: the caller sees the failure and nothing of the write is kept");
    await Assert.That(await _deadLetterAsync(conn, sourced.MessageId)).IsNull()
      .Because("the caller has the error in hand; dead-lettering it as well would report it twice");
  }

  [Test]
  public async Task Store_DirectCommitOfAnEphemeralEventOnASourcedStream_ThrowsMixedStreamEventExceptionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_sourced(streamId)], partitionCount: 10000);
    var ephemeral = _ephemeral(streamId) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([ephemeral], partitionCount: 10000))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.EventId).IsEqualTo(ephemeral.MessageId);
    await Assert.That(thrown.Message).Contains("Probe.Contracts.UserIsTyping");
    await Assert.That(thrown.Message).Contains("Sourced events");
  }

  [Test]
  public async Task Store_DirectCommitThatMixesBothKindsInItsFirstWrite_ThrowsMixedStreamEventExceptionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = _coordinator(dbContext);
    var streamId = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    var first = _ephemeral(streamId, ids[0]) with { RaiseOnMixedStream = true };
    var second = _sourced(streamId, ids[1]) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([first, second], partitionCount: 10000))
      .Throws<MixedStreamEventException>();

    await Assert.That(thrown!.EventId).IsEqualTo(second.MessageId);
    var conn = await _openAsync(dbContext);
    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEmpty();
  }

  // ---------------------------------------------------------------------------------------------
  // The inbox: a received event that would mix a stream is dead-lettered by the claim's emit chain.
  // ---------------------------------------------------------------------------------------------

  [Test]
  public async Task InboxChain_SourcedEventOnAnEphemeralStream_DeadLettersTheRowAndStoresTheRestAsync() {
    await using var dbContext = CreateDbContext();
    var streamId = (Guid)TrackedGuid.New();
    var otherStream = (Guid)TrackedGuid.New();
    var ephemeral = _received(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral);
    var conn = await _receiveAsync(dbContext, ephemeral);

    var refused = _received(streamId, SOURCED_TYPE, EventFlags.None);
    var unrelated = _received(otherStream, SOURCED_TYPE, EventFlags.None);
    await _receiveAsync(dbContext, refused, unrelated);

    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEquivalentTo([ephemeral.MessageId]);
    await Assert.That(await _storedIdsAsync(conn, otherStream)).IsEquivalentTo([unrelated.MessageId])
      .Because("the claim's other rows are stored as if the refused one were not there");
    var dead = await _deadLetterAsync(conn, refused.MessageId);
    await Assert.That(dead).IsNotNull();
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_inbox");
    await Assert.That(dead.FailureReason).IsEqualTo(MIXED_STREAM_REASON);
    await Assert.That(dead.ErrorText).StartsWith("WHIZ133");
    await using var inbox = conn.CreateCommand();
    inbox.CommandText = "SELECT count(*) FROM wh_inbox WHERE message_id = @id";
    inbox.Parameters.AddWithValue("id", refused.MessageId);
    await Assert.That((long)(await inbox.ExecuteScalarAsync())!).IsEqualTo(0L)
      .Because("the refused row leaves the inbox, so the claim never offers it again");
  }

  [Test]
  public async Task InboxChain_EphemeralEventOnASourcedStream_DeadLettersTheRowAsync() {
    await using var dbContext = CreateDbContext();
    var streamId = (Guid)TrackedGuid.New();
    var sourced = _received(streamId, SOURCED_TYPE, EventFlags.None);
    var conn = await _receiveAsync(dbContext, sourced);

    var refused = _received(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral);
    await _receiveAsync(dbContext, refused);

    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEquivalentTo([sourced.MessageId]);
    await Assert.That(await _deadLetterAsync(conn, refused.MessageId)).IsNotNull();
  }

  [Test]
  public async Task InboxChain_FirstClaimMixesBothKinds_KeepsTheFirstKindAsync() {
    await using var dbContext = CreateDbContext();
    var streamId = (Guid)TrackedGuid.New();
    var ids = _orderedIds(2);
    var first = _received(streamId, SOURCED_TYPE, EventFlags.None, ids[0]);
    var second = _received(streamId, EPHEMERAL_TYPE, EventFlags.Ephemeral, ids[1]);

    var conn = await _receiveAsync(dbContext, second, first);

    await Assert.That(await _storedIdsAsync(conn, streamId)).IsEquivalentTo([first.MessageId]);
    await Assert.That(await _deadLetterAsync(conn, second.MessageId)).IsNotNull();
  }

  // ---------------------------------------------------------------------------------------------

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext dbContext) =>
    new(dbContext, JsonContextRegistry.CreateCombinedOptions());

  private static Guid[] _orderedIds(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New()).OrderBy(id => id)];

  private static OutboxMessage _ephemeral(Guid streamId, Guid? id = null) =>
    CreateTestOutboxMessage(id ?? (Guid)TrackedGuid.New(), TOPIC, streamId, isEvent: true) with {
      MessageType = EPHEMERAL_TYPE,
      Flags = EventFlags.Ephemeral,
    };

  private static OutboxMessage _sourced(Guid streamId, Guid? id = null) =>
    CreateTestOutboxMessage(id ?? (Guid)TrackedGuid.New(), TOPIC, streamId, isEvent: true) with {
      MessageType = SOURCED_TYPE,
    };

  private static InboxMessage _received(Guid streamId, string messageType, EventFlags flags, Guid? id = null) {
    var messageId = id ?? (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(new { probe = true }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new InboxMessage {
      MessageId = messageId,
      HandlerName = "ProbeHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = messageType,
      StreamId = streamId,
      IsEvent = true,
      Flags = flags,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
  }

  // Stores the rows in the inbox, leases them to one instance and runs the claim's emit chain over them.
  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, params InboxMessage[] rows) {
    var coordinator = _coordinator(dbContext);
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = await _openAsync(dbContext);
    var instanceId = (Guid)TrackedGuid.New();
    await using (var lease = conn.CreateCommand()) {
      lease.CommandText = "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)";
      lease.Parameters.AddWithValue("inst", instanceId);
      lease.Parameters.AddWithValue("ids", rows.Select(r => r.MessageId).ToArray());
      if (await lease.ExecuteNonQueryAsync() != rows.Length) {
        throw new InvalidOperationException("the received rows were not all stored in the inbox; the probe proves nothing");
      }
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)";
      emit.Parameters.AddWithValue("inst", instanceId);
      _ = await emit.ExecuteScalarAsync();
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

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<List<Guid>> _storedIdsAsync(NpgsqlConnection conn, Guid streamId) =>
    [.. (await _versionsAsync(conn, streamId)).Select(v => v.EventId)];

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

  private static async Task<long> _outboxCountAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @id";
    cmd.Parameters.AddWithValue("id", messageId);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private sealed record DeadLetterRow(string SourceTable, int FailureReason, string ErrorText);

  private static async Task<DeadLetterRow?> _deadLetterAsync(NpgsqlConnection conn, Guid sourceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT source_table, failure_reason, error_text FROM wh_dead_letters WHERE source_id = @id";
    cmd.Parameters.AddWithValue("id", sourceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return new DeadLetterRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
  }
}
