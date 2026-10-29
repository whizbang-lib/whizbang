// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #1745 (WS-4 C4.11): which framework tables get a BRIN index, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// A BRIN index stores one minimum and maximum per range of heap pages, so it is only as selective as the table's
/// physical order follows the column: <c>pg_stats.correlation</c> near 1. The four candidates were
/// <c>wh_event_store</c> (<c>created_at</c>, <c>commit_sequence</c>), <c>wh_log</c> (<c>logged_at</c>),
/// <c>wh_lifecycle_completions</c> (<c>completed_at</c>) and <c>wh_message_deduplication</c>
/// (<c>first_seen_at</c>). Each is seeded here in its production write order and measured.
/// </para>
/// <para>
/// The event store keeps its rows (only expired ephemeral pointers and purged streams leave), so its order holds
/// through the stamper's one non-HOT update per event. The other three are retention tables: rows leave from the
/// oldest end, vacuum frees those pages, and the next day's rows fill them, so one retention pass breaks the order
/// and every pass after it rotates it further. Those tables keep their B-tree, which serves the retention delete at
/// any order.
/// </para>
/// <para>
/// The one index added, <c>idx_event_store_created_at_brin</c>, serves the startup orphaned-lifecycle query, a
/// thirty-minute window over a store that keeps every event and had no index reaching it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#brin-indexes</docs>
[Category("Shard5")]
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class BrinIndexCandidatesTests : EFCoreTestBase {
  /// <summary>The correlation at or above which a column's order is near enough to 1 for a BRIN index.</summary>
  private const double NEAR_ONE = 0.9;

  private const string BRIN_INDEX = "idx_event_store_created_at_brin";
  private const string EVENT_TYPE = "Whizbang.Tests.BrinEvent, Whizbang.Tests";
  private const string PERSPECTIVE = "Whizbang.Tests.BrinPerspective";

  // The store's history: one emitting batch an hour for two days, so the thirty-minute window holds one batch.
  private const int BATCHES = 48;
  private const int STREAMS_PER_BATCH = 50;
  private const int EVENTS_PER_STREAM = 10;
  private const int VACUUM_EVERY = 4;

  // A retention table's history: the table's default retention in days, this many rows a day.
  private const int ROWS_PER_DAY = 1000;

  private static readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  /// <summary>
  /// The event store, written through the emit chain and stamped after each batch as the stamper does, with vacuum
  /// freeing the pre-stamp row versions between batches: <c>created_at</c> and <c>commit_sequence</c> both stay
  /// near 1.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-event-store-created-at</docs>
  [Test]
  public async Task TheEventStore_InItsProductionWriteOrder_CorrelatesNearOneAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _seedEventStoreAsync(conn);

    var createdAt = await _correlationAsync(conn, "wh_event_store", "created_at");
    var commitSequence = await _correlationAsync(conn, "wh_event_store", "commit_sequence");

    await Assert.That(createdAt).IsGreaterThanOrEqualTo(NEAR_ONE)
      .Because("events are appended in time order and the one stamp per event writes its new version near the tail");
    await Assert.That(commitSequence).IsGreaterThanOrEqualTo(NEAR_ONE)
      .Because("the stamper numbers events in commit order, which is the order they were appended");
  }

  /// <summary>
  /// A retention table correlates near 1 while it only grows (the control), and falls below the bar after one
  /// retention pass at the table's default retention: the freed oldest pages take the newest rows.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-retention-tables</docs>
  [Test]
  [Arguments("wh_message_deduplication", "first_seen_at", 30)]
  [Arguments("wh_lifecycle_completions", "completed_at", 7)]
  [Arguments("wh_log", "logged_at", 14)]
  public async Task ARetentionTable_FallsBelowNearOne_AfterOneRetentionPassAsync(string table, string column, int retentionDays) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _holdAutovacuumOffAsync(conn, table);

    for (var day = 0; day < retentionDays; day++) {
      await _insertRetentionDayAsync(conn, table, day);
    }
    var growing = await _correlationAsync(conn, table, column);

    // One day later: the retention delete takes the day that left the window, autovacuum frees its pages, and
    // the new day's rows are written into them.
    await _executeAsync(conn, $"DELETE FROM {table} WHERE {column} < @cutoff", ("cutoff", _start.AddDays(1)));
    await _executeAsync(conn, $"VACUUM {table}");
    await _insertRetentionDayAsync(conn, table, retentionDays);
    var afterOnePass = await _correlationAsync(conn, table, column);

    await Assert.That(growing).IsGreaterThanOrEqualTo(NEAR_ONE)
      .Because("the harness must see near-1 order on this table before retention, or the drop below proves nothing");
    await Assert.That(afterOnePass).IsLessThan(NEAR_ONE)
      .Because("rows leave from the oldest end and the newest rows refill those pages, so the order rotates");
  }

  /// <summary>The added index: BRIN over the event store's <c>created_at</c>, at the default 128 pages per range.</summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-pages-per-range</docs>
  [Test]
  public async Task TheEventStoreCreatedAtBrinIndex_ExistsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    var definition = await _indexDefinitionAsync(conn, BRIN_INDEX);

    await Assert.That(definition).IsNotNull().Because("the orphaned-lifecycle window has no other index to use");
    await Assert.That(definition).Contains("USING brin (created_at)", StringComparison.Ordinal);
    await Assert.That(definition).Contains("pages_per_range='128'", StringComparison.Ordinal)
      .Because("one range is 1 MB of heap: the window reads a few ranges, and the index stays a few pages per GB");
    await Assert.That(definition).DoesNotContain("autosummarize", StringComparison.Ordinal)
      .Because("the window is the unsummarized tail, which a BRIN scan always reads; vacuum summarizes the rest");
  }

  /// <summary>
  /// Nothing is replaced: the B-trees on the candidate columns stay, because the reap needs its order and the
  /// retention deletes are served at any physical order.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-nothing-replaced</docs>
  [Test]
  [Arguments("idx_event_store_ephemeral_reap")]
  [Arguments("idx_event_store_commit_sequence")]
  [Arguments("idx_wh_log_logged_at")]
  [Arguments("idx_lifecycle_completions_completed_at")]
  [Arguments("idx_message_dedup_first_seen")]
  public async Task TheExistingBTree_IsKeptAsync(string index) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT am.amname FROM pg_class i JOIN pg_am am ON am.oid = i.relam
      WHERE i.relname = @p_index AND i.relkind = 'i'
      """;
    cmd.Parameters.AddWithValue("p_index", index);
    var method = await cmd.ExecuteScalarAsync() as string;

    await Assert.That(method).IsEqualTo("btree");
  }

  /// <summary>
  /// No other BRIN index: <c>commit_sequence</c> readers need its order (a B-tree), and the retention tables do not
  /// correlate once retention runs.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-commit-sequence</docs>
  [Test]
  public async Task NoOtherCandidate_HasABrinIndexAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT i.relname
      FROM pg_index x
      JOIN pg_class i ON i.oid = x.indexrelid
      JOIN pg_class t ON t.oid = x.indrelid
      JOIN pg_am am ON am.oid = i.relam
      WHERE am.amname = 'brin'
        AND t.relname IN ('wh_event_store', 'wh_log', 'wh_lifecycle_completions', 'wh_message_deduplication')
      ORDER BY i.relname
      """;
    var names = new List<string>();
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        names.Add(reader.GetString(0));
      }
    }

    await Assert.That(names).IsEquivalentTo([BRIN_INDEX]);
  }

  /// <summary>
  /// The query that motivated the index, the very SQL the coordinator runs, reads its window through the BRIN index
  /// on a store seeded in its production write order.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-event-store-created-at</docs>
  [Test]
  public async Task TheOrphanedLifecycleQuery_ReadsTheWindowThroughTheBrinIndexAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _seedEventStoreAsync(conn);

    var plan = await _orphanedLifecyclePlanAsync(conn);

    await Assert.That(plan).Contains($"Bitmap Index Scan on {BRIN_INDEX}", StringComparison.Ordinal)
      .Because("the window is thirty minutes of a store that keeps every event");
    await Assert.That(plan).DoesNotContain("Seq Scan on wh_event_store", StringComparison.Ordinal);
  }

  /// <summary>
  /// The control: the same query on the same store without the BRIN index reads the whole event store, so the
  /// harness can see the outcome the index removes.
  /// </summary>
  /// <docs>operations/infrastructure/storage-maintenance#brin-event-store-created-at</docs>
  [Test]
  public async Task TheOrphanedLifecycleQuery_WithoutTheBrinIndex_ScansTheStoreAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    await _seedEventStoreAsync(conn);

    await using var tx = await conn.BeginTransactionAsync();
    await _executeAsync(conn, $"DROP INDEX IF EXISTS {BRIN_INDEX}");
    var plan = await _orphanedLifecyclePlanAsync(conn);
    await tx.RollbackAsync();

    await Assert.That(plan).DoesNotContain(BRIN_INDEX, StringComparison.Ordinal);
    await Assert.That(plan).Contains("Seq Scan on wh_event_store", StringComparison.Ordinal)
      .Because("no other index on wh_event_store leads with created_at for every event");
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
  /// Writes the store's history through the emit chain, one batch an hour, stamping each batch as the stamper does
  /// and vacuuming every few batches as autovacuum does, then analyzes it.
  /// </summary>
  private static async Task _seedEventStoreAsync(NpgsqlConnection conn) {
    await _holdAutovacuumOffAsync(conn, "wh_event_store");
    var instanceId = (Guid)TrackedGuid.New();
    for (var batch = 0; batch < BATCHES; batch++) {
      var at = _start.AddHours(batch);
      await _emitBatchAsync(conn, instanceId, at);
      await _drainStampAsync(conn);
      if (batch % VACUUM_EVERY == VACUUM_EVERY - 1) {
        await _executeAsync(conn, "VACUUM wh_event_store");
      }
    }
    await _executeAsync(conn, "ANALYZE wh_event_store");
    await _executeAsync(conn, "ANALYZE wh_event_body");
    await _executeAsync(conn, "ANALYZE wh_perspective_cursors");
    await _executeAsync(conn, "ANALYZE wh_lifecycle_completions");
  }

  private static async Task _emitBatchAsync(NpgsqlConnection conn, Guid instanceId, DateTimeOffset at) {
    var messageIds = new Guid[STREAMS_PER_BATCH * EVENTS_PER_STREAM];
    var streamIds = new Guid[messageIds.Length];
    for (var s = 0; s < STREAMS_PER_BATCH; s++) {
      var streamId = (Guid)TrackedGuid.New();
      for (var e = 0; e < EVENTS_PER_STREAM; e++) {
        messageIds[(s * EVENTS_PER_STREAM) + e] = (Guid)TrackedGuid.New();
        streamIds[(s * EVENTS_PER_STREAM) + e] = streamId;
      }
    }

    await using var tx = await conn.BeginTransactionAsync();
    await using (var insert = conn.CreateCommand()) {
      insert.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, created_at)
        SELECT m.id, 'test-dest', @type, 'env', '{"p":{}}'::jsonb, '{}'::jsonb,
               1, 0, @p_inst, @p_at + INTERVAL '5 minutes', 0, m.sid, true, @p_at
        FROM unnest(@p_ids, @p_sids) AS m(id, sid)
        """;
      insert.Parameters.AddWithValue("type", EVENT_TYPE);
      insert.Parameters.AddWithValue("p_inst", instanceId);
      insert.Parameters.AddWithValue("p_at", at);
      insert.Parameters.AddWithValue("p_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, messageIds);
      insert.Parameters.AddWithValue("p_sids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, streamIds);
      _ = await insert.ExecuteNonQueryAsync();
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@p_ids, @p_inst, @p_at + INTERVAL '5 minutes', @p_at, 10000)";
      emit.Parameters.AddWithValue("p_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, messageIds);
      emit.Parameters.AddWithValue("p_inst", instanceId);
      emit.Parameters.AddWithValue("p_at", at);
      _ = await emit.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
  }

  /// <summary>
  /// The tests vacuum by hand at the points autovacuum would, so where row versions land is decided by the test, not
  /// by when the server's autovacuum happens to wake. Left on, an autovacuum landing mid-seed moves stamped rows and
  /// shifts the measured correlation and the planner's statistics from run to run.
  /// </summary>
  private static Task _holdAutovacuumOffAsync(NpgsqlConnection conn, string table) =>
    _executeAsync(conn, $"ALTER TABLE {table} SET (autovacuum_enabled = false, toast.autovacuum_enabled = false)");

  /// <summary>Stamps until a call stamps nothing. Bounded: one batch's events at most.</summary>
  private static async Task _drainStampAsync(NpgsqlConnection conn) {
    for (var call = 0; call <= STREAMS_PER_BATCH * EVENTS_PER_STREAM; call++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT stamp_pending_commit_sequences(1000)";
      var stamped = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
      if (stamped == 0) {
        return;
      }
    }
  }

  /// <summary>One day of a retention table's rows, in arrival order.</summary>
  private static async Task _insertRetentionDayAsync(NpgsqlConnection conn, string table, int day) {
    var columns = table switch {
      "wh_message_deduplication" => ("message_id, first_seen_at, observation_count", "gen_random_uuid(), {0}, 1"),
      "wh_lifecycle_completions" => ("event_id, instance_id, completed_at", "gen_random_uuid(), gen_random_uuid(), {0}"),
      _ => ("log_level, source, error_message, logged_at", "1, 'brin-test', 'seeded', {0}")
    };
    var at = $"@day_start + (n * INTERVAL '1 millisecond') * (86400000 / {ROWS_PER_DAY})";
    var values = string.Format(CultureInfo.InvariantCulture, columns.Item2, at);
    await _executeAsync(conn,
      $"INSERT INTO {table} ({columns.Item1}) SELECT {values} FROM generate_series(0, {ROWS_PER_DAY - 1}) AS n ORDER BY n",
      ("day_start", _start.AddDays(day)));
  }

  private static async Task<double> _correlationAsync(NpgsqlConnection conn, string table, string column) {
    await _executeAsync(conn, $"ANALYZE {table}");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT correlation FROM pg_stats WHERE tablename = @p_table AND attname = @p_column";
    cmd.Parameters.AddWithValue("p_table", table);
    cmd.Parameters.AddWithValue("p_column", column);
    var correlation = Convert.ToDouble(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    // The measurement is the evidence the storage-maintenance page records, so the run log carries it.
    Console.WriteLine($"[#1745] {table}.{column} correlation = {correlation.ToString("F4", CultureInfo.InvariantCulture)}");
    return correlation;
  }

  private static async Task<string?> _indexDefinitionAsync(NpgsqlConnection conn, string index) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE tablename = 'wh_event_store' AND indexname = @name";
    cmd.Parameters.AddWithValue("name", index);
    return await cmd.ExecuteScalarAsync() as string;
  }

  /// <summary>
  /// EXPLAIN of the coordinator's own orphaned-lifecycle SQL, its window the last thirty minutes of the seeded store
  /// (the window the startup reconciliation passes).
  /// </summary>
  private static async Task<string> _orphanedLifecyclePlanAsync(NpgsqlConnection conn) {
    var sql = OrphanedLifecycleEventsSql.Build(
        "wh_event_store", "wh_event_body", "wh_perspective_cursors", "wh_lifecycle_completions")
      .Replace("{0}", "$1", StringComparison.Ordinal)
      .Replace("{1}", "$2", StringComparison.Ordinal)
      .Replace("{2}", "$3", StringComparison.Ordinal)
      .Replace("{3}", "$4", StringComparison.Ordinal);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "EXPLAIN " + sql;
    string[] types = [EVENT_TYPE];
    string[] names = [PERSPECTIVE];
    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = types });
    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = names });
    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = _start.AddHours(BATCHES - 1).AddMinutes(-30) });
    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = 100 });
    var lines = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join("\n", lines);
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
