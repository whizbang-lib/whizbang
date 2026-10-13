// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That the claim poll stays priced by its batch when the planner's statistics for a queue table
/// describe the table as it was before a load, not as it is (#1349).
/// </summary>
/// <remarks>
/// <para>
/// The queue tables are empty between loads, and that is when autovacuum analyzes them. Under a load
/// the next analyze can be a long time coming: autovacuum runs one worker per table, and a worker
/// whose vacuum is waiting for a cleanup lock on a page the claim scans keep pinned never reaches its
/// analyze. A deployed service sat for over an hour with statistics taken from an empty
/// perspective-event table (<c>reltuples</c> 0, one page) over 55,758 pending rows on 1,559 pages.
/// </para>
/// <para>
/// On those statistics every candidate index looks free, and the planner took the narrowest: it
/// captured each selected stream by walking the whole pending set in event-id order and filtering on
/// the stream, once per stream. Each <c>claim_work</c> call ran past the caller's command timeout and
/// was canceled, so nothing was claimed, the backlog stayed the same size, and every later call met
/// it again.
/// </para>
/// <para>
/// <see cref="ClaimWorkPlanShapeTests"/> measures the same poll but analyzes after every fill on
/// purpose, so it only ever sees statistics that match the table. This class reproduces the mismatch:
/// statistics taken while the table held one page and no rows, autovacuum kept off it as the stuck
/// worker kept it off, then an import-shaped backlog.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard4")]
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class ClaimWorkStaleStatisticsTests : EFCoreTestBase {
  /// <summary>Streams the backlog spreads over: thousands, as an import of a few hundred items fans out.</summary>
  private const int STREAMS = 10_000;
  /// <summary>Perspectives each stream feeds.</summary>
  private const int PERSPECTIVES = 6;
  /// <summary>Pending events per stream per perspective.</summary>
  private const int EVENTS_PER_PERSPECTIVE = 3;
  /// <summary>
  /// <para>
  /// Pending rows in the backlog. Large enough that on true statistics the planner walks the indexes
  /// and stops at the batch: perspective events are narrow, and below about this size a full pass is
  /// legitimately the cheaper plan (see <see cref="ClaimWorkPlanShapeTests"/>), which would make the
  /// ceiling below measure the fixture rather than the statistics.
  /// </para>
  /// </summary>
  private const int PENDING_ROWS = STREAMS * PERSPECTIVES * EVENTS_PER_PERSPECTIVE;
  private const int BATCH = 100;
  /// <summary>
  /// <para>
  /// Tuples (index entries read plus sequential tuples read) one poll may examine on the
  /// perspective-event table and its indexes while acquiring a batch out of the backlog.
  /// </para>
  /// <para>
  /// Measured on a container with these constants: 6,512 on statistics analyzed after the fill. On
  /// statistics from before it the same poll had read for more than eight minutes when it was
  /// canceled: it walks the pending set once per stream it captures, about eighteen million entries
  /// for a batch of a hundred streams. The ceiling is three times the measured good poll.
  /// </para>
  /// </summary>
  private const long TUPLE_CEILING = 20_000;
  /// <summary>
  /// A bound on the one claim, so the stuck shape fails here instead of running for many minutes. The
  /// poll it guards takes milliseconds; the test asserts on what the poll read, not on how long it took.
  /// </summary>
  private const int CLAIM_COMMAND_TIMEOUT_SECONDS = 60;

  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_StatisticsFromBeforeTheLoad_AcquiringIsStillPricedByTheBatchAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    var poller = Guid.NewGuid();
    await _heartbeatAsync(connection, poller);

    await _analyzeAtOnePageThenFillAsync(connection);
    await Assert.That(await _plannerRowEstimateAsync(connection)).IsLessThan(PENDING_ROWS / 100)
      .Because("precondition: the planner must still believe the table is about empty, or this measures fresh statistics");

    var before = await _tuplesExaminedAsync(connection);
    var claimed = await _claimAsync(connection, poller);
    var examined = await _tuplesExaminedAsync(connection) - before;

    await Assert.That(claimed).IsGreaterThan(0).Because("there is unowned work and the poller is alive");
    await Assert.That(examined).IsLessThan(TUPLE_CEILING)
      .Because($"one poll examined {examined} tuples of wh_perspective_events to claim {claimed} rows out of "
        + $"{PENDING_ROWS} pending over {STREAMS} streams, on statistics taken before the backlog arrived; a poll "
        + "priced by its batch reads a few batches whatever the statistics said, and one priced by the backlog "
        + "reads the pending set once per stream it captures, which under a load is longer than the command timeout");
    await Assert.That(await _plannerRowEstimateAsync(connection)).IsGreaterThan(PENDING_ROWS / 2)
      .Because("the claim leaves the statistics describing the table as it is, so the next poll on every instance plans for it too");
  }

  /// <summary>
  /// On true statistics the refresh is a catalog read: it analyzes nothing, so a healthy database,
  /// where autovacuum keeps up, never pays for it.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_FreshStatistics_AnalyzesNothingAsync(CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await _executeAsync(connection, "ANALYZE wh_perspective_events");
    var analyzedBefore = await _analyzeCountAsync(connection);

    var refreshed = await _refreshAsync(connection);

    await Assert.That(refreshed).IsEqualTo(0).Because("every queue table's statistics match it");
    await Assert.That(await _analyzeCountAsync(connection)).IsEqualTo(analyzedBefore)
      .Because("nothing may be analyzed when nothing is stale");
  }

  /// <summary>
  /// Claimants that find the same table stale do not all analyze it: one does, the others wait for
  /// its turn, read the statistics again once it has committed, and go on without analyzing.
  /// </summary>
  /// <remarks>
  /// The peer here holds the table's turn and analyzes while the waiter is queued behind it, which is
  /// the interleaving the turn exists for. The statistics a claimant reads are cached for its
  /// transaction, so the waiter would analyze a second time if it did not read them again after the
  /// wait.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhileAPeerRefreshesTheTable_WaitsAndDoesNotAnalyzeAgainAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);

    await using var peer = new NpgsqlConnection(ConnectionString);
    await peer.OpenAsync(cancellationToken);
    await using var peerTransaction = await peer.BeginTransactionAsync(cancellationToken);
    await _executeAsync(peer,
      "SELECT pg_advisory_xact_lock(hashtext('wh_refresh_stale_queue_statistics'), 'wh_perspective_events'::regclass::oid::integer)");

    await using var waiter = new NpgsqlConnection(ConnectionString);
    await waiter.OpenAsync(cancellationToken);
    await using var waiterTransaction = await waiter.BeginTransactionAsync(cancellationToken);
    var waiterPid = await _backendPidAsync(waiter);
    var waiting = _refreshAsync(waiter);
    await _untilWaitingOnAdvisoryLockAsync(connection, waiterPid, waiting, cancellationToken);
    await _executeAsync(peer, "ANALYZE wh_perspective_events");
    var analyzedByPeer = await _analyzeCountAsync(connection);
    await peerTransaction.CommitAsync(cancellationToken);
    var refreshedByWaiter = await waiting;

    await Assert.That(refreshedByWaiter).IsEqualTo(0)
      .Because("the peer analyzed the table while this claimant waited for its turn, so nothing was left stale");
    await Assert.That(await _analyzeCountAsync(connection)).IsEqualTo(analyzedByPeer)
      .Because("the table is analyzed once, by the claimant that held the turn");
  }

  /// <summary>
  /// A claimant that cannot take the table's lock within the timeout gives up, says so, and claims on
  /// the statistics it has. The holder here is an ordinary session, which Postgres never cancels on
  /// the refresh's behalf, as it does an autovacuum; nothing the refresh waits on may stop a claim.
  /// </summary>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhileAnotherSessionHoldsTheTable_GivesUpWithinTheLockTimeoutAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);

    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var holderTransaction = await holder.BeginTransactionAsync(cancellationToken);
    await _executeAsync(holder, "LOCK TABLE wh_perspective_events IN SHARE UPDATE EXCLUSIVE MODE");

    var notices = new List<string>();
    connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
    var refreshed = await _refreshAsync(connection);

    await Assert.That(refreshed).IsEqualTo(0).Because("the table could not be analyzed while another session held it");
    await Assert.That(notices.Any(n => n.Contains("could not refresh the planner statistics", StringComparison.Ordinal))).IsTrue()
      .Because($"giving up is reported, with what failed, never swallowed (notices: {string.Join(" | ", notices)})");
    await Assert.That(await _plannerRowEstimateAsync(connection)).IsLessThan(5_000 / 100)
      .Because("the statistics are left as they were, for the next claim to refresh");
  }

  private static async Task<NpgsqlConnection> _openAsync(DbContext dbContext, CancellationToken cancellationToken) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
    return connection;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 300;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _heartbeatAsync(NpgsqlConnection connection, Guid instanceId) {
    await using var hb = connection.CreateCommand();
    hb.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
    hb.Parameters.AddWithValue("id", instanceId);
    await hb.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// <para>
  /// Statistics as a deployed table had them when it stalled, then an import-shaped backlog of unowned
  /// pending events: many streams, several perspectives each, autovacuum kept off the table as a stuck
  /// worker keeps it off.
  /// </para>
  /// <para>
  /// "As a deployed table had them" is one page and no rows: the table had held a row since it was
  /// created, and was analyzed after that row went. That is not the same as analyzing a table that
  /// never had a page. With no pages the planner sizes the table from its current pages and stays
  /// roughly right; with one page and no rows it computes a density of zero and believes every scan
  /// returns nothing, which is the state that stalled.
  /// </para>
  /// </summary>
  private static async Task _analyzeAtOnePageThenFillAsync(NpgsqlConnection connection, int rows = PENDING_ROWS) {
    await using var fill = connection.CreateCommand();
    fill.CommandText = @"
      ALTER TABLE wh_perspective_events SET (autovacuum_enabled = false);
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number)
      VALUES (gen_random_uuid(), 'TestPerspective', gen_random_uuid(), 0, 0, NOW(), 0);
      DELETE FROM wh_perspective_events;
      ANALYZE wh_perspective_events;
      WITH streams AS (
        SELECT gen_random_uuid() AS stream_id FROM generate_series(1, @streams)
      )
      INSERT INTO wh_perspective_events
        (stream_id, perspective_name, event_id, status, attempts, created_at, partition_number)
      SELECT st.stream_id, 'TestPerspective' || p, gen_random_uuid(), 0, 0,
             NOW() + (e * INTERVAL '1 microsecond'), compute_partition(st.stream_id)
      FROM streams st
      CROSS JOIN generate_series(1, @perspectives) p
      CROSS JOIN generate_series(1, @events) e;";
    fill.Parameters.AddWithValue("streams", rows / (PERSPECTIVES * EVENTS_PER_PERSPECTIVE));
    fill.Parameters.AddWithValue("perspectives", PERSPECTIVES);
    fill.Parameters.AddWithValue("events", EVENTS_PER_PERSPECTIVE);
    fill.CommandTimeout = 300;
    await fill.ExecuteNonQueryAsync();
  }

  /// <summary>What the planner estimates the pending set holds, from the statistics it has.</summary>
  private static async Task<long> _plannerRowEstimateAsync(NpgsqlConnection connection) {
    await using var explain = connection.CreateCommand();
    explain.CommandText = "EXPLAIN (FORMAT JSON) SELECT 1 FROM wh_perspective_events WHERE processed_at IS NULL";
    var json = (string)(await explain.ExecuteScalarAsync())!;
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement[0].GetProperty("Plan").GetProperty("Plan Rows").GetInt64();
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
        p_lease_seconds => 300,
        p_max_rows => @batch
      )";
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("batch", BATCH);
    cmd.CommandTimeout = CLAIM_COMMAND_TIMEOUT_SECONDS;
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<int> _refreshAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_refresh_stale_queue_statistics()";
    cmd.CommandTimeout = 60;
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<int> _backendPidAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT pg_backend_pid()";
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Returns once the given backend is waiting on an advisory lock: the signal, not a delay. Also
  /// returns if the call that should be waiting has already finished, so a call that never waits fails
  /// the assertions after it instead of hanging the test.
  /// </summary>
  private static async Task _untilWaitingOnAdvisoryLockAsync(
      NpgsqlConnection observer, int pid, Task call, CancellationToken cancellationToken) {
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND locktype = 'advisory' AND NOT granted)";
    cmd.Parameters.AddWithValue("pid", pid);
    while (!call.IsCompleted && !(bool)(await cmd.ExecuteScalarAsync(cancellationToken))!) {
      await Task.Yield();
    }
  }

  /// <summary>How many times the perspective-event table has been analyzed by hand, cluster counters flushed.</summary>
  private async Task<long> _analyzeCountAsync(NpgsqlConnection reporter) {
    await _executeAsync(reporter, "SELECT pg_stat_force_next_flush()");
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = "SELECT analyze_count FROM pg_stat_user_tables WHERE relname = 'wh_perspective_events'";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Index entries read plus sequential tuples read on the perspective-event table and its indexes,
  /// after the polling backend flushed its counters. Read over a connection of its own so the
  /// reporting query is not in the count.
  /// </summary>
  private async Task<long> _tuplesExaminedAsync(NpgsqlConnection reporter) {
    await _executeAsync(reporter, "SELECT pg_stat_force_next_flush()");
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync();
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = @"
      SELECT u.seq_tup_read + COALESCE((SELECT SUM(s.idx_tup_read) FROM pg_stat_user_indexes s WHERE s.relid = u.relid), 0)
      FROM pg_stat_user_tables u WHERE u.relname = 'wh_perspective_events'";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }
}
