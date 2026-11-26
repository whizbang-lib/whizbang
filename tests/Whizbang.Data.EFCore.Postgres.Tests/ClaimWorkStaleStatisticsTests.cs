// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

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
/// perspective-event table (<c>reltuples</c> 0, one page) over tens of thousands of pending rows.
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
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("precondition: the fill alone leaves the statistics stale, so the ANALYZE below is what makes them fresh");
    await _executeAsync(connection, "ANALYZE wh_perspective_events");
    await Assert.That(await _isStaleAsync(connection)).IsFalse()
      .Because("precondition: analyzed after the fill, the statistics describe the table");
    var analyzedBefore = await _analyzeCountAsync(connection);

    var refreshed = await _refreshAsync(connection);

    await Assert.That(refreshed).IsEqualTo(0).Because("every queue table's statistics match it");
    await Assert.That(await _analyzeCountAsync(connection)).IsEqualTo(analyzedBefore)
      .Because("nothing may be analyzed when nothing is stale");
  }

  /// <summary>
  /// Claimants that find the same table stale do not all analyze it, and none waits for another: the one that takes the
  /// table's turn analyzes, and every other claimant moves on at once with the statistics it has (#2611).
  /// </summary>
  /// <remarks>
  /// The turn used to be a blocking advisory lock. While a peer held it, every claim queued behind it, and when the peer
  /// itself waited (on an ANALYZE a reindex blocked), every claim of the service waited as long, up to the lock timeout.
  /// A claimant that finds the turn taken leaves the table to its holder and writes no back-off: the holder decides.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhileAPeerHoldsTheTablesTurn_MovesOnWithoutWaitingAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("precondition: the claimant must find the table stale, or it never reaches the turn and this proves nothing");

    await using var peer = new NpgsqlConnection(ConnectionString);
    await peer.OpenAsync(cancellationToken);
    await using var peerTransaction = await peer.BeginTransactionAsync(cancellationToken);
    await _executeAsync(peer, $"SELECT pg_advisory_xact_lock({TURN_LOCK_KEY})");
    var analyzedBefore = await _analyzeCountAsync(connection);

    await using var claimant = new NpgsqlConnection(ConnectionString);
    await claimant.OpenAsync(cancellationToken);
    var claimantPid = await _backendPidAsync(claimant);
    var refreshing = _refreshAsync(claimant);
    var waited = await _everWaitedOnALockAsync(connection, claimantPid, refreshing, cancellationToken);
    var refreshed = await refreshing;
    await peerTransaction.RollbackAsync(cancellationToken);

    await Assert.That(waited).IsFalse()
      .Because("a claimant that finds another holding the table's turn moves on; it never queues behind it");
    await Assert.That(refreshed).IsEqualTo(0).Because("the table is left to the claimant that holds its turn");
    await Assert.That(await _analyzeCountAsync(connection)).IsEqualTo(analyzedBefore);
    await Assert.That(await _backoffAsync(connection)).IsNull()
      .Because("the turn's holder decides whether the table backs off; a claimant that only found it taken writes nothing");
  }

  /// <summary>
  /// The shape measured on a deployed service (#2611): an application transaction left idle for hours, a concurrent
  /// reindex of a queue index waiting on it while it holds the table's ShareUpdateExclusiveLock, and the table's
  /// statistics stale. The refresh never waits on that lock: <c>ANALYZE (SKIP_LOCKED)</c> skips the table, the table
  /// backs off, and the skip is reported once, naming the table and the reason. While the back-off lasts no claim tries
  /// again; once it has passed, the next one does.
  /// </summary>
  /// <remarks>
  /// Before, every claim queued on the refresh's lock, then on the ANALYZE under its five second lock timeout, and gave
  /// up: the service's claims averaged 8.2 s against 20 to 40 ms elsewhere. The back-off's expiry is moved into the past
  /// rather than waited for: the test reads no clock.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhileAReindexWaitsOnAnIdleTransaction_NeverWaits_AndBacksOffAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("precondition: the table must be stale, or the refresh never reaches the ANALYZE and this proves nothing");

    await using var idle = new NpgsqlConnection(ConnectionString);
    await idle.OpenAsync(cancellationToken);
    await using var idleTransaction = await idle.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
    await _executeAsync(idle, "SELECT count(*) FROM wh_settings");
    await using var reindexer = new NpgsqlConnection(ConnectionString);
    await reindexer.OpenAsync(cancellationToken);
    var reindexerPid = await _backendPidAsync(reindexer);
    var index = await _scalarAsync<string>(connection,
      "SELECT indexrelid::regclass::text FROM pg_index WHERE indrelid = 'wh_perspective_events'::regclass ORDER BY 1 LIMIT 1");
    var reindex = _executeAsync(reindexer, $"REINDEX INDEX CONCURRENTLY {index}");
    try {
      await _untilWaitingOnVirtualXidAsync(connection, reindexerPid, reindex, cancellationToken);

      var notices = new List<PostgresNotice>();
      await using var claimant = new NpgsqlConnection(ConnectionString);
      await claimant.OpenAsync(cancellationToken);
      claimant.Notice += (_, e) => { lock (notices) { notices.Add(e.Notice); } };
      var claimantPid = await _backendPidAsync(claimant);
      var first = _refreshAsync(claimant);
      var waited = await _everWaitedOnALockAsync(connection, claimantPid, first, cancellationToken);
      var refreshed = await first;
      var backoff = await _backoffAsync(connection);
      var afterFirst = _skips(notices);
      var skippedByAnalyze = _analyzeSkips(notices);

      await Assert.That(waited).IsFalse()
        .Because("the refresh may not wait on a lock the reindex holds, however long the reindex itself waits");
      await Assert.That(refreshed).IsEqualTo(0).Because("the table could not be analyzed while the reindex held it");
      await Assert.That(skippedByAnalyze).IsEqualTo(1).Because("ANALYZE (SKIP_LOCKED) is what skipped the table");
      await Assert.That(afterFirst).IsEquivalentTo([(QUEUE_TABLE, LOCK_NOT_AVAILABLE)])
        .Because("the skip is reported once, naming the table and the reason");
      await Assert.That(backoff.HasValue).IsTrue();
      await Assert.That(backoff!.Value.Reason).IsEqualTo(LOCK_NOT_AVAILABLE);
      await Assert.That(backoff.Value.InTheFuture).IsTrue().Because("the table backs off for a bounded interval");

      notices.Clear();
      var duringBackoff = await _refreshAsync(claimant);
      await Assert.That(duringBackoff).IsEqualTo(0);
      await Assert.That(_analyzeSkips(notices)).IsEqualTo(0)
        .Because("while the table backs off, no claim tries to analyze it at all");
      await Assert.That(_skips(notices)).IsEmpty().Because("the skip is reported once per back-off, not once per claim");

      await _executeAsync(connection, "UPDATE wh_queue_statistics_backoff SET retry_after = NOW() - INTERVAL '1 second'");
      notices.Clear();
      _ = await _refreshAsync(claimant);
      await Assert.That(_analyzeSkips(notices)).IsEqualTo(1).Because("once the back-off has passed, the next claim tries again");
      await Assert.That(_skips(notices)).IsEquivalentTo([(QUEUE_TABLE, LOCK_NOT_AVAILABLE)])
        .Because("the reindex still holds the table, so it backs off again and says so again");
    } finally {
      await _executeAsync(connection, $"SELECT pg_cancel_backend({reindexerPid})");
      await idleTransaction.RollbackAsync(cancellationToken);
      await Assert.That(async () => await reindex).Throws<PostgresException>();
    }
  }

  /// <summary>
  /// An ordinary session holding a lock that conflicts with ANALYZE skips the table as a reindex does: the refresh gives
  /// up at once rather than at its lock timeout (it never waits), reports the skip, backs off, and leaves the statistics
  /// for a later claim. The holder is an ordinary session, which Postgres never cancels on the refresh's behalf, as it
  /// does an autovacuum.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhileAnotherSessionHoldsTheTable_GivesUpWithinTheLockTimeoutAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("precondition: the table must be stale, or the refresh never reaches the lock and this proves nothing");

    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var holderTransaction = await holder.BeginTransactionAsync(cancellationToken);
    await _executeAsync(holder, "LOCK TABLE wh_perspective_events IN SHARE UPDATE EXCLUSIVE MODE");

    var notices = new List<PostgresNotice>();
    await using var claimant = new NpgsqlConnection(ConnectionString);
    await claimant.OpenAsync(cancellationToken);
    claimant.Notice += (_, e) => { lock (notices) { notices.Add(e.Notice); } };
    var claimantPid = await _backendPidAsync(claimant);
    var refreshing = _refreshAsync(claimant);
    var waited = await _everWaitedOnALockAsync(connection, claimantPid, refreshing, cancellationToken);
    var refreshed = await refreshing;
    await holderTransaction.RollbackAsync(cancellationToken);

    await Assert.That(waited).IsFalse().Because("nothing the refresh would wait on may hold a claim");
    await Assert.That(refreshed).IsEqualTo(0).Because("the table could not be analyzed while another session held it");
    await Assert.That(_skips(notices)).IsEquivalentTo([(QUEUE_TABLE, LOCK_NOT_AVAILABLE)])
      .Because($"skipping is reported, never silent (notices: {string.Join(" | ", notices.Select(n => n.MessageText))})");
    await Assert.That(notices.Single(n => n.MessageText == SKIP_NOTICE).Hint).Contains("wh_perspective_events");
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("nothing was analyzed, so the statistics are left for a claim after the back-off");
    await Assert.That(await _plannerRowEstimateAsync(connection)).IsLessThan(5_000 / 100);
  }

  /// <summary>
  /// An ANALYZE that fails, rather than skips, backs the table off too and says what failed: the claim goes on with the
  /// statistics it has instead of failing with it. Here an expression index raises while the table is analyzed.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhenTheAnalyzeFails_BacksOff_AndSaysWhatFailedAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _executeAsync(connection, """
      CREATE FUNCTION wh_test_analyze_fails(p UUID) RETURNS UUID LANGUAGE plpgsql IMMUTABLE AS $$
      BEGIN
        IF current_setting('whizbang_test.analyze_fails', TRUE) = 'on' THEN
          RAISE EXCEPTION 'the expression index refused to be analyzed';
        END IF;
        RETURN p;
      END $$;
      CREATE INDEX wh_test_analyze_fails_idx ON wh_perspective_events (wh_test_analyze_fails(stream_id));
      """);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("precondition: the table must be stale, or the refresh never runs the ANALYZE that fails");

    var notices = new List<PostgresNotice>();
    await using var claimant = new NpgsqlConnection(ConnectionString);
    await claimant.OpenAsync(cancellationToken);
    claimant.Notice += (_, e) => { lock (notices) { notices.Add(e.Notice); } };
    await _executeAsync(claimant, "SET whizbang_test.analyze_fails = 'on'");
    var refreshed = await _refreshAsync(claimant);

    await Assert.That(refreshed).IsEqualTo(0);
    await Assert.That(_skips(notices)).IsEquivalentTo([(QUEUE_TABLE, "failed")]);
    await Assert.That(notices.Single(n => n.MessageText == SKIP_NOTICE).Hint)
      .Contains("the expression index refused to be analyzed").Because("the failure is named, never swallowed");
    await Assert.That((await _backoffAsync(connection))!.Value.Reason).IsEqualTo("failed");
  }

  /// <summary>
  /// 207's remedy for a stuck autovacuum (#1349) survives: when the only sessions holding a lock that conflicts with
  /// ANALYZE are autovacuum workers, the turn's holder waits for the lock, so PostgreSQL cancels the worker after
  /// deadlock_timeout, and the wait is bounded by deadlock_timeout plus one second. No test can make PostgreSQL run an
  /// autovacuum on demand, so the classification is replaced in this test's database to call the holder one, and the
  /// holder is a client session Postgres never cancels: the wait then ends at its bound, and the table backs off.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_WhenOnlyAutovacuumHoldsTheTable_WaitsForItBoundedByTheDeadlockTimeoutAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await _executeAsync(connection,
      "CREATE OR REPLACE FUNCTION _wh_only_autovacuum_blocks_analyze(p_table REGCLASS) RETURNS BOOLEAN LANGUAGE sql STABLE AS 'SELECT TRUE'");
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var holderTransaction = await holder.BeginTransactionAsync(cancellationToken);
    await _executeAsync(holder, "LOCK TABLE wh_perspective_events IN SHARE UPDATE EXCLUSIVE MODE");

    var notices = new List<PostgresNotice>();
    await using var claimant = new NpgsqlConnection(ConnectionString);
    await claimant.OpenAsync(cancellationToken);
    claimant.Notice += (_, e) => { lock (notices) { notices.Add(e.Notice); } };
    var claimantPid = await _backendPidAsync(claimant);
    var refreshing = _refreshAsync(claimant);
    var waited = await _everWaitedOnALockAsync(connection, claimantPid, refreshing, cancellationToken);
    var refreshed = await refreshing;
    await holderTransaction.RollbackAsync(cancellationToken);

    await Assert.That(waited).IsTrue().Because("an autovacuum is waited for, so that PostgreSQL cancels it");
    await Assert.That(refreshed).IsEqualTo(0).Because("the holder here is not an autovacuum, so nothing cancelled it");
    await Assert.That(_skips(notices)).IsEquivalentTo([(QUEUE_TABLE, "lock_timeout")])
      .Because("the wait is bounded, and a wait that reached its bound backs the table off like a skip");
    await Assert.That(notices.Single(n => n.MessageText == SKIP_NOTICE).Hint).Contains("lock timeout");
  }

  /// <summary>
  /// Who counts as an autovacuum worker for that wait: every session holding a lock that conflicts with ANALYZE must be
  /// one, read from <c>backend_type</c>, or, for a role not allowed to read it, from a session connected to the database
  /// with no user. A client session, or no holder at all, is not. The holder is a real session holding a real lock; a
  /// temporary view stands in for <c>pg_stat_activity</c> in this session only to give it each type, as in
  /// <c>StampFenceAutovacuumSqlTests</c>. The refresh itself pins <c>pg_catalog</c> first, where no view can stand in.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
  [Test]
  [Timeout(600000)]
  public async Task OnlyAutovacuumBlocksAnalyze_IsTrueOnlyWhenEveryConflictingHolderIsAnAutovacuumWorkerAsync(
      CancellationToken cancellationToken) {
    await using var observer = new NpgsqlConnection(ConnectionString);
    await observer.OpenAsync(cancellationToken);
    var nobody = await _scalarAsync<bool>(observer, "SELECT _wh_only_autovacuum_blocks_analyze('wh_perspective_events')");
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    var holderPid = await _backendPidAsync(holder);
    await using var holderTransaction = await holder.BeginTransactionAsync(cancellationToken);
    await _executeAsync(holder, "LOCK TABLE wh_perspective_events IN SHARE UPDATE EXCLUSIVE MODE");

    var autovacuum = await _onlyAutovacuumAsSeenAsync(observer, holderPid, "'autovacuum worker'", "a.usesysid");
    var hidden = await _onlyAutovacuumAsSeenAsync(observer, holderPid, "NULL::text", "NULL::oid");
    var client = await _onlyAutovacuumAsSeenAsync(observer, holderPid, "'client backend'", "a.usesysid");
    var hiddenClient = await _onlyAutovacuumAsSeenAsync(observer, holderPid, "NULL::text", "a.usesysid");
    await holderTransaction.RollbackAsync(cancellationToken);

    await Assert.That(nobody).IsFalse().Because("with no holder there is nothing to wait for");
    await Assert.That(autovacuum).IsTrue();
    await Assert.That(hidden).IsTrue().Because("a hidden session in the database with no user is an autovacuum worker");
    await Assert.That(client).IsFalse();
    await Assert.That(hiddenClient).IsFalse().Because("a hidden session with a user is a client, and is never waited for");
  }

  /// <summary>The classification as this session sees it with the holder given a backend type and a user.</summary>
  private static async Task<bool> _onlyAutovacuumAsSeenAsync(NpgsqlConnection observer, int holderPid, string type, string user) {
    var pid = holderPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    await _executeAsync(observer, $"""
      CREATE OR REPLACE TEMP VIEW pg_stat_activity AS
      SELECT a.datid, a.pid,
             CASE WHEN a.pid = {pid} THEN {user} ELSE a.usesysid END AS usesysid,
             CASE WHEN a.pid = {pid} THEN {type} ELSE a.backend_type END AS backend_type
      FROM pg_catalog.pg_stat_activity a
      """);
    return await _scalarAsync<bool>(observer, "SELECT _wh_only_autovacuum_blocks_analyze('wh_perspective_events')");
  }

  /// <summary>
  /// Through the coordinator, as a service claims: with the refresh unable to analyze, the claim never waits on a lock,
  /// still claims, counts the skip on <c>whizbang.work_coordinator.queue_statistics.refresh_skipped</c> by table and
  /// reason, and warns once per back-off, naming the table.
  /// </summary>
  /// <docs>operations/observability/metrics#queue-statistics-refresh</docs>
  [Test]
  [Timeout(600000)]
  public async Task ClaimWork_WhileTheRefreshCannotAnalyze_NeverWaits_CountsTheSkip_AndWarnsOncePerBackoffAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    var poller = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    await _heartbeatAsync(connection, poller);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await using var holder = new NpgsqlConnection(ConnectionString);
    await holder.OpenAsync(cancellationToken);
    await using var holderTransaction = await holder.BeginTransactionAsync(cancellationToken);
    await _executeAsync(holder, "LOCK TABLE wh_perspective_events IN SHARE UPDATE EXCLUSIVE MODE");
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var logger = new WarningLogger();
    await using var claimContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      claimContext, JsonContextRegistry.CreateCombinedOptions(), logger, metrics);
    var request = new ClaimWorkRequest(poller, "test", "test-host", 1, MaxStreams: BATCH);

    var claiming = coordinator.ClaimWorkAsync(request, cancellationToken);
    var waited = await _anyoneWaitedOnTheTableAsync(connection, claiming, cancellationToken);
    var batch = await claiming;
    _ = await coordinator.ClaimWorkAsync(request, cancellationToken);
    await holderTransaction.RollbackAsync(cancellationToken);
    var skips = _skipCounts(metrics);

    await Assert.That(waited).IsFalse().Because("a claim never waits on the statistics refresh");
    await Assert.That(batch.PerspectiveStreamIds).IsNotEmpty().Because("the claim goes on with the statistics it has");
    await Assert.That(skips).IsEquivalentTo([(1L, (string?)QUEUE_TABLE, (string?)LOCK_NOT_AVAILABLE)])
      .Because("the skip is counted by table and reason, once per back-off");
    await Assert.That(logger.Warnings.Count(w => w.Contains(QUEUE_TABLE, StringComparison.Ordinal)
        && w.Contains("could not be refreshed", StringComparison.Ordinal))).IsEqualTo(1)
      .Because($"the skip is warned once per back-off, naming the table (warnings: {string.Join(" | ", logger.Warnings)})");
  }

  /// <summary>
  /// The refresh works for an application role that has data rights only, as a consumer that keeps
  /// the schema's owner on the <c>-init</c> connection runs it (<see cref="DataOnlyRoleE2ETests"/>).
  /// </summary>
  /// <remarks>
  /// Only a table's owner may analyze it, and the claim runs as the application role. Run with that
  /// role's own rights, the refresh could analyze nothing, and because it only acts on tables it may
  /// analyze, it would say nothing either: the deployment it exists for would stall exactly as before.
  /// The <c>-init</c> connection is not the answer; it is schema-only and never used once a service is
  /// up. The refresh runs with the rights of the role that created it, which is the role that migrated
  /// the schema and owns the tables.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task RefreshStaleQueueStatistics_AsADataOnlyRole_StillAnalyzesTheStaleTableAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext, cancellationToken);
    await _analyzeAtOnePageThenFillAsync(connection, rows: 5_000);
    await Assert.That(await _isStaleAsync(connection)).IsTrue()
      .Because("precondition: the table must be stale, or there is nothing for the role to refresh");
    var role = $"wh_app_{Guid.NewGuid():N}"[..30];
    await _executeAsync(connection, $"""
      CREATE ROLE {role};
      GRANT USAGE ON SCHEMA public TO {role};
      GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role};
      GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO {role};
      """);
    try {
      var analyzedBefore = await _analyzeCountAsync(connection);

      await _executeAsync(connection, $"SET ROLE {role}");
      int refreshed;
      try {
        await Assert.That(await _canAnalyzeAsync(connection)).IsFalse()
          .Because("precondition: the role must really be unable to analyze the table on its own rights");
        refreshed = await _refreshAsync(connection);
      } finally {
        await _executeAsync(connection, "RESET ROLE");
      }

      await Assert.That(refreshed).IsEqualTo(1)
        .Because("the stale table is analyzed with the rights of the schema's owner, whatever role the claim runs as");
      await Assert.That(await _analyzeCountAsync(connection)).IsEqualTo(analyzedBefore + 1)
        .Because("the table was analyzed once");
    } finally {
      await _executeAsync(connection, $"DROP OWNED BY {role}; DROP ROLE {role}");
    }
  }

  /// <summary>The refresh's turn on the perspective-event table: its advisory lock's two keys.</summary>
  private const string TURN_LOCK_KEY =
    "hashtext('wh_refresh_stale_queue_statistics'), 'wh_perspective_events'::regclass::oid::integer";
  private const string QUEUE_TABLE = "wh_perspective_events";
  private const string LOCK_NOT_AVAILABLE = "lock_not_available";
  private const string SKIP_NOTICE = "whizbang.queue_statistics_refresh_skipped";
  private const string SKIP_COUNTER = "whizbang.work_coordinator.queue_statistics.refresh_skipped";

  /// <summary>The warnings a coordinator writes.</summary>
  private sealed class WarningLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<string> _warnings = [];
    public List<string> Warnings { get { lock (_warnings) { return [.. _warnings]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (_warnings) { _warnings.Add(formatter(state, exception)); }
      }
    }
  }

  /// <summary>The skip counter's non-zero series, as (count, table, reason), read at one collection.</summary>
  private static List<(long Value, string? Table, string? Reason)> _skipCounts(WorkCoordinatorMetrics metrics) {
    var cells = new List<(long, string?, string?)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, metrics.QueueStatisticsRefreshSkips.Meter) && instrument.Name == SKIP_COUNTER) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? table = null;
      string? reason = null;
      foreach (var tag in tags) {
        if (tag.Key == WorkCoordinatorMetrics.TABLE_TAG) {
          table = tag.Value as string;
        } else if (tag.Key == WorkCoordinatorMetrics.REASON_TAG) {
          reason = tag.Value as string;
        }
      }
      if (value > 0) {
        cells.Add((value, table, reason));
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return cells;
  }

  /// <summary>The refresh-skipped notices among <paramref name="notices"/>, as (table, reason) from their detail.</summary>
  private static List<(string, string)> _skips(List<PostgresNotice> notices) {
    lock (notices) {
      return [.. notices.Where(n => n.MessageText == SKIP_NOTICE).Select(n => {
        var detail = n.Detail ?? "";
        var split = detail.IndexOf('=', StringComparison.Ordinal);
        return split < 0 ? (detail, "") : (detail[..split], detail[(split + 1)..]);
      })];
    }
  }

  /// <summary>How many times ANALYZE (SKIP_LOCKED) itself said it skipped a table.</summary>
  private static int _analyzeSkips(List<PostgresNotice> notices) {
    lock (notices) {
      return notices.Count(n => n.MessageText.StartsWith("skipping analyze of", StringComparison.Ordinal));
    }
  }

  /// <summary>The perspective-event table's back-off, if it has one: its reason and whether it lasts past now.</summary>
  private static async Task<(string Reason, bool InTheFuture)?> _backoffAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT reason, retry_after > NOW() FROM wh_queue_statistics_backoff WHERE table_name = @t";
    cmd.Parameters.AddWithValue("t", QUEUE_TABLE);
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? (reader.GetString(0), reader.GetBoolean(1)) : null;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection connection, string sql) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>
  /// Whether the given backend was ever seen waiting for a lock while <paramref name="call"/> ran: read from
  /// <c>pg_locks</c> until the call finishes. A call that waits shows here for as long as it waits.
  /// </summary>
  private static async Task<bool> _everWaitedOnALockAsync(
      NpgsqlConnection observer, int pid, Task call, CancellationToken cancellationToken) {
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND NOT granted)";
    cmd.Parameters.AddWithValue(nameof(pid), pid);
    var waited = false;
    while (!call.IsCompleted) {
      waited |= (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
      await Task.Yield();
    }
    return waited;
  }

  /// <summary>
  /// Whether any backend was seen waiting for a lock on the perspective-event table, or for an advisory lock, while
  /// <paramref name="call"/> ran. The holder's own lock is granted, so a waiter here can only be the claim.
  /// </summary>
  private static async Task<bool> _anyoneWaitedOnTheTableAsync(
      NpgsqlConnection observer, Task call, CancellationToken cancellationToken) {
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM pg_locks
        WHERE NOT granted AND (relation = 'wh_perspective_events'::regclass OR locktype = 'advisory'))
      """;
    var waited = false;
    while (!call.IsCompleted) {
      waited |= (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
      await Task.Yield();
    }
    return waited;
  }

  /// <summary>
  /// Returns once the given backend waits on another transaction's virtual transaction id, which is where a concurrent
  /// reindex waits for the transactions older than it: the signal, not a delay. Throws if the call finished first.
  /// </summary>
  private static async Task _untilWaitingOnVirtualXidAsync(
      NpgsqlConnection observer, int pid, Task call, CancellationToken cancellationToken) {
    await using var cmd = observer.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND locktype = 'virtualxid' AND NOT granted)";
    cmd.Parameters.AddWithValue(nameof(pid), pid);
    while (!(bool)(await cmd.ExecuteScalarAsync(cancellationToken))!) {
      if (call.IsCompleted) {
        await call;
        throw new InvalidOperationException("precondition: the reindex finished without waiting on the idle transaction");
      }
      await Task.Yield();
    }
  }

  /// <summary>Whether the current role may analyze the perspective-event table on its own rights.</summary>
  private static async Task<bool> _canAnalyzeAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT pg_has_role(relowner, 'MEMBER') FROM pg_class WHERE oid = 'wh_perspective_events'::regclass";
    return (bool)(await cmd.ExecuteScalarAsync())!;
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
    // The fill's change counts reach the shared statistics when this backend next reports them, about
    // once a second; until then another session reads the table as unchanged. Report them now.
    await _executeAsync(connection, "SELECT pg_stat_force_next_flush()");
    await _executeAsync(connection, "SELECT 1");
  }

  /// <summary>Whether the refresh's own test says the perspective-event table's statistics are stale.</summary>
  private static async Task<bool> _isStaleAsync(NpgsqlConnection connection) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT _wh_queue_statistics_stale('wh_perspective_events'::regclass)";
    return (bool)(await cmd.ExecuteScalarAsync())!;
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
