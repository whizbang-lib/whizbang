// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// One step of the schema pass: its own transaction, the schema lock taken at its start, the step's ledger re-checked
/// under it, and a failure rolled back with only its record surviving.
/// </summary>
/// <remarks>
/// Against a real server, because every property here is about what PostgreSQL commits, rolls back and locks. The
/// work each test hands the runner writes to a table of its own, so what committed is read back rather than inferred.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaStepRunner.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public sealed class SchemaStepRunnerTests : IAsyncDisposable {
  private const string SCHEMA = "public";
  private PerTestDatabase _database;
  private MinimalDbContext _context = null!;
  private long _lockId;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("steprunner");
    await _executeAsync("""
      CREATE TABLE step_work (id int NOT NULL);
      CREATE TABLE step_failures (step text NOT NULL, message text NOT NULL);
      CREATE TABLE step_deferred (id int, CONSTRAINT step_deferred_once UNIQUE (id) DEFERRABLE INITIALLY DEFERRED);
      """);
    _context = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
      .UseNpgsql(_database.ConnectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
    await _context.Database.OpenConnectionAsync();
    _lockId = Random.Shared.NextInt64();
  }

  public async ValueTask DisposeAsync() => await _context.DisposeAsync();

  [After(Test)]
  public async Task TeardownAsync() {
    await _context.DisposeAsync();
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  // ─── A step that runs ────────────────────────────────────────────────────

  [Test]
  public async Task AStepThatIsNotCurrent_RunsAndCommits_AndIsReportedAsync() {
    var observer = new Observer();
    var runner = _runner(services: _services(observer));

    var outcome = await runner.RunAsync("one", _ => Task.FromResult(false), _workAsync(1), null, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(SchemaStepOutcome.Applied);
    await Assert.That(await _countAsync("step_work")).IsEqualTo(1L).Because("the step's work committed");
    await Assert.That(observer.Committed).IsEquivalentTo(["one"]);
    await Assert.That(_context.Database.CurrentTransaction).IsNull()
      .Because("the next step opens a transaction of its own, so this one is not left enlisted");
  }

  /// <summary>The re-check under the lock: a step already done is skipped, by a retry or by a second instance.</summary>
  [Test]
  public async Task AStepThatIsCurrentUnderTheLock_RunsNothingAsync() {
    var observer = new Observer();
    var runner = _runner(services: _services(observer));
    var applied = false;

    var outcome = await runner.RunAsync(
      "one", _ => Task.FromResult(true), _ => { applied = true; return Task.CompletedTask; }, null, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(SchemaStepOutcome.AlreadyCurrent);
    await Assert.That(applied).IsFalse();
    await Assert.That(observer.Committed).IsEquivalentTo(["one"]);
  }

  /// <summary>The lock is the step's, taken inside its transaction and released when it ends.</summary>
  [Test]
  public async Task TheLockIsHeldWhileTheStepRuns_AndReleasedWhenItCommitsAsync() {
    var runner = _runner();
    bool? heldElsewhereDuring = null;

    await runner.RunAsync("one", _ => Task.FromResult(false), async ct => {
      await using var other = new NpgsqlConnection(_database.ConnectionString);
      await other.OpenAsync(ct);
      heldElsewhereDuring = await AdvisoryLockProbe.IsHeldElsewhereAsync(other, _lockId, ct);
    }, null, CancellationToken.None);

    await using var after = new NpgsqlConnection(_database.ConnectionString);
    await after.OpenAsync();
    await Assert.That(heldElsewhereDuring).IsEqualTo(true);
    await Assert.That(await AdvisoryLockProbe.IsHeldElsewhereAsync(after, _lockId, CancellationToken.None)).IsFalse()
      .Because("a transaction-scoped lock ends with the step's transaction; nothing session-scoped can leak");
  }

  // ─── A step that fails ───────────────────────────────────────────────────

  /// <summary>
  /// A failure rolls back only its own step, and its record is written after the rollback so it is not lost with it.
  /// </summary>
  [Test]
  public async Task AFailedStep_RollsBack_AndItsFailureRecordSurvivesAsync() {
    var runner = _runner();
    var failure = new InvalidOperationException("the step failed");

    await Assert.That(async () => await runner.RunAsync("one", _ => Task.FromResult(false), async ct => {
      await _workAsync(1)(ct);
      throw failure;
    }, _recordAsync("one"), CancellationToken.None)).Throws<InvalidOperationException>().WithMessage("the step failed");

    await Assert.That(await _countAsync("step_work")).IsEqualTo(0L).Because("the step's own work rolled back");
    await Assert.That(await _countAsync("step_failures")).IsEqualTo(1L)
      .Because("the record is written outside the transaction that rolled back");
    await Assert.That(_context.Database.CurrentTransaction).IsNull();
  }

  [Test]
  public async Task AFailedStepWithNothingToRecord_StillPropagatesAsync() {
    var runner = _runner();

    await Assert.That(async () => await runner.RunAsync(
        "one", _ => Task.FromResult(false), _ => throw new InvalidOperationException("no record"), null, CancellationToken.None))
      .Throws<InvalidOperationException>();
  }

  /// <summary>A record that cannot be written is reported and does not replace the failure that matters.</summary>
  [Test]
  public async Task ARecordThatCannotBeWritten_IsLogged_AndTheStepsOwnFailurePropagatesAsync() {
    var logger = new RecordingLogger();
    var runner = _runner(logger: logger);

    await Assert.That(async () => await runner.RunAsync(
        "one", _ => Task.FromResult(false), _ => throw new InvalidOperationException("the step failed"),
        (_, _) => throw new TimeoutException("the record failed"), CancellationToken.None))
      .Throws<InvalidOperationException>().WithMessage("the step failed");

    await Assert.That(logger.Warnings).Contains(w => w.Contains("one", StringComparison.Ordinal));
  }

  /// <summary>A canceled start is not a failed migration, so it records nothing.</summary>
  [Test]
  public async Task ACanceledStep_RecordsNoFailureAsync() {
    var runner = _runner();
    using var cancel = new CancellationTokenSource();

    await Assert.That(async () => await runner.RunAsync("one", _ => Task.FromResult(false), ct => {
      cancel.Cancel();
      ct.ThrowIfCancellationRequested();
      return Task.CompletedTask;
    }, _recordAsync("one"), cancel.Token)).Throws<OperationCanceledException>();

    await Assert.That(await _countAsync("step_failures")).IsEqualTo(0L);
  }

  /// <summary>A step whose commit is refused failed, even though all of its statements succeeded.</summary>
  [Test]
  public async Task AStepWhoseCommitFails_IsAFailedStepAsync() {
    var runner = _runner();

    await Assert.That(async () => await runner.RunAsync("one", _ => Task.FromResult(false),
        ct => _context.Database.ExecuteSqlRawAsync("INSERT INTO step_deferred VALUES (1), (1)", ct),
        _recordAsync("one"), CancellationToken.None))
      .Throws<PostgresException>();

    await Assert.That(await _countAsync("step_deferred")).IsEqualTo(0L);
    await Assert.That(await _countAsync("step_failures")).IsEqualTo(1L);
  }

  /// <summary>
  /// A connection that died before the lock was taken: the step never ran, so there is nothing to record, and the
  /// failed rollback is reported rather than raised over the failure that matters.
  /// </summary>
  [Test]
  public async Task AConnectionLostBeforeTheLock_RecordsNothing_AndReportsTheRollbackAsync() {
    var logger = new RecordingLogger();
    var runner = _runner(logger: logger);
    await _terminateContextBackendAsync();
    var recorded = false;

    await Assert.That(async () => await runner.RunAsync("one", _ => Task.FromResult(false), _workAsync(1),
        (_, _) => { recorded = true; return Task.CompletedTask; }, CancellationToken.None))
      .Throws<Exception>();

    await Assert.That(recorded).IsFalse().Because("the step never ran under the lock, so it did not fail");
    await Assert.That(logger.Warnings).Contains(w => w.Contains("one", StringComparison.Ordinal))
      .Because("the rollback over a dead connection failed, and a catch that does not rethrow says what failed");
  }

  // ─── Another instance holds the lock ─────────────────────────────────────

  /// <summary>
  /// The lock is held and the schema is current when the step asks: another instance finished the work, so this one
  /// stops, and every later step of its pass does nothing.
  /// </summary>
  [Test]
  public async Task WhenAnotherInstanceFinishesTheSchema_ThePassStopsAsync() {
    await using var holder = await _holdLockAsync();
    var observer = new Observer();
    var runner = _runner(isSchemaCurrent: _ => Task.FromResult(true), services: _services(observer));
    var asked = 0;

    var first = await runner.RunAsync("one", _ => { asked++; return Task.FromResult(false); }, _workAsync(1), null, CancellationToken.None);
    var second = await runner.RunAsync("two", _ => { asked++; return Task.FromResult(false); }, _workAsync(2), null, CancellationToken.None);

    await Assert.That(first).IsEqualTo(SchemaStepOutcome.CompletedElsewhere);
    await Assert.That(second).IsEqualTo(SchemaStepOutcome.CompletedElsewhere);
    await Assert.That(runner.CompletedElsewhere).IsTrue();
    await Assert.That(asked).IsEqualTo(0).Because("the lock was never this instance's, so no step ran under it");
    await Assert.That(await _countAsync("step_work")).IsEqualTo(0L);
    await Assert.That(observer.Contended).IsEqualTo(1);
  }

  /// <summary>
  /// The holder lets go with the schema still behind (it failed, or it is between two of its steps): this instance
  /// takes the step, re-checking it under the lock as always.
  /// </summary>
  [Test]
  public async Task WhenTheHolderLetsGoWithWorkOutstanding_TheStepIsTakenAsync() {
    var holder = await _holdLockAsync();
    var observer = new Observer(onContended: async () => await holder.DisposeAsync());
    var time = new FakeTimeProvider();
    var runner = _runner(isSchemaCurrent: _ => Task.FromResult(false), services: _services(observer), time: time);

    var run = runner.RunAsync("one", _ => Task.FromResult(false), _workAsync(1), null, CancellationToken.None);
    while (!run.IsCompleted) {
      time.Advance(TimeSpan.FromMilliseconds(50));
      await Task.Yield();
    }

    await Assert.That(await run).IsEqualTo(SchemaStepOutcome.Applied);
    await Assert.That(observer.Contended).IsEqualTo(1);
    await Assert.That(await _countAsync("step_work")).IsEqualTo(1L);
  }

  // ─── What a pass says ────────────────────────────────────────────────────

  /// <summary>
  /// Every step of a pass logs one fixed form counted against the whole pass, and the pass ends with one summary.
  /// </summary>
  [Test]
  public async Task APass_LogsEveryStepAgainstItsTotal_AndOneSummaryAsync() {
    var logger = new RecordingLogger();
    var runner = _runner(logger: logger);

    runner.BeginPass(3);
    runner.Skip("a.sql");
    await runner.RunAsync("b.sql", _ => Task.FromResult(false), _workAsync(1), null, CancellationToken.None);
    await runner.RunAsync("constraints", _ => Task.FromResult(true), _workAsync(2), null, CancellationToken.None);
    runner.CompletePass();

    var lines = logger.Lines;
    await Assert.That(lines.Count).IsEqualTo(6);
    await Assert.That(lines[0]).IsEqualTo("Information Migration 1 of 3 - Skipped : a.sql (already current)");
    await Assert.That(lines[1]).IsEqualTo("Information Migration 2 of 3 - Started : b.sql");
    await Assert.That(lines[2]).Matches(@"^Information Migration 2 of 3 - Succeeded : b\.sql \(\d+\.\d{3}s\)$");
    await Assert.That(lines[3]).IsEqualTo("Information Migration 3 of 3 - Started : constraints");
    await Assert.That(lines[4]).IsEqualTo("Information Migration 3 of 3 - Skipped : constraints (already current)");
    await Assert.That(lines[5]).Matches(@"^Information Migration complete: 3 of 3 succeeded \(1 applied, 2 skipped\) in \d+\.\d{3}s$");
  }

  /// <summary>
  /// A failure at step N says so at Error with the server's SQLSTATE, then the pass stops there: nothing after N runs
  /// or is logged, and the summary names where it stopped. The record carries the same text.
  /// </summary>
  [Test]
  public async Task AFailureAtStepN_IsLoggedWithItsSqlState_AndThePassStopsThereAsync() {
    var logger = new RecordingLogger();
    var runner = _runner(logger: logger);
    string? recorded = null;

    runner.BeginPass(3);
    runner.Skip("a.sql");
    await Assert.That(async () => await runner.RunAsync("b.sql", _ => Task.FromResult(false),
        ct => _context.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE EXCEPTION 'boom'; END $$", ct),
        (ex, _) => { recorded = SchemaStepRunner.FailureText(ex); return Task.CompletedTask; }, CancellationToken.None))
      .Throws<PostgresException>();

    var lines = logger.Lines;
    await Assert.That(lines.Count).IsEqualTo(4);
    await Assert.That(lines[1]).IsEqualTo("Information Migration 2 of 3 - Started : b.sql");
    await Assert.That(lines[2]).IsEqualTo("Error Migration 2 of 3 - Failed : b.sql : P0001 boom");
    await Assert.That(lines[3]).IsEqualTo("Error Migration stopped at 2 of 3: b.sql : P0001 boom");
    await Assert.That(recorded).IsEqualTo("P0001 boom");
  }

  /// <summary>A failure the initializer will retry says so, and when.</summary>
  [Test]
  public async Task ARetryableFailure_SaysItWillRetry_AndWhenAsync() {
    var logger = new RecordingLogger();
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    var runner = new SchemaStepRunner(_context, _lockId, SCHEMA, _ => Task.FromResult(false), null, logger, time,
      retryDelayFor: _ => TimeSpan.FromSeconds(2));

    runner.BeginPass(1);
    await Assert.That(async () => await runner.RunAsync("a.sql", _ => Task.FromResult(false),
        _ => throw new TimeoutException("slow"), null, CancellationToken.None))
      .Throws<TimeoutException>();

    await Assert.That(logger.Lines[1]).IsEqualTo(
      "Error Migration 1 of 1 - Failed : a.sql : TimeoutException slow; retrying in 2.000s at 2026-10-22T12:00:02.0000000+00:00");
  }

  /// <summary>The step that begins a pass is not one of its counted steps, so it says nothing in this form.</summary>
  [Test]
  public async Task AStepBeforeThePassBegins_IsNotCountedAsync() {
    var logger = new RecordingLogger();
    var runner = _runner(logger: logger);

    await runner.RunAsync("pass", _ => Task.FromResult(false), _workAsync(1), null, CancellationToken.None);

    await Assert.That(logger.Lines).DoesNotContain(l => l.Contains("Migration ", StringComparison.Ordinal));
  }

  [Test]
  public async Task AFailureThatIsNotTheServers_IsNamedByItsTypeAsync() {
    await Assert.That(SchemaStepRunner.FailureText(new InvalidOperationException("odd"))).IsEqualTo("InvalidOperationException odd");
    await Assert.That(SchemaStepRunner.FailureText(new InvalidOperationException("outer",
        new PostgresException("inner", "ERROR", "ERROR", "40P01")))).IsEqualTo("40P01 inner");
  }

  // ─── Pieces ──────────────────────────────────────────────────────────────

  [Test]
  public async Task TheRetryDelayDoublesFromATenthOfASecond_ToACap_WithJitterUnderHalfAsync() {
    var random = new Random(1637);

    var first = SchemaStepRunner.LockRetryDelay(1, random);
    var second = SchemaStepRunner.LockRetryDelay(2, random);
    var late = SchemaStepRunner.LockRetryDelay(40, random);

    await Assert.That(first.TotalMilliseconds).IsGreaterThanOrEqualTo(100).And.IsLessThan(150);
    await Assert.That(second.TotalMilliseconds).IsGreaterThanOrEqualTo(200).And.IsLessThan(300);
    await Assert.That(late.TotalMilliseconds).IsGreaterThanOrEqualTo(20_000).And.IsLessThan(30_000);
  }

  [Test]
  public async Task NullArgumentsAreRefusedAsync() {
    await Assert.That(() => new SchemaStepRunner(null!, 1, SCHEMA, _ => Task.FromResult(true), null, null))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new SchemaStepRunner(_context, 1, null!, _ => Task.FromResult(true), null, null))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new SchemaStepRunner(_context, 1, SCHEMA, null!, null, null))
      .Throws<ArgumentNullException>();
    var runner = _runner();
    await Assert.That(async () => await runner.RunAsync(null!, _ => Task.FromResult(true), _ => Task.CompletedTask, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await runner.RunAsync("one", null!, _ => Task.CompletedTask, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await runner.RunAsync("one", _ => Task.FromResult(true), null!, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  // ─────────────────────────────────────────────────────────────────────────

  private SchemaStepRunner _runner(
      Func<CancellationToken, Task<bool>>? isSchemaCurrent = null, IServiceProvider? services = null,
      ILogger? logger = null, TimeProvider? time = null) =>
    new(_context, _lockId, SCHEMA, isSchemaCurrent ?? (_ => Task.FromResult(false)), services, logger, time);

  private static ServiceProvider _services(ISchemaInitializationObserver observer) {
    var services = new ServiceCollection();
    services.AddSingleton(observer);
    return services.BuildServiceProvider();
  }

  private Func<CancellationToken, Task> _workAsync(int id) =>
    ct => _context.Database.ExecuteSqlAsync($"INSERT INTO step_work VALUES ({id})", ct);

  /// <summary>Writes the failure on the context's own connection, which is where the generated pass writes it.</summary>
  private Func<Exception, CancellationToken, Task> _recordAsync(string step) =>
    async (ex, ct) => {
      await using var command = ((NpgsqlConnection)_context.Database.GetDbConnection()).CreateCommand();
      command.CommandText = "INSERT INTO step_failures VALUES ($1, $2)";
      command.Parameters.AddWithValue(step);
      command.Parameters.AddWithValue(ex.Message);
      await command.ExecuteNonQueryAsync(ct);
    };

  /// <summary>Another instance, mid-step: a transaction holding the schema lock. Disposing it releases the lock.</summary>
  private async Task<NpgsqlConnection> _holdLockAsync() {
    var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    var transaction = await connection.BeginTransactionAsync();
    await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction);
    command.Parameters.AddWithValue(_lockId);
    await command.ExecuteNonQueryAsync();
    return connection;
  }

  private async Task _terminateContextBackendAsync() {
    await using var pidCommand = ((NpgsqlConnection)_context.Database.GetDbConnection()).CreateCommand();
    pidCommand.CommandText = "SELECT pg_backend_pid()";
    var pid = (int)(await pidCommand.ExecuteScalarAsync())!;
    await using var killer = new NpgsqlConnection(_database.ConnectionString);
    await killer.OpenAsync();
    await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend($1)", killer);
    kill.Parameters.AddWithValue(pid);
    await kill.ExecuteScalarAsync();
  }

  private async Task<long> _countAsync(string table) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
    return (long)(await command.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private sealed class Observer(Func<Task>? onContended = null) : ISchemaInitializationObserver {
    public List<string> Committed { get; } = [];
    public int Contended { get; private set; }

    public async ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) {
      Contended++;
      if (onContended is not null) {
        await onContended();
      }
    }

    public ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) {
      Committed.Add(stepName);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class RecordingLogger : ILogger {
    public List<string> Warnings { get; } = [];
    public List<string> Lines { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
      if (logLevel >= LogLevel.Information) {
        Lines.Add($"{logLevel} {formatter(state, exception)}");
      }
    }
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }
}
