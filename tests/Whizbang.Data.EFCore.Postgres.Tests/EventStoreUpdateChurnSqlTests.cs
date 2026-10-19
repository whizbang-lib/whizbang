// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1489: how often a <c>wh_event_store</c> row is rewritten after it is inserted.
///
/// <para>The event store is append-only apart from one write: the commit-order stamper sets
/// <c>commit_sequence</c> once, after the row's transaction has committed. These tests count every
/// row update the table takes, through a trigger that draws from a sequence, so an update made by a
/// transaction that later rolls back is counted too, exactly as <c>pg_stat_user_tables.n_tup_upd</c>
/// counts it. A second writer, a stamp that rewrites a stamped row, or a re-run that rewrites rows it
/// already wrote makes the count exceed one per event.</para>
///
/// <para>The one update cannot be HOT (heap-only): <c>commit_sequence</c> is the key of
/// <c>idx_event_store_commit_sequence</c>, part of the predicate of the four unstamped partial
/// indexes and an <c>INCLUDE</c> column of <c>idx_event_store_ephemeral_reap</c>, and changing any
/// column an index references makes Postgres write a new entry in every index.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence#one-update-per-event</docs>
[Category("Shard5")]
public class EventStoreUpdateChurnSqlTests : EFCoreTestBase {

  private const string EVENT_TYPE = "Whizbang.Tests.ChurnEvent, Whizbang.Tests";
  private const int STREAMS = 4;
  private const int EVENTS_PER_STREAM = 10;
  private const int EVENTS = STREAMS * EVENTS_PER_STREAM;

  [Test]
  public async Task Stamp_EventsWrittenThroughTheEmitChain_AreEachUpdatedExactlyOnceAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _installUpdateCounterAsync(conn);

    // Two emitting transactions, so a stamp call meets rows from more than one writer.
    var streams = Enumerable.Range(0, STREAMS).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    await _emitAsync(conn, streams[..2]);
    await _emitAsync(conn, streams[2..]);

    var inserted = await _scalarAsync(conn, "SELECT count(*) FROM wh_event_store");
    var afterInsert = await _updatesSoFarAsync(conn);

    // Drain in small batches so the drain takes several calls, then call again with nothing left.
    var stamped = await _drainAsync(conn, batchSize: 7);
    var idle = await _stampAsync(conn, batchSize: 1000);

    var unstamped = await _scalarAsync(conn, "SELECT count(*) FROM wh_event_store WHERE commit_sequence IS NULL");
    var updates = await _updatesSoFarAsync(conn);

