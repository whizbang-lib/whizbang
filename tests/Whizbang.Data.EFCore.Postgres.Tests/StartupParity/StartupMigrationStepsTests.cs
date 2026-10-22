// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Migrations;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.StartupParity;

/// <summary>
/// The schema pass as a host runs it at startup, one transaction per step (#1637): a real host with the EF Core
/// driver registered as an application registers it, against a real server, with the step lines read from the
/// host's own logging pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here stands in for the migration runner. The host's initializer service runs the generated pass; a
/// failure is a real one, raised by the server while a migration file runs; every interleaving is fixed by an
/// observer signal, never by time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaStepRunner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangDatabaseInitializerService.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public sealed partial class StartupMigrationStepsTests {
  private static readonly StartupDriver _driver = StartupDriver.For("efcore");
  private PerTestDatabase _database;
  private readonly List<ServiceProvider> _instances = [];

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("startsteps");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    foreach (var instance in _instances) {
      await instance.DisposeAsync();
    }
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>
  /// A fresh database migrates to completion, and every step of the pass says so against the whole pass: Started
  /// and Succeeded for each of the Y steps in order, then one summary.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task AFreshDatabase_LogsEveryStepStartedAndSucceeded_ThenTheSummaryAsync(CancellationToken cancellationToken) {
    var log = new CapturedLog();

    await _startAsync(log, cancellationToken: cancellationToken);

    var lines = log.StepLines;
    var total = _totalOf(lines);
    await Assert.That(total).IsEqualTo(
      MigrationStepTransactionTests.MigrationFiles().Length + (int)await _scalarAsync<long>(
        "SELECT count(*) FROM wh_schema_migrations WHERE file_name LIKE 'perspective:%'") + 4)
      .Because("Y counts the migration files, the perspective tables, and the core-table, constraint, association and registry steps");
    await Assert.That(lines.Count).IsEqualTo((2 * total) + 1);
    for (var x = 1; x <= total; x++) {
      await Assert.That(lines[(2 * x) - 2]).StartsWith($"Information Migration {x} of {total} - Started : ");
      await Assert.That(lines[(2 * x) - 1]).Matches($@"^Information Migration {x} of {total} - Succeeded : \S+ \(\d+\.\d{{3}}s\)$");
    }
    await Assert.That(lines[^1]).Matches($@"^Information Migration complete: {total} of {total} succeeded \({total} applied, 0 skipped\) in \d+\.\d{{3}}s$");
  }

  /// <summary>
  /// A step that fails keeps every step before it committed, records its own failure, and says where the pass
  /// stopped; the next attempt resumes at that step and runs nothing that is committed and unchanged.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task AFailedStep_KeepsWhatCommitted_RecordsItsError_AndTheRetryResumesThereAsync(CancellationToken cancellationToken) {
    var plan = MigrationStepTransactionTests.PickFailurePoint();
    await _executeAsync($"""
      CREATE FUNCTION public.test_refuse_one_function() RETURNS event_trigger LANGUAGE plpgsql AS $$
      DECLARE r record;
      BEGIN
        FOR r IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP
          IF r.object_identity LIKE 'public.{plan.Function}(%' THEN
            RAISE EXCEPTION 'injected failure creating %', r.object_identity;
          END IF;
        END LOOP;
      END $$;
      CREATE EVENT TRIGGER test_refuse_one_function ON ddl_command_end
        WHEN TAG IN ('CREATE FUNCTION') EXECUTE FUNCTION public.test_refuse_one_function();
      """);
    var log = new CapturedLog();
    var observer = new Observer(holdFirstFailure: true);
    var instance = _compose(log, observer, blocking: false);
    var gate = instance.GetRequiredService<ISchemaReadyGate>();

    await _initializer(instance).StartAsync(cancellationToken);
    await observer.AttemptFailed.Task.WaitAsync(cancellationToken);

    var failed = log.StepLines;
    var failedAt = failed.FindIndex(l => l.StartsWith("Error Migration ", StringComparison.Ordinal) && l.Contains($" - Failed : {plan.File} : P0001 injected failure creating", StringComparison.Ordinal));
    await Assert.That(failedAt).IsGreaterThan(0).Because("the failed step is logged at Error with its SQLSTATE and the server's message");
    var position = Regex.Match(failed[failedAt], @"Migration (\d+ of \d+)").Groups[1].Value;
    await Assert.That(failed[failedAt - 1]).IsEqualTo($"Information Migration {position} - Started : {plan.File}");
    await Assert.That(failed.Count).IsEqualTo(failedAt + 2).Because("no step after the failed one runs");
    await Assert.That(failed[^1]).StartsWith($"Error Migration stopped at {position}: {plan.File} : P0001 injected failure creating");
    await Assert.That(await _listAsync(
        "SELECT file_name FROM wh_schema_migrations WHERE owner = 'whizbang' AND status IN (1, 2) ORDER BY file_name"))
      .IsEquivalentTo(plan.Before).Because("every step before the failure committed, with its ledger row");
    await Assert.That(await _scalarAsync<short>("SELECT status FROM wh_schema_migrations WHERE file_name = $1", plan.File))
      .IsEqualTo((short)-1);
    await Assert.That(await _scalarAsync<string>("SELECT status_description FROM wh_schema_migrations WHERE file_name = $1", plan.File))
      .StartsWith("Failed: P0001 injected failure creating");
    await Assert.That(gate.IsReady).IsFalse();

    await _executeAsync("DROP EVENT TRIGGER test_refuse_one_function; DROP FUNCTION public.test_refuse_one_function();");
    log.Clear();
    observer.ReleaseFailure();
    await gate.WaitForReadyAsync(cancellationToken);

    var retried = log.StepLines;
    await Assert.That(retried).Contains($"Information Migration {position} - Started : {plan.File}")
      .Because("the retry resumes at the failed step");
    foreach (var before in plan.Before) {
      await Assert.That(retried).DoesNotContain(l => l.EndsWith($"Started : {before}", StringComparison.Ordinal))
        .Because($"{before} committed and is unchanged");
    }
    await Assert.That(log.All).DoesNotContain(l => l.Contains("re-running because", StringComparison.Ordinal)
        && plan.Before.Any(b => l.Contains(b, StringComparison.Ordinal)))
      .Because("a committed file's functions are in place, so no sweep finds them missing");
    await Assert.That(retried[^1]).StartsWith("Information Migration complete: ");
  }

  /// <summary>A restart against a current database applies nothing: no step line, and no DDL at all.</summary>
  [Test]
  [Timeout(300000)]
  public async Task ARestartOnACurrentDatabase_RunsNoStep_AndNoDdlAsync(CancellationToken cancellationToken) {
    await _startAsync(new CapturedLog(), cancellationToken: cancellationToken);
    await _executeAsync("""
      CREATE TABLE public.test_ddl_seen (tag text, identity text);
      CREATE FUNCTION public.test_ddl_record() RETURNS event_trigger LANGUAGE plpgsql AS $$
      BEGIN
        INSERT INTO public.test_ddl_seen SELECT command_tag, object_identity FROM pg_event_trigger_ddl_commands();
      END $$;
      CREATE EVENT TRIGGER test_ddl_watch ON ddl_command_end EXECUTE FUNCTION public.test_ddl_record();
      """);
    var restart = new CapturedLog();

    await _startAsync(restart, cancellationToken: cancellationToken);

    await Assert.That(restart.StepLines.Where(l => l.Contains(" - Started : ", StringComparison.Ordinal))).IsEmpty()
      .Because("every step is current, so the fast path applies nothing");
    await Assert.That(await _listAsync("SELECT tag || ' ' || coalesce(identity, '') FROM public.test_ddl_seen")).IsEmpty()
      .Because("a current schema costs a restart no DDL, and so no relation lock");
  }

  /// <summary>
  /// A session holding locks on the instance and role tables while the pass runs, as the electors and the eviction
  /// path do at startup, makes the pass wait; it does not deadlock it, and nothing committed is rolled back.
  /// </summary>
  /// <remarks>
  /// The pass is held after 110 (which alters <c>wh_instance_evictions</c>) commits. The session then takes a share
  /// lock on <c>wh_role_assignments</c>, which 184 alters, and the pass is released into it. The session then takes
  /// a row-exclusive lock on <c>wh_instance_evictions</c>. In one transaction the pass would still hold 110's exclusive
  /// lock there while waiting on the session: a deadlock, whichever of the two requests came first. Committed per
  /// step, 110's lock is gone and the session's request is granted at once.
  /// </remarks>
  [Test]
  [Timeout(300000)]
  public async Task ASessionHoldingTheInstanceAndRoleTables_DoesNotDeadlockThePassAsync(CancellationToken cancellationToken) {
    await _startAsync(new CapturedLog(), cancellationToken: cancellationToken);
    var evictions = MigrationStepTransactionTests.MigrationFiles().Single(f => f.StartsWith("110_", StringComparison.Ordinal));
    var roles = MigrationStepTransactionTests.MigrationFiles().Single(f => f.StartsWith("184_", StringComparison.Ordinal));
    object both = new[] { evictions, roles };
    await _executeAsync("UPDATE wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name = ANY($1)", both);
    var log = new CapturedLog();
    var observer = new Observer(holdAfter: evictions);

    var start = _startAsync(log, observer, cancellationToken);
    var reached = await Task.WhenAny(observer.Holding.Task, start);
    await Assert.That(ReferenceEquals(reached, observer.Holding.Task)).IsTrue()
      .Because("110 commits on its own, so the pass is between two steps here");

    await using var session = new NpgsqlConnection(_database.ConnectionString);
    await session.OpenAsync(cancellationToken);
    await using var held = await session.BeginTransactionAsync(cancellationToken);
    await _inAsync(session, held, "LOCK TABLE wh_role_assignments, wh_service_instances IN ACCESS SHARE MODE", cancellationToken);
    observer.Release();
    await _inAsync(session, held, "LOCK TABLE wh_instance_evictions IN ROW EXCLUSIVE MODE", cancellationToken);
    await held.CommitAsync(cancellationToken);
    await start;

    await Assert.That(log.All).DoesNotContain(l => l.Contains("40P01", StringComparison.Ordinal));
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM wh_schema_migrations WHERE file_name = ANY($1) AND content_hash <> 'an-earlier-release' AND status IN (1, 2)",
        both)).IsEqualTo(2L)
      .Because("both steps committed, and 110's commit was never undone");
  }

  // ─────────────────────────────────────────────────────────────────────────

  [GeneratedRegex(@"^\w+ Migration (\d+ of \d+ - |complete: |stopped at )")]
  private static partial Regex _stepLine();

  private static int _totalOf(List<string> lines) =>
    int.Parse(Regex.Match(lines[0], @"Migration \d+ of (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

  private ServiceProvider _compose(CapturedLog log, Observer? observer = null, bool blocking = true) {
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Information));
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.Configure<SchemaInitializationOptions>(o => {
      o.NonBlockingSchemaInit = !blocking;
      o.InitRetryDelay = TimeSpan.Zero;
    });
    if (observer is not null) {
      services.AddSingleton<ISchemaInitializationObserver>(observer);
    }
    _driver.Register(services, _database.ConnectionString);
    var provider = services.BuildServiceProvider();
    _instances.Add(provider);
    return provider;
  }

  private static WhizbangDatabaseInitializerService _initializer(IServiceProvider instance) =>
    instance.GetRequiredService<WhizbangDatabaseInitializerService>();

  private async Task _startAsync(CapturedLog log, Observer? observer = null, CancellationToken cancellationToken = default) =>
    await _initializer(_compose(log, observer)).StartAsync(cancellationToken);

  private static async Task _inAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct) {
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync(ct);
  }

  private async Task<List<string>> _listAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    var values = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      values.Add(reader.GetString(0));
    }
    return values;
  }

  private async Task<T> _scalarAsync<T>(string sql, params object[] parameters) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var parameter in parameters) {
      command.Parameters.AddWithValue(parameter);
    }
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql, params object[] parameters) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var parameter in parameters) {
      command.Parameters.AddWithValue(parameter);
    }
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>Holds the first failed attempt, or the pass after one step, until released.</summary>
  private sealed class Observer(bool holdFirstFailure = false, string? holdAfter = null) : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AttemptFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseFailure() => _release.TrySetResult();
    public void Release() => _release.TrySetResult();

    public async ValueTask OnAttemptFailedAsync(int attempt, Exception exception, CancellationToken cancellationToken) {
      AttemptFailed.TrySetResult();
      if (holdFirstFailure && attempt == 1) {
        await _release.Task.WaitAsync(cancellationToken);
      }
    }

    public async ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) {
      if (stepName == holdAfter) {
        Holding.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
      }
    }
  }

  /// <summary>
  /// The host's log as its logging pipeline formats it: a provider registered like any other, so a line is
  /// asserted exactly as an operator would read it.
  /// </summary>
  private sealed class CapturedLog : ILoggerProvider {
    private readonly List<string> _lines = [];

    public List<string> All {
      get {
        lock (_lines) {
          return [.. _lines];
        }
      }
    }

    public List<string> StepLines => [.. All.Where(l => _stepLine().IsMatch(l))];

    public void Clear() {
      lock (_lines) {
        _lines.Clear();
      }
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this);

    public void Dispose() { }

    private sealed class Writer(CapturedLog log) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter) {
        lock (log._lines) {
          log._lines.Add($"{logLevel} {formatter(state, exception)}");
        }
      }
    }
  }
}
