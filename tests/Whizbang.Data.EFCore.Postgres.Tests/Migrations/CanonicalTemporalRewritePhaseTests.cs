// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// That one instance rewrites a stored format, and that the instance responsible for it waits for
/// its turn rather than assuming whoever holds the schema lock will do the work.
/// </summary>
/// <remarks>
/// <para>
/// The rewrites run before the initializer's transaction opens, which is also outside the
/// transaction-scoped advisory lock that makes one instance do the schema work. The phase takes the
/// same key in a transaction of its own, so a connection pooler cannot separate the lock from the
/// statements it guards, and nothing stays held if the instance dies mid-pass.
/// </para>
/// <para>
/// The key is shared with the bootstrap and DDL phases of every sibling instance. A sibling that
/// holds it is not necessarily rewriting: it may be bootstrapping, and a sibling that then waits
/// for the migrator never rewrites at all. So losing the lock once means "wait", never "skip".
/// A fleet that started together once left every table unconverted that way, without a line above
/// debug level to say so.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class CanonicalTemporalRewritePhaseTests : IAsyncDisposable {
  private const long LOCK_ID = 987654321;
  private const int TIMEOUT_SECONDS = 30;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    var database = await PerTestDatabaseFactory.CreateAsync("rewritephase");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await _executeAsync("CREATE TABLE marker (note text NOT NULL)");
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }

    GC.SuppressFinalize(this);
  }

  private async Task _executeAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  private NpgsqlConnection _connect() => new(_connectionString);

  /// <summary>A rewrite that leaves a trace, so applying it is observable.</summary>
  private static (string Name, string Sql)[] _rewrites(params string[] notes) =>
    [.. notes.Select(n => (n, $"INSERT INTO marker (note) VALUES ('{n}');"))];

  /// <summary>Whether the schema lock is held by anyone in this test's database.</summary>
  /// <remarks>
  /// Scoped to the current database: pg_locks is cluster-wide, and a sibling fixture applying its own
  /// schema in another database on the shared container takes the same lock id.
  /// </remarks>
  private async Task<string> _lockHoldersAsync() =>
    await _scalarAsync(
      $"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objid = {LOCK_ID}"
      + " AND database = (SELECT oid FROM pg_database WHERE datname = current_database())");

  /// <summary>Takes the schema lock at session scope on a connection the test keeps open.</summary>
  private async Task<NpgsqlConnection> _holdLockAsync() {
    var holder = _connect();
    await holder.OpenAsync();
    await using var take = new NpgsqlCommand($"SELECT pg_advisory_lock({LOCK_ID})", holder);
    await take.ExecuteScalarAsync();
    return holder;
  }

  private static async Task _releaseLockAsync(NpgsqlConnection holder) {
    await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LOCK_ID})", holder);
    await release.ExecuteScalarAsync();
  }

  /// <summary>
  /// The longest a test waits for a line the phase logs almost at once. The wait is a signal, so
  /// the bound exists only to fail rather than hang when the line never comes.
  /// </summary>
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(30);

  /// <summary>The holder applies every rewrite, gives the lock back, and says what it did.</summary>
  [Test]
  public async Task TheRewritesRunUnderTheSchemaLockAsync() {
    var log = new SignalingListLogger();

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("first", "second"), TIMEOUT_SECONDS, log);

    await Assert.That(ran).IsTrue();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("2");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo("0")
      .Because("a lock still held after the phase would stall every other instance's rewrite");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("2 of 2")))
      .IsTrue()
      .Because("an operator reads what a startup converted from the log, not from a debug trace");
  }

  /// <summary>
  /// An instance that cannot take the lock waits for it and applies once it is free.
  /// </summary>
  /// <remarks>
  /// The holder may be a sibling's bootstrap or DDL transaction, which converts nothing, and a
  /// sibling staged as a waiter never rewrites. Skipping would leave the table in the old form with
  /// no one left to change it.
  /// </remarks>
  [Test]
  public async Task AnInstanceWaitsForTheLockAndAppliesOnceItIsReleasedAsync() {
    await using var holder = await _holdLockAsync();
    var log = new SignalingListLogger();

    var run = CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("after-the-wait"), TIMEOUT_SECONDS, log);

    // The lock goes back only once the phase has said it is waiting, so the test proves a wait
    // happened rather than a lucky first attempt. The logger signals that line; nothing polls for
    // it and nothing drives a clock, for the reason SignalingListLogger gives.
    await log.WaitForAsync("waiting").WaitAsync(_signalTimeout);
    await _releaseLockAsync(holder);

    await Assert.That(await run).IsTrue()
      .Because("the phase retries until it holds the lock, and it can hold it as soon as the "
        + "holder gives it back, which is well inside the budget");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("waiting")))
      .IsTrue()
      .Because("the phase must report the wait at a level an operator sees");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("1");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo("0");
  }

  /// <summary>
  /// A lock that stays held past the budget is reported as a warning and the phase gives up.
  /// </summary>
  /// <remarks>
  /// The table stays in the old form and the next start tries again. Silence here is what turned
  /// one lost race into a fleet that never converted.
  /// </remarks>
  [Test]
  public async Task AnInstanceGivesUpWhenTheLockStaysHeldAsync() {
    await using var holder = await _holdLockAsync();
    var log = new SignalingListLogger();

    // The budget for the lock is the command timeout, two seconds here, and this holder never lets
    // go, so the budget is the only way out and the outcome cannot depend on scheduling.
    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("should-not-run"), commandTimeoutSeconds: 2, log);

    await Assert.That(ran).IsFalse();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("0");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning
        && e.Message.Contains(LOCK_ID.ToString(CultureInfo.InvariantCulture))))
      .IsTrue()
      .Because("giving up is a warning naming the lock, not a debug line");

    await _releaseLockAsync(holder);
  }

  /// <summary>Cancellation during the wait ends it with the cancellation, nothing applied.</summary>
  [Test]
  public async Task CancellationDuringTheWaitThrowsAsync() {
    await using var holder = await _holdLockAsync();
    var log = new SignalingListLogger();
    using var cts = new CancellationTokenSource();

    var run = CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("should-not-run"), TIMEOUT_SECONDS, log, cts.Token);

    // Canceled once the phase is in its wait, which is where a shutdown finds it. The cancellation
    // ends the wait itself, so the budget never comes into it.
    await log.WaitForAsync("waiting").WaitAsync(_signalTimeout);
    await cts.CancelAsync();

    await Assert.That(async () => await run).Throws<OperationCanceledException>();

    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("0");
    await _releaseLockAsync(holder);
  }

  /// <summary>
  /// A connection that fails while the lock is being taken propagates the failure and holds nothing.
  /// </summary>
  /// <remarks>
  /// The transaction opened for the try-lock is ended on the way out, so what the caller sees is
  /// the connection's failure, and no lock stays with a session about to be discarded. The backend
  /// behind the phase's connection is terminated the moment the connection opens, from another
  /// session, so the first statement the phase sends, the try-lock, is the one that fails.
  /// </remarks>
  [Test]
  public async Task AFailureWhileTakingTheLockPropagatesAndHoldsNothingAsync() {
    var log = new SignalingListLogger();

    NpgsqlConnection doomed() {
      var connection = _connect();
      connection.StateChange += (_, e) => {
        if (e.CurrentState == System.Data.ConnectionState.Open) {
          using var killer = _connect();
          killer.Open();
          using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", killer);
          kill.Parameters.AddWithValue("pid", connection.ProcessID);
          kill.ExecuteScalar();
        }
      };
      return connection;
    }

    await Assert.That(async () => await CanonicalTemporalRewritePhase.ApplyAsync(
        doomed, LOCK_ID, _rewrites("should-not-run"), TIMEOUT_SECONDS, log))
      .Throws<NpgsqlException>()
      .Because("a failure taking the lock is the caller's to report; the phase has nothing to apply it to");

    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("0");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo("0");
  }

  /// <summary>
  /// A rewrite that fails is reported, the rest still run, and the lock comes back.
  /// </summary>
  /// <remarks>
  /// A lock leaked on the failure path is worse than the failure: every later instance would wait
  /// out its budget and give up, start after start.
  /// </remarks>
  [Test]
  public async Task AFailedRewriteStillReleasesTheLockAsync() {
    var log = new SignalingListLogger();

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("broken", "INSERT INTO table_that_does_not_exist (x) VALUES (1);"),
       ("good", "INSERT INTO marker (note) VALUES ('after-the-failure');")],
      TIMEOUT_SECONDS,
      log);

    await Assert.That(ran).IsTrue();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM marker")).IsEqualTo("1")
      .Because("one rewrite failing must not stop the others, which convert different tables");
    await Assert.That(await _lockHoldersAsync()).IsEqualTo("0");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("broken")))
      .IsTrue();
  }

  /// <summary>A failure after a success keeps the success: each rewrite stands on its own.</summary>
  [Test]
  public async Task AFailureAfterASuccessKeepsTheSuccessAsync() {
    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect,
      LOCK_ID,
      [("good", "INSERT INTO marker (note) VALUES ('before-the-failure');"),
       ("broken", "INSERT INTO table_that_does_not_exist (x) VALUES (1);")],
      TIMEOUT_SECONDS);

    await Assert.That(ran).IsTrue();
    await Assert.That(await _scalarAsync("SELECT note FROM marker")).IsEqualTo("before-the-failure")
      .Because("a later table's failure must not roll back an earlier table's conversion");
  }

  /// <summary>What a rewrite says about itself reaches the log.</summary>
  /// <remarks>
  /// The statement knows how many rows it touched and whether it skipped a settled table; the
  /// phase only knows that it ran. The statement raises a notice and the phase relays it.
  /// </remarks>
  [Test]
  public async Task ANoticeRaisedByARewriteIsReportedAsync() {
    var log = new SignalingListLogger();

    await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID,
      [("talkative", "DO $$ BEGIN RAISE NOTICE 'wh_per_thing: 3 row(s) converted'; END $$;")],
      TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("3 row(s) converted")))
      .IsTrue();
  }

  private const string OLD_RENDERING = "2026-04-21T22:38:17.357886+00:00";

  /// <summary>
  /// A table holding a date in the rendering an earlier release wrote, and the rewrite that converts
  /// it, so the index the initializer builds next has something to meet.
  /// </summary>
  /// <remarks>
  /// fillfactor leaves no free space in a page, so the rewrite cannot update a row in place and the
  /// superseded version is a row version of its own, as it is on a real table whose rows carry whole
  /// documents.
  /// </remarks>
  private async Task<(string Name, string Sql)[]> _seedOldRenderingAsync() {
    await _executeAsync("""
      CREATE TABLE wh_per_fence (id uuid PRIMARY KEY, data jsonb NOT NULL) WITH (fillfactor = 100);
      """);
    await _executeAsync($"""
      INSERT INTO wh_per_fence (id, data)
      SELECT gen_random_uuid(),
             jsonb_build_object('OccurredAt', '{OLD_RENDERING}', 'Padding', repeat('x', 1000))
      FROM generate_series(1, 200);
      """);

    return [("fence", """
      UPDATE wh_per_fence
      SET data = data || jsonb_build_object('OccurredAt',
            (EXTRACT(EPOCH FROM (data ->> 'OccurredAt')::timestamptz) * 1000000)::bigint)
      WHERE jsonb_typeof(data -> 'OccurredAt') = 'string';
      """)];
  }

  /// <summary>Builds the index the initializer builds over the rewritten key.</summary>
  private async Task _indexRewrittenKeyAsync() {
    var index = new JsonIndexInfo("OccurredAt", "OccurredAt", JsonIndexCast.Int8, true, false, false);
    foreach (var statement in JsonIndexSql.CreateStatements(index, "wh_per_fence", "fence")) {
      await _executeAsync(statement);
    }
  }

  /// <summary>
  /// Opens a repeatable-read transaction and takes its snapshot, so the snapshot predates the
  /// rewrite and stays in force until the test ends it.
  /// </summary>
  private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> _holdOlderSnapshotAsync() {
    var holder = _connect();
    await holder.OpenAsync();
    var transaction = await holder.BeginTransactionAsync(IsolationLevel.RepeatableRead);
    await using var take = new NpgsqlCommand("SELECT 1", holder, transaction);
    await take.ExecuteScalarAsync();
    return (holder, transaction);
  }

  /// <summary>
  /// The phase returns only once no snapshot older than its commit is left, so the index the
  /// initializer builds next never meets a row version the rewrite superseded.
  /// </summary>
  /// <remarks>
  /// The initializer builds that index right after this phase returns, with a plain
  /// <c>CREATE INDEX</c>, which indexes every row version an open snapshot can still see. While a
  /// snapshot taken before the rewrite lives, that includes the version carrying the old rendering,
  /// and the cast in the index expression fails on it. At startup other instances and other work
  /// are running against the same server, so this is the ordinary case, not a corner of one.
  /// </remarks>
  [Test]
  public async Task ThePhaseReturnsOnlyOnceNoOlderSnapshotCanSeeWhatItRewroteAsync() {
    var rewrites = await _seedOldRenderingAsync();
    var (holder, snapshot) = await _holdOlderSnapshotAsync();
    await using var _ = holder;
    await using var __ = snapshot;
    var log = new SignalingListLogger();

    var run = CanonicalTemporalRewritePhase.ApplyAsync(_connect, LOCK_ID, rewrites, TIMEOUT_SECONDS, log);
    await Task.WhenAny(run, log.WaitForAsync("older than")).WaitAsync(_signalTimeout);

    await Assert.That(run.IsCompleted).IsFalse()
      .Because("returning now hands the initializer a table whose superseded rows its index would meet");
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information
        && e.Message.Contains(holder.ProcessID.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
      .IsTrue()
      .Because("an operator watching a stalled startup needs to know which session it is waiting for");

    await snapshot.CommitAsync();

    await Assert.That(await run).IsTrue();
    await _indexRewrittenKeyAsync();
    await Assert.That(await _scalarAsync(
      "SELECT count(*) FROM pg_indexes WHERE tablename = 'wh_per_fence' AND indexname LIKE '%occurredat%'"))
      .IsEqualTo("1");
  }

  /// <summary>
  /// An older snapshot that outlasts the budget is a warning naming it, and the rewrite stays.
  /// </summary>
  /// <remarks>
  /// Reported rather than fatal, like the rest of this phase: the rewrite is committed, and the
  /// initializer's retry builds the index once the snapshot is gone. The budget is the command
  /// timeout, two seconds here, and the snapshot never ends, so the outcome cannot depend on
  /// scheduling.
  /// </remarks>
  [Test]
  public async Task AnOlderSnapshotThatOutlastsTheBudgetIsAWarningAsync() {
    var rewrites = await _seedOldRenderingAsync();
    var (holder, snapshot) = await _holdOlderSnapshotAsync();
    await using var _ = holder;
    await using var __ = snapshot;
    var log = new SignalingListLogger();

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, rewrites, commandTimeoutSeconds: 2, log);

    await Assert.That(ran).IsTrue();
    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning
        && e.Message.Contains(holder.ProcessID.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
      .IsTrue();
    await Assert.That(await _scalarAsync(
      "SELECT string_agg(DISTINCT jsonb_typeof(data -> 'OccurredAt'), ',') FROM wh_per_fence"))
      .IsEqualTo("number");
  }

  /// <summary>
  /// A pass that converted nothing waits for no one, whatever is open.
  /// </summary>
  /// <remarks>
  /// Every start after the first finds its tables settled, and only a write leaves superseded row
  /// versions behind. Waiting then would stall every startup behind unrelated long transactions.
  /// </remarks>
  [Test]
  public async Task APassThatConvertedNothingWaitsForNoOneAsync() {
    var (holder, snapshot) = await _holdOlderSnapshotAsync();
    await using var _ = holder;
    await using var __ = snapshot;
    var log = new SignalingListLogger();

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, [("settled", "SELECT 1;")], TIMEOUT_SECONDS, log);

    await Assert.That(ran).IsTrue();
    await Assert.That(log.Entries.Any(e => e.Message.Contains("older than", StringComparison.Ordinal)))
      .IsFalse();
  }

  /// <summary>Nothing to rewrite takes no lock and opens no connection.</summary>
  [Test]
  public async Task NoRewritesTakesNoLockAsync() {
    var opened = 0;

    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      () => { opened++; return _connect(); }, LOCK_ID, [], TIMEOUT_SECONDS);

    await Assert.That(ran).IsTrue();
    await Assert.That(opened).IsEqualTo(0)
      .Because("a database with nothing to convert should pay nothing for this phase");
  }

  /// <summary>A missing factory, rewrite list or clock is a caller error.</summary>
  [Test]
  public async Task MissingArgumentsAreRefusedAsync() {
    await Assert.That(async () => await CanonicalTemporalRewritePhase.ApplyAsync(
      null!, LOCK_ID, _rewrites("x"), TIMEOUT_SECONDS)).Throws<ArgumentNullException>();

    await Assert.That(async () => await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, null!, TIMEOUT_SECONDS)).Throws<ArgumentNullException>();

    await Assert.That(async () => await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("x"), TIMEOUT_SECONDS, timeProvider: null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// A table of 5,000 documents with an index over an extraction, never analyzed, and a statement that
  /// rewrites every row of it and marks the table as rewritten.
  /// </summary>
  private async Task<(string Name, string Sql)> _rewrittenTableAsync(string table, bool failAfterMarking = false) {
    await _executeAsync($"""
      CREATE TABLE {table} (id int PRIMARY KEY, data jsonb NOT NULL) WITH (autovacuum_enabled = false);
      INSERT INTO {table} SELECT g, jsonb_build_object('At', '2026-01-0' || (g % 9 + 1)) FROM generate_series(1, 5000) AS g;
      CREATE INDEX {table}_at ON {table} ((data ->> 'At'));
      """);
    var fail = failAfterMarking ? "RAISE EXCEPTION 'rewrite failed after marking';" : "";
    return (table, $"""
      DO $rw$ BEGIN
        UPDATE {table} SET data = jsonb_build_object('At', (data ->> 'At') || 'T00:00:00Z');
        {IndexStatistics.MarkRewrittenSql("'public'", $"'{table}'")}
        {IndexStatistics.MarkRewrittenSql("'public'", $"'{table}'")}
        {fail}
      END $rw$;
      """);
  }

  private async Task<long> _indexStatisticsAsync(string index) =>
    long.Parse(await _scalarAsync(
      $"SELECT count(*) FROM pg_stats WHERE schemaname = 'public' AND tablename = '{index}'"), CultureInfo.InvariantCulture);

  /// <summary>
  /// Issue #1004: a mass update skews a table's statistics, so the phase analyzes each table a rewrite
  /// changed, once, after it commits.
  /// </summary>
  [Test]
  public async Task ARewrittenTableIsAnalyzedAsync() {
    var log = new SignalingListLogger();
    var rewrite = await _rewrittenTableAsync("wh_per_rewritten");

    await CanonicalTemporalRewritePhase.ApplyAsync(_connect, LOCK_ID, [rewrite], TIMEOUT_SECONDS, log);

    await Assert.That(await _indexStatisticsAsync("wh_per_rewritten_at")).IsGreaterThan(0)
      .Because("analyzing the rewritten table gathers statistics for its index's expression too");
    await Assert.That(log.Entries.Count(e => e.Message.StartsWith("Analyzed ", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("a table marked twice is analyzed once");
  }

  /// <summary>A rewrite rolled back to its savepoint changed nothing, so its table is not analyzed.</summary>
  [Test]
  public async Task ARolledBackRewriteIsNotAnalyzedAsync() {
    var log = new SignalingListLogger();
    var rewrite = await _rewrittenTableAsync("wh_per_rolled_back", failAfterMarking: true);

    await CanonicalTemporalRewritePhase.ApplyAsync(_connect, LOCK_ID, [rewrite], TIMEOUT_SECONDS, log);

    await Assert.That(await _indexStatisticsAsync("wh_per_rolled_back_at")).IsEqualTo(0);
    await Assert.That(log.Entries.Where(e => e.Message.StartsWith("Analyzed ", StringComparison.Ordinal))).IsEmpty();
  }
}