    await Assert.That(inserted).IsEqualTo(EVENTS);
    await Assert.That(afterInsert).IsEqualTo(0L)
      .Because("the emit chain writes the pointer row once, with INSERT ... ON CONFLICT DO NOTHING, and never updates it");
    await Assert.That(stamped).IsEqualTo(EVENTS);
    await Assert.That(idle).IsEqualTo(0);
    await Assert.That(unstamped).IsEqualTo(0L);
    await Assert.That(updates).IsEqualTo((long)EVENTS)
      .Because("each event is stamped once and nothing else rewrites the row: one update per inserted event");
  }

  [Test]
  public async Task Stamp_InsideATransactionThatCommits_IsOneNonHotUpdatePerEventAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    await _emitAsync(conn, [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()]);
    var inserted = await _scalarAsync(conn, "SELECT count(*) FROM wh_event_store");

    // pg_stat_xact_user_tables reports this transaction's own counts, with no reporting delay.
    await using var tx = await conn.BeginTransactionAsync();
    var stamped = await _stampAsync(conn, batchSize: 1000);
    var (updated, hot) = await _transactionUpdateCountsAsync(conn);
    await tx.CommitAsync();

    await Assert.That(stamped).IsEqualTo((int)inserted);
    await Assert.That(updated).IsEqualTo(inserted)
      .Because("a stamp writes each row it stamps once");
    await Assert.That(hot).IsEqualTo(0L)
      .Because("commit_sequence is referenced by six indexes (key, INCLUDE and partial-index predicate), so the stamp can never be a heap-only update");
  }

  [Test]
  public async Task Reclassify_RunTwice_RewritesNoRowTheFirstRunAlreadyFlaggedAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    await _emitAsync(conn, [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()]);
    _ = await _drainAsync(conn, batchSize: 1000);
    await _installUpdateCounterAsync(conn);
    var storedType = await _storedEventTypeAsync(conn);

    // Startup reclassification runs on every boot that finds the type newly ephemeral.
    var first = await _reclassifyAsync(conn, storedType);
    var afterFirst = await _updatesSoFarAsync(conn);
    var second = await _reclassifyAsync(conn, storedType);
    var afterSecond = await _updatesSoFarAsync(conn);

    await Assert.That(first).IsEqualTo((long)(2 * EVENTS_PER_STREAM));
    await Assert.That(afterFirst).IsEqualTo((long)(2 * EVENTS_PER_STREAM))
      .Because("the first run flags each event once");
    await Assert.That(second).IsEqualTo(0L);
    await Assert.That(afterSecond).IsEqualTo(afterFirst)
      .Because("a re-run skips rows already flagged: the WHERE excludes flags & 8 = 8, so no row is rewritten with its own value");
  }

  // ---------- helpers ----------

  private static async Task<NpgsqlConnection> _openAsync(DbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  /// <summary>
  /// Counts row updates on wh_event_store. nextval is not transactional, so an update whose transaction
  /// rolls back still counts, which is how n_tup_upd counts it.
  /// </summary>
  private static async Task _installUpdateCounterAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, """
      CREATE SEQUENCE test_event_store_updates MINVALUE 0 START 0;
      CREATE FUNCTION test_count_event_store_update() RETURNS TRIGGER AS $$
      BEGIN
        PERFORM nextval('test_event_store_updates');
        RETURN NULL;
      END;
      $$ LANGUAGE plpgsql;
      CREATE TRIGGER test_count_event_store_update
        AFTER UPDATE ON wh_event_store
        FOR EACH ROW EXECUTE FUNCTION test_count_event_store_update();
      """);
  }

  private static async Task<long> _updatesSoFarAsync(NpgsqlConnection conn) =>
    await _scalarAsync(conn, "SELECT CASE WHEN is_called THEN last_value + 1 ELSE 0 END FROM test_event_store_updates");

  /// <summary>Writes EVENTS_PER_STREAM events per stream through the emit chain, in one transaction.</summary>
  private static async Task _emitAsync(NpgsqlConnection conn, Guid[] streamIds) {
    var instanceId = (Guid)TrackedGuid.New();
    var messageIds = new List<Guid>();
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var streamId in streamIds) {
      for (var i = 0; i < EVENTS_PER_STREAM; i++) {
        var messageId = (Guid)TrackedGuid.New();
        messageIds.Add(messageId);
        await using var insert = conn.CreateCommand();
        insert.CommandText = """
          INSERT INTO wh_outbox
            (message_id, destination, message_type, envelope_type, event_data, metadata,
             status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event,
             created_at)
          VALUES
            (@id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
             1, 0, @inst, NOW() + INTERVAL '5 minutes', 0, @sid, true, NOW())
          """;
        insert.Parameters.AddWithValue("id", messageId);
        insert.Parameters.AddWithValue("type", EVENT_TYPE);
        insert.Parameters.AddWithValue("inst", instanceId);
        insert.Parameters.AddWithValue("sid", streamId);
        await insert.ExecuteNonQueryAsync();
      }
    }
    await using var emit = conn.CreateCommand();
    emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, NOW() + INTERVAL '5 minutes', NOW(), 10000)";
    emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, messageIds.ToArray());
    emit.Parameters.AddWithValue("inst", instanceId);
    await emit.ExecuteNonQueryAsync();
    await tx.CommitAsync();
  }

  private static async Task<int> _stampAsync(NpgsqlConnection conn, int batchSize) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(@bs)";
    cmd.Parameters.AddWithValue("bs", batchSize);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>Stamps until a call stamps nothing. Bounded: the test writes a fixed number of rows.</summary>
  private static async Task<int> _drainAsync(NpgsqlConnection conn, int batchSize) {
    var total = 0;
    for (var call = 0; call <= EVENTS; call++) {
      var stamped = await _stampAsync(conn, batchSize);
      if (stamped == 0) {
        break;
      }
      total += stamped;
    }
    return total;
  }

  private static async Task<(long Updated, long Hot)> _transactionUpdateCountsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT n_tup_upd, n_tup_hot_upd FROM pg_stat_xact_user_tables
      WHERE relid = 'wh_event_store'::regclass
      """;
    await using var reader = await cmd.ExecuteReaderAsync();
    _ = await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  private static async Task<string> _storedEventTypeAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT DISTINCT event_type FROM wh_event_store";
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _reclassifyAsync(NpgsqlConnection conn, string eventType) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT events_reclassified FROM reclassify_events_ephemeral(@names)";
    string[] names = [eventType];
    cmd.Parameters.AddWithValue("names", NpgsqlDbType.Array | NpgsqlDbType.Text, names);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
