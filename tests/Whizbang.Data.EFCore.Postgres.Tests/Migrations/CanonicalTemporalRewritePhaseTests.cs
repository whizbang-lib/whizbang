using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
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

  /// <summary>Whether the schema lock is held by anyone.</summary>
  private async Task<string> _lockHoldersAsync() =>
    await _scalarAsync($"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objid = {LOCK_ID}");

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
    var log = new ListLogger();

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
    var log = new ListLogger();

    var run = CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID, _rewrites("after-the-wait"), TIMEOUT_SECONDS, log);

    // The lock goes back only once the phase has said it is waiting, so the test proves a wait
    // happened rather than a lucky first attempt. The logger signals that line; nothing polls for
    // it and nothing drives a clock, for the reason ListLogger gives.
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
    var log = new ListLogger();

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
    var log = new ListLogger();
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
    var log = new ListLogger();

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
    var log = new ListLogger();

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
    var log = new ListLogger();

    await CanonicalTemporalRewritePhase.ApplyAsync(
      _connect, LOCK_ID,
      [("talkative", "DO $$ BEGIN RAISE NOTICE 'wh_per_thing: 3 row(s) converted'; END $$;")],
      TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("3 row(s) converted")))
      .IsTrue();
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

  /// <summary>Keeps every entry, so a test can ask what was said and at what level.</summary>
  /// <summary>
  /// Keeps every entry, and signals the moment one arrives that a test is waiting for.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The signal is what makes these tests deterministic. The phase spends real time on database
  /// round-trips, and a test that spins until it sees a log line, rather than being told, can hold
  /// the thread pool for the whole spin: the round-trip's continuation never runs, the phase makes
  /// no progress at all, and the test concludes it waited long enough. That reads as a failure in a
  /// couple of hundred milliseconds and reproduces only under load, which is what it did.
  /// </para>
  /// <para>
  /// Driving a <c>FakeTimeProvider</c> from such a spin makes it worse, because the loop creates
  /// the phase's whole budget in fake time while the phase is still on its first round-trip, so the
  /// phase gives up having never seen the lock released. These tests use the real clock: the budget
  /// is thirty seconds and the phase retries every quarter second, so releasing the lock decides
  /// the outcome and scheduling cannot.
  /// </para>
  /// <para>
  /// <see cref="Entries"/> hands out a snapshot, because the phase logs from its own thread while a
  /// test reads.
  /// </para>
  /// </remarks>
  private sealed class ListLogger : ILogger {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly List<(string Fragment, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>Every entry logged so far.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries {
      get {
        lock (_entries) {
          return [.. _entries];
        }
      }
    }

    /// <summary>Completes once an entry whose message contains <paramref name="fragment"/> arrives.</summary>
    /// <param name="fragment">The text to wait for.</param>
    /// <returns>A task that completes on the matching entry, or at once if one is already there.</returns>
    public Task WaitForAsync(string fragment) {
      lock (_entries) {
        if (_entries.Exists(e => e.Message.Contains(fragment, StringComparison.Ordinal))) {
          return Task.CompletedTask;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((fragment, signal));
        return signal.Task;
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      var ready = new List<TaskCompletionSource>();

      lock (_entries) {
        _entries.Add((logLevel, message));
        for (var i = _waiters.Count - 1; i >= 0; i--) {
          if (message.Contains(_waiters[i].Fragment, StringComparison.Ordinal)) {
            ready.Add(_waiters[i].Signal);
            _waiters.RemoveAt(i);
          }
        }
      }

      // Outside the lock: a continuation the phase runs must not be able to re-enter it.
      foreach (var signal in ready) {
        signal.SetResult();
      }
    }
  }
}
