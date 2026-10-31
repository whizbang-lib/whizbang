// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>An ordinary Sourced event on an archive-policy probe stream.</summary>
public sealed record ArchivePolicyProbeDeposited : IEvent {
  /// <summary>The account, which is the stream.</summary>
  [StreamId]
  public Guid AccountId { get; init; }
}

/// <summary>
/// The append-to-archived policy on every write path (#1738, WS-3 S9, migration 271). A stream archived whole keeps a
/// tombstone that holds its versions; an append to it is refused per event, never per batch, under the policy its type
/// declares: <see cref="AppendToArchivedPolicy.Refuse"/> (the default) raises <see cref="StreamArchivedException"/> to an
/// awaiting caller and dead-letters a background write with <see cref="MessageFailureReason.StreamArchived"/>;
/// <see cref="AppendToArchivedPolicy.AutoRestore"/> raises the restore state to an awaiting caller (which restores and
/// retries) and dead-letters a background write with <see cref="MessageFailureReason.AwaitingRestore"/>, queuing the
/// stream's restore request in the same transaction.
/// </summary>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/271_ArchiveReadThroughAndAppendPolicy.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class ArchiveAppendPolicySqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Probe.Archive.Deposited, Probe.Archive";
  private const string STREAM_TYPE = "Probe.Archive.Deposited";
  private const int STREAM_ARCHIVED_REASON = 26;
  private const int AWAITING_RESTORE_REASON = 27;
  private const int HOLD_FOR_REVIEW = 2;
  private const int HOLD_INDEFINITELY = 2;

  private static readonly DateTimeOffset _t0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

  // ===== A direct append =====

  [Test]
  public async Task DirectAppend_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));
    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));
    await _archiveWholeAsync(stream);

    var thrown = await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream })))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.ArchivedThroughVersion).IsEqualTo(2L)
      .Because("a directly appended stream starts at version 1 (#1913), so two events end at version 2");
    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.Refuse)
      .Because("a stream type with no policy refuses");
    await Assert.That(thrown.Message).IsEqualTo(
      $"Stream {stream} is archived through version 2 and refuses appends (AppendToArchived Refuse).");
    var conn = await _openAsync(context);
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(0L)
      .Because("nothing is hot after a whole-stream archive, and the refused append stores nothing");

    // The control: the same append to an open stream is stored.
    var open = (Guid)TrackedGuid.New();
    await store.AppendAsync(open, _envelope(new ArchivePolicyProbeDeposited { AccountId = open }));
    await Assert.That(await _storedCountAsync(conn, open)).IsEqualTo(1L);
  }

  [Test]
  public async Task DirectAppend_AutoRestore_RaisesTheRestoreState_AndStoresNothingAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));
    var conn = await _openAsync(context);
    await _setPolicyAsync(conn, await _streamTypeAsync(conn, stream), "AutoRestore");
    await _archiveWholeAsync(stream);

    var thrown = await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream })))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.Policy).IsEqualTo(AppendToArchivedPolicy.AutoRestore)
      .Because("the store tells the caller to restore the stream and retry; the driver's store alone does not restore");
    await Assert.That(thrown.StreamId).IsEqualTo(stream);
    await Assert.That(await _storedCountAsync(conn, stream)).IsEqualTo(0L);
    await Assert.That(await _requestStateAsync(conn, stream)).IsNull()
      .Because("a caller that awaits the write restores inline; only a background write queues a request");
  }

  [Test]
  public async Task PrefixArchivedStream_TakesAppendsAsUsual_OnEveryPathAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (stream, EVENT_TYPE), (stream, EVENT_TYPE), (stream, EVENT_TYPE));
    var archived = await _store().WriteAsync(ArchiveBatch.Prefix(stream, 2));
    await Assert.That(archived.Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1), (stream, EVENT_TYPE));
    await _executeAsync(conn,
      "SELECT wh_append_event(gen_random_uuid(), @s, @s, 'Agg', 'T', '{}'::jsonb, '{}'::jsonb, NULL::jsonb, NOW())",
      ("s", stream));

    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([3L, 4L, 5L])
      .Because("a prefix archive leaves the stream Active with a hot tail, so it numbers from the tail as before");
    await Assert.That(await _deadLetterAsync(conn, ids[0])).IsNull();
  }

  [Test]
  public async Task SealedAndArchived_TheSealsRefusalWinsAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));
    await new PostgresStreamFinalizer(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync)
      .FinalizeAsync(stream, "Closed");
    await _archiveWholeAsync(stream);

    await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream })))
      .Throws<StreamSealedException>()
      .Because("a sealed stream has ended whether or not it is archived; restoring it would not let the append in");
  }

  // ===== The outbox chain: a write nobody awaits =====

  [Test]
  public async Task OutboxAppend_Refuse_DeadLettersThatEvent_AndTheRestOfTheBatchCommitsAsync() {
    await using var conn = await _openAsync();
    var archived = (Guid)TrackedGuid.New();
    var open = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (archived, EVENT_TYPE), (archived, EVENT_TYPE));
    await _archiveWholeAsync(archived);

    var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1), (archived, EVENT_TYPE), (open, EVENT_TYPE), (open, EVENT_TYPE));

    await Assert.That(await _storedCountAsync(conn, archived)).IsEqualTo(0L)
      .Because("the tombstone holds versions 1 and 2; an event numbered from nothing hot would reuse one");
    await Assert.That(await _storedCountAsync(conn, open)).IsEqualTo(2L)
      .Because("a refusal is one event's failure; the other streams of the batch are stored (262's trigger failed them all)");
    var dead = await _deadLetterAsync(conn, ids[0]);
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_outbox");
    await Assert.That(dead.FailureReason).IsEqualTo(STREAM_ARCHIVED_REASON);
    await Assert.That(dead.ErrorText).IsEqualTo(
      $"Stream {archived} is archived through version 2 and refuses appends (AppendToArchived Refuse).");
    await Assert.That(dead.OperatorDisposition).IsEqualTo(0)
      .Because("a refused write is an ordinary dead letter, which the recovery policy holds for review");
    await Assert.That(await _requestStateAsync(conn, archived)).IsNull().Because("Refuse queues no restore");
    await Assert.That(await _deadLetterAsync(conn, ids[1])).IsNull();
  }

  [Test]
  public async Task OutboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesOneRequestPerStreamAsync() {
    await using var conn = await _openAsync();
    var archived = (Guid)TrackedGuid.New();
    var refusing = (Guid)TrackedGuid.New();
    var open = (Guid)TrackedGuid.New();
    await _emitOutboxAsync(conn, _t0, (archived, EVENT_TYPE));
    await _emitOutboxAsync(conn, _t0, (refusing, "Probe.Archive.Other, Probe.Archive"));
    await _setPolicyAsync(conn, STREAM_TYPE, "AutoRestore");
    await _archiveWholeAsync(archived);
    await _archiveWholeAsync(refusing);

    var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1),
      (archived, EVENT_TYPE), (archived, EVENT_TYPE), (refusing, EVENT_TYPE), (open, EVENT_TYPE));

    foreach (var id in ids[..2]) {
      var dead = await _deadLetterAsync(conn, id);
      await Assert.That(dead!.FailureReason).IsEqualTo(AWAITING_RESTORE_REASON);
      await Assert.That(dead.ErrorText).IsEqualTo(
        $"Stream {archived} is archived through version 1; the append waits for its restore (AppendToArchived AutoRestore).");
      await Assert.That(dead.RecoveryStatus).IsEqualTo(HOLD_FOR_REVIEW);
      await Assert.That(dead.OperatorDisposition).IsEqualTo(HOLD_INDEFINITELY)
        .Because("the dead letter is held by its stream's restore request, out of every scan, replay and campaign");
    }
    await Assert.That(await _requestStateAsync(conn, archived)).IsEqualTo("Pending");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_archive_restore_requests WHERE stream_id = @s", ("s", archived)))
      .IsEqualTo(1L).Because("one request per stream, however many of its events were refused");
    await Assert.That((await _deadLetterAsync(conn, ids[2]))!.FailureReason).IsEqualTo(STREAM_ARCHIVED_REASON)
      .Because("the policy is the stream's type's: a stream whose type declares nothing refuses, in the same batch");
    await Assert.That(await _requestStateAsync(conn, refusing)).IsNull();
    await Assert.That(await _storedCountAsync(conn, open)).IsEqualTo(1L);
  }

  // ===== store_outbox_messages: a write its caller awaits =====

  [Test]
  public async Task StoreOutboxMessages_AnAwaitedWrite_Refuse_ThrowsStreamArchivedException_AndStoresNothingAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    await _archiveWholeAsync(stream);
    var awaited = _outbox(stream) with { RaiseOnMixedStream = true };
    var unrelated = _outbox((Guid)TrackedGuid.New()) with { RaiseOnMixedStream = true };

    var thrown = await Assert.That(async () =>
        await coordinator.StoreOutboxMessagesAsync([awaited, unrelated], partitionCount: 10000))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(thrown.Policy).IsEqualTo(AppendToArchivedPolicy.Refuse);
    var conn = await _openAsync(dbContext);
    await Assert.That(await _outboxCountAsync(conn, unrelated.MessageId)).IsEqualTo(0L)
      .Because("an awaited write is all or nothing: the caller sees the failure and nothing of the write is kept");
    await Assert.That(await _deadLetterAsync(conn, awaited.MessageId)).IsNull()
      .Because("the caller has the refusal in hand; dead-lettering it as well would report it twice");
  }

  [Test]
  public async Task StoreOutboxMessages_AnAwaitedWrite_AutoRestore_RaisesTheRestoreStateAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    var conn = await _openAsync(dbContext);
    await _setPolicyAsync(conn, STREAM_TYPE, "AutoRestore");
    await _archiveWholeAsync(stream);
    var awaited = _outbox(stream) with { RaiseOnMixedStream = true };
    var notAwaited = _outbox(stream);

    var thrown = await Assert.That(async () => await coordinator.StoreOutboxMessagesAsync([awaited], partitionCount: 10000))
      .Throws<StreamArchivedException>();
    await coordinator.StoreOutboxMessagesAsync([notAwaited], partitionCount: 10000);

    await Assert.That(thrown!.Policy).IsEqualTo(AppendToArchivedPolicy.AutoRestore);
    await Assert.That(await _deadLetterAsync(conn, awaited.MessageId)).IsNull();
    await Assert.That((await _deadLetterAsync(conn, notAwaited.MessageId))!.FailureReason).IsEqualTo(AWAITING_RESTORE_REASON)
      .Because("the control: the same write nobody awaits is dead-lettered awaiting the restore it queues");
    await Assert.That(await _requestStateAsync(conn, stream)).IsEqualTo("Pending");
  }

  // ===== The inbox chain =====

  [Test]
  public async Task InboxAppend_Refuse_DeadLettersTheRow_AndStoresTheRestAsync() {
    await using var dbContext = CreateDbContext();
    var archived = (Guid)TrackedGuid.New();
    var open = (Guid)TrackedGuid.New();
    await _receiveAsync(dbContext, _received(archived));
    await _archiveWholeAsync(archived);
    var refused = _received(archived);
    var stored = _received(open);

    var conn = await _receiveAsync(dbContext, refused, stored);

    await Assert.That(await _storedCountAsync(conn, archived)).IsEqualTo(0L);
    await Assert.That(await _storedCountAsync(conn, open)).IsEqualTo(1L);
    var dead = await _deadLetterAsync(conn, refused.MessageId);
    await Assert.That(dead!.SourceTable).IsEqualTo("wh_inbox");
    await Assert.That(dead.FailureReason).IsEqualTo(STREAM_ARCHIVED_REASON);
    await Assert.That(await _scalarAsync(conn,
        "SELECT count(*) FROM wh_inbox_state WHERE message_id = @id AND processed_at IS NOT NULL AND failure_reason = 26",
        ("id", refused.MessageId)))
      .IsEqualTo(1L).Because("the lease row is completed in place, so the claim never offers it again");
  }

  [Test]
  public async Task InboxAppend_AutoRestore_DeadLettersAwaitingRestore_AndQueuesTheRequestAsync() {
    await using var dbContext = CreateDbContext();
    var archived = (Guid)TrackedGuid.New();
    var first = await _receiveAsync(dbContext, _received(archived));
    await _setPolicyAsync(first, await _streamTypeAsync(first, archived), "AutoRestore");
    await _archiveWholeAsync(archived);
    var refused = _received(archived);

    var conn = await _receiveAsync(dbContext, refused);

    var dead = await _deadLetterAsync(conn, refused.MessageId);
    await Assert.That(dead!.FailureReason).IsEqualTo(AWAITING_RESTORE_REASON);
    await Assert.That(dead.RecoveryStatus).IsEqualTo(HOLD_FOR_REVIEW);
    await Assert.That(dead.OperatorDisposition).IsEqualTo(HOLD_INDEFINITELY);
    await Assert.That(await _requestStateAsync(conn, archived)).IsEqualTo("Pending")
      .Because("the request is queued in the claim's own transaction, with the dead letter it holds");
  }

  // =================================================================================================

  private sealed record DeadLetterRow(string SourceTable, int FailureReason, string ErrorText, int RecoveryStatus, int OperatorDisposition);

  private PostgresTableArchiveStore _store() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private async Task _archiveWholeAsync(Guid stream) {
    var result = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static Task _setPolicyAsync(NpgsqlConnection conn, string streamType, string policy) =>
    _executeAsync(conn, "SELECT wh_sync_stream_type_policies(ARRAY[@t], ARRAY[@p])", ("t", streamType), ("p", policy));

  private static async Task<string> _streamTypeAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_type FROM wh_stream_lifecycle WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string?> _requestStateAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT state FROM wh_archive_restore_requests WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  private static MessageEnvelope<TMessage> _envelope<TMessage>(TMessage payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static OutboxMessage _outbox(Guid stream) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), "archive-topic", stream, isEvent: true) with { MessageType = EVENT_TYPE };

  // Queues the events in the outbox and runs the outbox emit chain over them, as store_outbox_messages does.
  private static async Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, DateTimeOffset now, params (Guid Stream, string Type)[] events) {
    var instanceId = (Guid)TrackedGuid.New();
    Guid[] ids = [.. events.Select(_ => (Guid)TrackedGuid.New()).Order()];
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'archive-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           '{}'::jsonb, NULL, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", ids[i]), ("type", events[i].Type), ("inst", instanceId), ("lease", now.AddMinutes(5)),
        ("sid", events[i].Stream), ("now", now));
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instanceId);
    emit.Parameters.AddWithValue("lease", now.AddMinutes(5));
    emit.Parameters.AddWithValue(nameof(now), now);
    _ = await emit.ExecuteScalarAsync();
    return ids;
  }

  private static InboxMessage _received(Guid streamId) {
    var messageId = (Guid)TrackedGuid.New();
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
      MessageType = EVENT_TYPE,
      StreamId = streamId,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
    };
  }

  // Stores the rows in the inbox, leases them to one instance and runs the claim's emit chain over them.
  private static async Task<NpgsqlConnection> _receiveAsync(WorkCoordinationDbContext dbContext, params InboxMessage[] rows) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    var conn = await _openAsync(dbContext);
    var instanceId = (Guid)TrackedGuid.New();
    Guid[] ids = [.. rows.Select(r => r.MessageId)];
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', NOW(), 4)",
      ("inst", instanceId));
    return conn;
  }

  private static Task<long> _storedCountAsync(NpgsqlConnection conn, Guid stream) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = @s", ("s", stream));

  private static Task<long> _outboxCountAsync(NpgsqlConnection conn, Guid messageId) =>
    _scalarAsync(conn, "SELECT count(*) FROM wh_outbox WHERE message_id = @id", ("id", messageId));

  private static async Task<List<long>> _versionsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT version FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetInt64(0));
    }
    return rows;
  }

  private static async Task<DeadLetterRow?> _deadLetterAsync(NpgsqlConnection conn, Guid sourceId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      "SELECT source_table, failure_reason, error_text, recovery_status, operator_disposition FROM wh_dead_letters WHERE source_id = @id";
    cmd.Parameters.AddWithValue("id", sourceId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return new DeadLetterRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
