using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That the claim poll's cost is bounded by the batch it returns, not by the backlog it polls over.
/// </summary>
/// <remarks>
/// <para>
/// Every instance calls <c>claim_work</c> several times a second. Measured under a bulk load, one
/// call read tens of thousands of blocks and the queue tables were scanned whole thousands of
/// times an hour, so the poll alone took most of the database's cores and the backlog it was
/// polling over grew because of it.
/// </para>
/// <para>
/// The queue tables are empty between loads. A session that polled while they were empty carries
/// plans made for empty tables, and once the tables fill those plans scan them whole. The test
/// reproduces exactly that: poll on empty tables, fill them with wide rows leased elsewhere, poll
/// again on the same session, and read how many tuples the tables gave up for that one call.
/// </para>
/// <para>
/// A poll has two halves and each had to be bounded separately. Acquisition takes work the instance
/// does not hold; the re-offer hands back the streams it does, and is what a busy instance spends
/// almost all of its polls doing. The tests here cover both: one fills the tables with unowned rows
/// and measures an acquiring poll, one fills them with rows this instance already holds and measures
/// a re-offering poll at two different holding sizes. Payloads are wide enough in every case that a
/// row is not updated in place, so a small-row fill cannot make a poll look bounded by keeping the
/// whole backlog on a handful of pages.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard2")]
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class ClaimWorkPlanShapeTests : EFCoreTestBase {
  private const int ROWS_PER_TABLE = 20_000;
  private const int BATCH = 100;
  /// <summary>Tuples a bounded poll may read from one table: a few batches, never the backlog.</summary>
  private const long READ_BUDGET_PER_TABLE = 4L * BATCH * 4;
  private const string OUTBOX = "wh_outbox";
  private const string INBOX = "wh_inbox";
  private const string PERSPECTIVE_EVENTS = "wh_perspective_events";
  private const string EVENT_STORE = "wh_event_store";
  private static readonly string[] TABLES = [OUTBOX, INBOX, PERSPECTIVE_EVENTS, EVENT_STORE];
  /// <summary>Rows the poller holds per queue table in the re-offer scenario: a full outstanding budget.</summary>
  private const int HELD_ROWS_PER_TABLE = 5_000;
  /// <summary>Streams those rows spread over, so the re-offer has many more streams than one batch.</summary>
  private const int HELD_STREAMS = 500;
  /// <summary>
  /// Blocks (heap and index, hit or read) one poll may touch on one table and its indexes while
  /// re-offering out of a full budget of holdings. A re-offer bounded by its batch pays an index
  /// descent and a page or two per stream it returns and nothing for the streams it does not, so the
  /// ceiling is that constant times the batch, and it has to hold at double the holdings as well.
  ///
  /// Measured on a container at 5,000, 10,000 and 40,000 held rows per table, one steady-state poll
  /// returning 300 rows: 700, 912 and 925 blocks in total across the four tables, the worst single
  /// table being 341, 441 and 449. The same polls before this change cost 5,425, 10,705 and 42,388
  /// blocks, the inbox alone 3,817, 7,571 and 30,095, one per held row's page. Twelve per returned
  /// row leaves a factor of two and a half above the measured worst and a factor of three below what
  /// the old shape cost at the smallest of the three sizes.
  /// </summary>
  private const long BLOCKS_PER_RETURNED_ROW = 12;
  private const long BLOCK_CEILING_PER_TABLE = BLOCKS_PER_RETURNED_ROW * BATCH;
  /// <summary>
  /// Tuples (index entries read plus sequential tuples read) one poll may examine on one table while
  /// re-offering out of a full budget of holdings. Blocks alone would not catch an index-only pass
  /// over the whole holdings, which is cheap in blocks and still grows with the backlog.
  ///
  /// Measured on the same polls: 100 on the inbox and 101 on the outbox and on the perspective
  /// events (one probe per returned stream), 2 on the event store, at every one of the three holding
  /// sizes. Before the change the same polls examined 15,005, 5,001, 5,000 and 10,006 at 5,000 held
  /// rows, and eight times that at 40,000. Four per returned stream is four times the measured worst
  /// and a fiftieth of the smallest number the old shape produced.
  /// </summary>
  private const long TUPLES_PER_RETURNED_STREAM = 4;
  private const long TUPLE_CEILING_PER_TABLE = TUPLES_PER_RETURNED_STREAM * BATCH;

  private static async Task _heartbeatAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var hb = connection.CreateCommand();
    hb.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
    hb.Parameters.AddWithValue("id", instanceId);
    await hb.ExecuteNonQueryAsync();
  }

  private static async Task<int> _claimAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM claim_work(
        p_instance_id => @id,
        p_service_name => 'test',
        p_host_name => 'test-host',
        p_process_id => 1,
        p_max_streams => @batch,
        p_partition_count => 10000,
        p_lease_seconds => 300
      )";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("batch", BATCH);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Wide rows. With a holder, all leased by that live instance, so the poller finds nothing to
  /// take; without one, all unowned, so the poller acquires from them.
  /// </summary>
  private static async Task _fillAsync(NpgsqlConnection connection, Guid? holder) {
    await using var fill = connection.CreateCommand();
    var lease = holder is null ? "NULL::timestamptz" : "NOW() + INTERVAL '5 minutes'";
    fill.CommandText = $@"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'topic', 'TestEvent', jsonb_build_object('pad', repeat('x', 1500)), '{{}}',
             0, 0, NOW(), gen_random_uuid(), 0, @holder, {lease}
      FROM generate_series(1, @n);
      INSERT INTO wh_inbox
        (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
         stream_id, partition_number, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', jsonb_build_object('pad', repeat('x', 1500)), '{{}}',
             0, 0, NOW(), gen_random_uuid(), 0, @holder, {lease}
      FROM generate_series(1, @n);
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'TestPerspective', gen_random_uuid(), 0, 0, NOW(), @holder, {lease}
      FROM generate_series(1, @n);";
    fill.Parameters.AddWithValue(nameof(holder), (object?)holder ?? DBNull.Value);
    fill.Parameters[nameof(holder)].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
    fill.Parameters.AddWithValue("n", ROWS_PER_TABLE);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// A poll that acquires from an unowned backlog reads a few batches of it, not all of it.
  /// </summary>
  /// <remarks>
  /// This is the shape a bulk load produces: the producer's outbox and the consumers' perspective
  /// events fill with unowned rows faster than the fleet takes them. Measured under one, each poll
  /// read tens of thousands of blocks, the queue tables were scanned whole several times per poll,
  /// and adding instances made it worse, because every instance paid the backlog on every poll.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_AcquiringFromAnUnownedBacklog_ReadsAFewBatchesNotTheBacklogAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    var poller = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);
    for (var i = 0; i < 6; i++) {
      await _claimAsync(connection, poller);
    }

    await _fillAsync(connection, holder: null);
    var before = await _sequentialTuplesReadAsync(connection);

    var claimed = await _claimAsync(connection, poller);

    var after = await _sequentialTuplesReadAsync(connection);
    await Assert.That(claimed).IsGreaterThan(0).Because("there is unowned work and the poller is alive");
    await Assert.That(claimed).IsLessThanOrEqualTo(3 * BATCH)
      .Because("a poll returns at most a batch per category");
    foreach (var table in new[] { OUTBOX, INBOX, PERSPECTIVE_EVENTS }) {
      var read = after[table] - before[table];
      await Assert.That(read).IsLessThan(READ_BUDGET_PER_TABLE)
        .Because($"one poll read {read} tuples of {table} sequentially while acquiring from {ROWS_PER_TABLE} unowned rows; "
          + "acquisition bounded by its batch walks an index to the first eligible rows, and one bounded by the "
          + "backlog ranks every pending row on every poll of every instance");
    }
  }

  /// <summary>
  /// A poll that re-offers what the instance already holds is priced by the batch it returns, not by
  /// the holdings: doubling the holdings does not change what the poll costs.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the steady state of every busy instance: it holds as many leased rows as its budget
  /// allows, most of them already handed to a drain, and it polls several times a second to re-offer
  /// the streams it holds. Measured on a saturated database with acquisition already bounded, one
  /// such poll still touched thousands of blocks to return a batch, because the inbox re-offer
  /// ranked every held row with window functions and fetched every held row's heap page to do it,
  /// the outbox re-offer ranked every held row with a window function it never read, the perspective
  /// re-offer aggregated every held event, and the event-store chain re-checked every held inbox
  /// event against the event store on every poll.
  /// </para>
  /// <para>
  /// Both ceilings are needed and neither is enough alone. Blocks catch the heap fetches: the held
  /// rows are found through an index, so a sequential-tuple counter reads zero while every held
  /// row's page is fetched. Tuples catch an index-only scan of the whole holdings, which is cheap in
  /// blocks and still grows with the backlog. The counters in <c>pg_statio_user_tables</c> and
  /// <c>pg_stat_user_indexes</c> are cluster-wide, so the fill is vacuumed before the window is
  /// measured: without that, autovacuum's own reads of the just-written pages land inside it.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_ReofferingWhatItHolds_IsPricedByTheBatchNotTheHoldingsAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    var poller = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);
    for (var i = 0; i < 6; i++) {
      await _claimAsync(connection, poller);
    }

    var (claimed, single) = await _measureSteadyPollAsync(connection, poller);
    await Assert.That(claimed).IsGreaterThan(0).Because("the poller holds work and re-offers it");
    await Assert.That(claimed).IsLessThanOrEqualTo(4 * BATCH)
      .Because("a poll returns at most a batch per category");
    await _assertWithinCeilingsAsync(single, HELD_ROWS_PER_TABLE, null);

    // Double the holdings. A poll priced by the batch it returns costs the same; one priced by the
    // holdings costs twice as much, which is the shape that saturated a shared server.
    var (_, doubled) = await _measureSteadyPollAsync(connection, poller);
    await _assertWithinCeilingsAsync(doubled, 2 * HELD_ROWS_PER_TABLE, single);
  }

  /// <summary>
  /// Adds a full budget of holdings, lets the chain and autovacuum finish with them, then measures
  /// one steady-state poll: what it returned, and the blocks and tuples it cost per table.
  /// </summary>
  private async Task<(int Claimed, Dictionary<string, Cost> Cost)> _measureSteadyPollAsync(
      NpgsqlConnection connection, Guid poller) {
    await _fillHeldAsync(connection, poller);
    // The first poll after a fill is the one that chains the newly stored events into the event
    // store; the steady state this measures begins on the next one.
    await _claimAsync(connection, poller);
    await _settleAsync(connection);
    var before = await _costAsync(connection);
    var claimed = await _claimAsync(connection, poller);
    var after = await _costAsync(connection);
    return (claimed, TABLES.ToDictionary(
      t => t,
      t => new Cost(after[t].Blocks - before[t].Blocks, after[t].Tuples - before[t].Tuples)));
  }

  private static async Task _assertWithinCeilingsAsync(
      Dictionary<string, Cost> cost, int heldRows, Dictionary<string, Cost>? half) {
    var report = string.Join(", ", TABLES.Select(t => $"{t}={cost[t].Blocks}b/{cost[t].Tuples}t"));
    foreach (var table in TABLES) {
      var earlier = half is null ? "" : $" (it touched {half[table].Blocks} with half as many)";
      await Assert.That(cost[table].Blocks).IsLessThan(BLOCK_CEILING_PER_TABLE)
        .Because($"one poll touched {cost[table].Blocks} blocks of {table} and its indexes while re-offering out of "
          + $"{heldRows} held rows{earlier} (all tables: {report}); a re-offer bounded by its batch walks an index to "
          + "the streams it returns, and one bounded by the holdings fetches every held row's page, several times a "
          + "second, on every instance in the fleet");
      await Assert.That(cost[table].Tuples).IsLessThan(TUPLE_CEILING_PER_TABLE)
        .Because($"one poll examined {cost[table].Tuples} tuples of {table} while re-offering out of {heldRows} held "
          + $"rows (all tables: {report}); an index-only pass over the whole holdings is cheap in blocks and still "
          + "grows with the backlog, which is the thing this has to stay bounded against");
    }
  }

  /// <summary>Blocks touched and tuples examined on one table and its indexes.</summary>
  private readonly record struct Cost(long Blocks, long Tuples);

  /// <summary>
  /// Leaves autovacuum nothing to do on the queue tables, so its reads of the pages the fill just
  /// wrote do not land inside the measured window. The counters are cluster-wide, not per backend.
  /// </summary>
  private static async Task _settleAsync(NpgsqlConnection connection) {
    await using var vacuum = connection.CreateCommand();
    vacuum.CommandText = "VACUUM (ANALYZE) wh_outbox, wh_inbox, wh_perspective_events, wh_event_store";
    vacuum.CommandTimeout = 300;
    await vacuum.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Wide rows leased to the poller with live leases, over many streams: the holdings of a busy
  /// instance. Every inbox event already has its event-store row, as it does once the chain has run.
  /// </summary>
  private static async Task _fillHeldAsync(NpgsqlConnection connection, Guid holder) {
    await using var fill = connection.CreateCommand();
    fill.CommandText = @"
      WITH streams AS (
        SELECT gen_random_uuid() AS stream_id, s AS ordinal FROM generate_series(1, @streams) s
      ),
      rows AS (
        SELECT st.stream_id, st.ordinal, r AS seq FROM streams st CROSS JOIN generate_series(1, @per_stream) r
      )
      INSERT INTO wh_outbox
        (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
         stream_id, partition_number, instance_id, lease_expiry)
      SELECT gen_random_uuid(), 'topic', 'TestEvent', jsonb_build_object('pad', repeat('x', 1500)), '{}',
             0, 1, NOW() - (rows.ordinal * INTERVAL '1 millisecond') + (rows.seq * INTERVAL '1 microsecond'),
             rows.stream_id, rows.ordinal % 10, @holder, NOW() + INTERVAL '5 minutes'
      FROM rows;
      WITH streams AS (
        SELECT gen_random_uuid() AS stream_id, s AS ordinal FROM generate_series(1, @streams) s
      ),
      rows AS (
        SELECT st.stream_id, st.ordinal, r AS seq, gen_random_uuid() AS message_id
        FROM streams st CROSS JOIN generate_series(1, @per_stream) r
      ),
      inserted AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           stream_id, partition_number, instance_id, lease_expiry, is_event, priority)
        SELECT rows.message_id, 'TestHandler', 'TestEvent',
               jsonb_build_object('pad', repeat('x', 1500)), '{}',
               0, 1, NOW() - (rows.ordinal * INTERVAL '1 millisecond') + (rows.seq * INTERVAL '1 microsecond'),
               rows.stream_id, rows.ordinal % 10, @holder, NOW() + INTERVAL '5 minutes', TRUE,
               CASE rows.ordinal % 3 WHEN 0 THEN 50 WHEN 1 THEN 150 ELSE 250 END
        FROM rows
        RETURNING message_id, stream_id, received_at
      )
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      SELECT i.message_id, i.stream_id, i.stream_id, 'TestAggregate', 'TestEvent', '{}'::jsonb,
             ROW_NUMBER() OVER (PARTITION BY i.stream_id ORDER BY i.received_at, i.message_id), i.received_at
      FROM inserted i;
      WITH streams AS (
        SELECT gen_random_uuid() AS stream_id, s AS ordinal FROM generate_series(1, @streams) s
      ),
      rows AS (
        SELECT st.stream_id, st.ordinal, r AS seq FROM streams st CROSS JOIN generate_series(1, @per_stream) r
      )
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, instance_id, lease_expiry, priority)
      SELECT rows.stream_id, 'TestPerspective', gen_random_uuid(), 0, 1,
             NOW() - (rows.ordinal * INTERVAL '1 millisecond'), @holder, NOW() + INTERVAL '5 minutes',
             CASE rows.ordinal % 3 WHEN 0 THEN 50 WHEN 1 THEN 150 ELSE 250 END
      FROM rows;
      ANALYZE wh_outbox; ANALYZE wh_inbox; ANALYZE wh_perspective_events; ANALYZE wh_event_store;";
    fill.Parameters.AddWithValue("holder", holder);
    fill.Parameters.AddWithValue("streams", HELD_STREAMS);
    fill.Parameters.AddWithValue("per_stream", HELD_ROWS_PER_TABLE / HELD_STREAMS);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Per table and its indexes: blocks touched (heap and index, hit or read) and tuples examined
  /// (index entries read plus sequential tuples read), after the polling backend flushed its
  /// counters. Read over a connection of its own so the reporting queries themselves are not in it.
  /// </summary>
  private async Task<Dictionary<string, Cost>> _costAsync(NpgsqlConnection reporter) {
    await using (var flush = reporter.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = @"
      SELECT t.relname,
             t.heap_blks_read + t.heap_blks_hit + COALESCE(x.idx_blocks, 0),
             u.seq_tup_read + COALESCE(x.idx_tuples, 0)
      FROM pg_statio_user_tables t
      JOIN pg_stat_user_tables u ON u.relid = t.relid
      LEFT JOIN (
        SELECT i.relid,
               SUM(i.idx_blks_read + i.idx_blks_hit) AS idx_blocks,
               SUM(s.idx_tup_read) AS idx_tuples
        FROM pg_statio_user_indexes i
        JOIN pg_stat_user_indexes s ON s.indexrelid = i.indexrelid
        GROUP BY i.relid
      ) x ON x.relid = t.relid
      WHERE t.relname IN ('wh_outbox', 'wh_inbox', 'wh_perspective_events', 'wh_event_store')";
    var counts = new Dictionary<string, Cost>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      counts[reader.GetString(0)] = new Cost(reader.GetInt64(1), reader.GetInt64(2));
    }
    return counts;
  }

  /// <summary>Tuples read by sequential scans per table, after the reporting backend flushed its counters.</summary>
  private async Task<Dictionary<string, long>> _sequentialTuplesReadAsync(NpgsqlConnection reporter) {
    await using (var flush = reporter.CreateCommand()) {
      flush.CommandText = "SELECT pg_stat_force_next_flush()";
      await flush.ExecuteNonQueryAsync();
    }
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = @"
      SELECT relname, seq_tup_read FROM pg_stat_user_tables
      WHERE relname IN ('wh_outbox', 'wh_inbox', 'wh_perspective_events')";
    var counts = new Dictionary<string, long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      counts[reader.GetString(0)] = reader.GetInt64(1);
    }
    return counts;
  }

  /// <summary>
  /// A session that polled empty tables must not scan them whole once they fill.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_AfterTheTablesFill_ReadsAFewBatchesNotTheBacklogAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    var poller = Guid.NewGuid();
    var holder = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);
    await _heartbeatAsync(connection, holder);

    // Enough polls on empty tables for the session to settle on the plans it will keep.
    for (var i = 0; i < 6; i++) {
      await _claimAsync(connection, poller);
    }

    await _fillAsync(connection, holder: holder);
    var before = await _sequentialTuplesReadAsync(connection);

    var claimed = await _claimAsync(connection, poller);

    var after = await _sequentialTuplesReadAsync(connection);
    await Assert.That(claimed).IsEqualTo(0)
      .Because("every row is leased by a live peer, so the poll has nothing to take and its cost is pure overhead");
    foreach (var table in new[] { OUTBOX, INBOX, PERSPECTIVE_EVENTS }) {
      var read = after[table] - before[table];
      await Assert.That(read).IsLessThan(READ_BUDGET_PER_TABLE)
        .Because($"one poll read {read} tuples of {table} sequentially with {ROWS_PER_TABLE} rows queued; "
          + "a poll bounded by its batch reads a few hundred, and one bounded by the backlog reads them all "
          + "several times a second on every instance");
    }
  }
}
