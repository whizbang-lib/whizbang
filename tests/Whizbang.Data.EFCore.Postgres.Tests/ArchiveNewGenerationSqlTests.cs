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
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <see cref="AppendToArchivedPolicy.NewGeneration"/> on every write path (#1734, owner decision on #1738). An append to a
/// stream archived whole starts a new generation of it: the event is stored at the tombstone's last version plus one, the
/// stream becomes Active with its archive as a prefix, and each perspective resumes after its parked cursor (the cursor
/// stays parked; <c>GetPerspectiveCursorAsync</c> reads it when the perspective has no live cursor). When a perspective
/// holds no snapshot at its parked cursor, the append falls back to <see cref="AppendToArchivedPolicy.AutoRestore"/>.
/// </summary>
/// <docs>fundamentals/events/archive-store#new-generation</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/280_FinalSnapshotAndScheduledDisposition.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class ArchiveNewGenerationSqlTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Probe.Archive.Deposited, Probe.Archive";
  private const string STREAM_TYPE = "Probe.Archive.Deposited";
  private const string PERSPECTIVE = "Probe.Balance";
  private const int AWAITING_RESTORE_REASON = 27;

  private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

  // ===== A direct append =====

  [Test]
  public async Task DirectAppend_EveryParkedCursorHasItsSnapshot_StoresAtTheNextVersion_AndTheArchiveBecomesAPrefixAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var ids = await _appendAsync(store, stream, 3);
    var conn = await _openAsync(context);
    await _cursorAsync(conn, stream, ids[2]);
    await _snapshotAsync(conn, stream, ids[2], 3);
    await _setPolicyAsync(conn, await _streamTypeAsync(conn, stream), "NewGeneration");
    await _archiveWholeAsync(stream);

    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));

    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([4L])
      .Because("the archive holds versions 1 to 3, so the new generation continues at 4 and reuses none");
    await Assert.That(await _ledgerAsync(conn, stream)).IsEqualTo("Active|4|archived")
      .Because("the stream is live again and its archive is now a prefix, still named by the locator");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(1L).Because("the parked cursor stays: the perspective resumes after it");
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_event_archive WHERE stream_id = @s", ("s", stream)))
      .IsEqualTo(3L).Because("nothing is restored: the earlier generation stays archived");
  }

  [Test]
  public async Task DirectAppend_AParkedCursorWithoutASnapshotAtIt_FallsBackToAutoRestoreAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var ids = await _appendAsync(store, stream, 3);
    var conn = await _openAsync(context);
    await _cursorAsync(conn, stream, ids[2]);
    await _snapshotAsync(conn, stream, ids[1], 2);
    await _setPolicyAsync(conn, await _streamTypeAsync(conn, stream), "NewGeneration");
    await _archiveWholeAsync(stream);

    var thrown = await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream })))
      .Throws<StreamArchivedException>();

    await Assert.That(thrown!.Policy).IsEqualTo(AppendToArchivedPolicy.AutoRestore)
      .Because("a snapshot behind the cursor is not where the perspective stopped, so the stream must be restored instead");
    await Assert.That(await _versionsAsync(conn, stream)).IsEmpty();
    await Assert.That(await _ledgerAsync(conn, stream)).IsEqualTo("Archived|3|archived");
  }

  [Test]
  public async Task DirectAppend_NoPerspectiveEverConsumedTheStream_StartsANewGenerationAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    _ = await _appendAsync(store, stream, 2);
    var conn = await _openAsync(context);
    await _setPolicyAsync(conn, await _streamTypeAsync(conn, stream), "NewGeneration");
    await _archiveWholeAsync(stream);

    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));

    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([3L]);
  }

  [Test]
  public async Task DirectAppend_SealedAndArchived_TheSealStillRefusesAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    _ = await _appendAsync(store, stream, 1);
    var conn = await _openAsync(context);
    await _setPolicyAsync(conn, await _streamTypeAsync(conn, stream), "NewGeneration");
    await _executeAsync(conn, "SELECT wh_finalize_stream(@s, 'ended', NOW())", ("s", stream));
    await _archiveWholeAsync(stream);

    await Assert.That(async () =>
        await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream })))
      .Throws<StreamSealedException>();
  }

  // ===== The outbox =====

  [Test]
  public async Task OutboxAppend_StoresTheEventAtTheNextVersion_AndDeadLettersNothingAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    _ = await _emitOutboxAsync(conn, _t0, stream, stream);
    await _setPolicyAsync(conn, STREAM_TYPE, "NewGeneration");
    await _archiveWholeAsync(stream);

    var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1), stream);

    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([3L]);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_dead_letters WHERE source_id = @id", ("id", ids[0]))).IsEqualTo(0L);
  }

  [Test]
  public async Task OutboxAppend_AParkedCursorWithoutASnapshotAtIt_IsDeadLetteredAwaitingRestoreAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    var first = await _emitOutboxAsync(conn, _t0, stream);
    await _cursorAsync(conn, stream, first[0]);
    await _setPolicyAsync(conn, STREAM_TYPE, "NewGeneration");
    await _archiveWholeAsync(stream);

    var ids = await _emitOutboxAsync(conn, _t0.AddMinutes(1), stream);

    await Assert.That(await _versionsAsync(conn, stream)).IsEmpty();
    await Assert.That(await _scalarAsync(conn, "SELECT failure_reason FROM wh_dead_letters WHERE source_id = @id", ("id", ids[0])))
      .IsEqualTo((long)AWAITING_RESTORE_REASON);
    await Assert.That(await _scalarAsync(conn, "SELECT count(*) FROM wh_archive_restore_requests WHERE stream_id = @s AND state = 'Pending'", ("s", stream)))
      .IsEqualTo(1L);
  }

  [Test]
  public async Task StoreOutboxMessages_AnAwaitedWrite_IsStoredAtTheNextVersionAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, JsonContextRegistry.CreateCombinedOptions());
    var stream = (Guid)TrackedGuid.New();
    await coordinator.StoreOutboxMessagesAsync([_outbox(stream)], partitionCount: 10000);
    var conn = await _openAsync(dbContext);
    await _setPolicyAsync(conn, STREAM_TYPE, "NewGeneration");
    await _archiveWholeAsync(stream);

    await coordinator.StoreOutboxMessagesAsync([_outbox(stream) with { RaiseOnMixedStream = true }], partitionCount: 10000);

    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([2L]);
  }

  // ===== The inbox =====

  [Test]
  public async Task InboxAppend_StoresTheReceivedEventAtTheNextVersionAsync() {
    await using var dbContext = CreateDbContext();
    var stream = (Guid)TrackedGuid.New();
    var first = await _receiveAsync(dbContext, _received(stream));
    await _setPolicyAsync(first, await _streamTypeAsync(first, stream), "NewGeneration");
    await _archiveWholeAsync(stream);

    var conn = await _receiveAsync(dbContext, _received(stream));

    await Assert.That(await _versionsAsync(conn, stream)).IsEquivalentTo([2L]);
  }

  // ===== The cursor a perspective resumes from =====

  [Test]
  public async Task GetPerspectiveCursor_WithNoLiveCursor_ReadsTheParkedOne_AndALiveOneWinsAsync() {
    var stream = (Guid)TrackedGuid.New();
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var ids = await _appendAsync(store, stream, 2);
    var conn = await _openAsync(context);
    await _cursorAsync(conn, stream, ids[1]);
    await _snapshotAsync(conn, stream, ids[1], 2);
    await _setPolicyAsync(conn, await _streamTypeAsync(conn, stream), "NewGeneration");
    await _archiveWholeAsync(stream);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(context, JsonContextRegistry.CreateCombinedOptions());

    var parked = await coordinator.GetPerspectiveCursorAsync(stream, PERSPECTIVE);
    var batch = await coordinator.GetPerspectiveCursorsBatchAsync([stream]);
    var unknown = await coordinator.GetPerspectiveCursorAsync(stream, "Probe.Other");

    await Assert.That(parked!.LastEventId).IsEqualTo(ids[1])
      .Because("the perspective resumes after the event its parked cursor names, so the new generation applies onto its row");
    await Assert.That(batch.Single().LastEventId).IsEqualTo(ids[1]);
    await Assert.That(unknown).IsNull().Because("the control: a perspective with no cursor anywhere starts from nothing");

    await store.AppendAsync(stream, _envelope(new ArchivePolicyProbeDeposited { AccountId = stream }));
    var newest = (await _versionIdsAsync(conn, stream))[0];
    await _cursorAsync(conn, stream, newest);

    await Assert.That((await coordinator.GetPerspectiveCursorAsync(stream, PERSPECTIVE))!.LastEventId).IsEqualTo(newest)
      .Because("once the perspective has moved on, its live cursor is where it stands");
  }

  // =================================================================================================

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

  private static async Task<Guid[]> _appendAsync(EFCoreEventStore<WorkCoordinationDbContext> store, Guid stream, int count) {
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      var envelope = _envelope(new ArchivePolicyProbeDeposited { AccountId = stream });
      await store.AppendAsync(stream, envelope);
      ids[i] = envelope.MessageId.Value;
    }
    return ids;
  }

  private static Task _cursorAsync(NpgsqlConnection conn, Guid stream, Guid eventId) =>
    _executeAsync(conn, """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES (@s, @p, @e, 0, NOW())
      ON CONFLICT (stream_id, perspective_name) DO UPDATE SET last_event_id = EXCLUDED.last_event_id
      """, ("s", stream), ("p", PERSPECTIVE), ("e", eventId));

  private static Task _snapshotAsync(NpgsqlConnection conn, Guid stream, Guid eventId, long version) =>
    _executeAsync(conn, """
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number, snapshot_version)
      VALUES (@s, @p, @e, '{"n": 1}'::jsonb, @v, @v)
      """, ("s", stream), ("p", PERSPECTIVE), ("e", eventId), ("v", version));

  private static Task _setPolicyAsync(NpgsqlConnection conn, string streamType, string policy) =>
    _executeAsync(conn, "SELECT wh_sync_stream_type_policies(ARRAY[@t], ARRAY[@p])", ("t", streamType), ("p", policy));

  private static async Task<string> _streamTypeAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stream_type FROM wh_stream_lifecycle WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string> _ledgerAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT concat_ws('|', state, last_version, CASE WHEN archive_locator IS NULL THEN 'none' ELSE 'archived' END)
      FROM wh_stream_lifecycle WHERE stream_id = @s
      """;
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static MessageEnvelope<TMessage> _envelope<TMessage>(TMessage payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static OutboxMessage _outbox(Guid stream) =>
    CreateTestOutboxMessage((Guid)TrackedGuid.New(), "archive-topic", stream, isEvent: true) with { MessageType = EVENT_TYPE };

  // Queues one event per stream given in the outbox and runs the outbox emit chain over them, as store_outbox_messages does.
  private static async Task<Guid[]> _emitOutboxAsync(NpgsqlConnection conn, DateTimeOffset now, params Guid[] streams) {
    var instanceId = (Guid)TrackedGuid.New();
    Guid[] ids = [.. streams.Select(_ => (Guid)TrackedGuid.New()).Order()];
    for (var i = 0; i < streams.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'archive-topic', @type, 'env', jsonb_build_object('p', jsonb_build_object('n', 1)),
           '{}'::jsonb, NULL, 1, 0, @inst, @lease, 0, @sid, true, 0, @now)
        """,
        ("id", ids[i]), ("type", EVENT_TYPE), ("inst", instanceId), ("lease", now.AddMinutes(5)), ("sid", streams[i]), ("now", now));
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

  private static async Task<List<Guid>> _versionIdsAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version";
    cmd.Parameters.AddWithValue("s", stream);
    var rows = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.GetGuid(0));
    }
    return rows;
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
