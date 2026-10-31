// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The schema pass against workers that are using the instance and role tables (#1636).
/// </summary>
/// <remarks>
/// <para>
/// The reported failure: the migrations that add columns to the instance and role tables (109, 110, 184, 203) took
/// ACCESS EXCLUSIVE on them on every run, column present or not, while the service's own electors, assigner and
/// heartbeat had them open; with a second lock held in the same transaction the pass deadlocked them (40P01) and
/// retried until a run got lucky. The core tables' ensure does the same to <c>wh_service_instances</c>, and the
/// bootstrap closure the same to the role tables.
/// </para>
/// <para>
/// The worker here is a real session in an open transaction that has read and written every one of those tables,
/// which is what a vote, a heartbeat and a claim hold. The pass's connections carry a <c>lock_timeout</c>, so a
/// statement that would queue behind the worker fails with <c>55P03</c> instead of hanging, and the first line that
/// says so cancels the pass: the timeout detects a wait, it never ends one, because the worker holds its locks until
/// the test is over.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/CatalogCheckedDdl.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaStepRunner.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#idempotent-ddl-takes-no-lock</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public sealed partial class MigrationAgainstRunningWorkersTests {
  /// <summary>The tables the electors, the assigner, the heartbeat and the eviction paths use while a pass runs.</summary>
  private static readonly string[] _instanceAndRoleTables = [
    "wh_service_instances", "wh_instance_evictions", "wh_instance_capabilities", "wh_role_assignments",
    "wh_role_candidates", "wh_role_pending_work", "wh_standby_requests",
  ];

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("busytables");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>
  /// Every migration that adds a column or an index to an instance or role table runs again, with the bootstrap
  /// closure and the core tables, while a worker holds all of those tables: the pass completes without waiting for
  /// the worker once, because nothing it re-applies has anything left to do.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ARerun_WhileAWorkerHoldsTheInstanceAndRoleTables_CompletesWithoutWaitingForItAsync(
      CancellationToken cancellationToken) {
    await _initializeAsync(_database.ConnectionString, null, cancellationToken);
    var rerun = _filesAlteringInstanceOrRoleTables();
    await Assert.That(rerun).Contains("109_InstanceState.sql").And.Contains("110_StandbyHandshake.sql")
      .And.Contains("184_RoleAssignmentResilience.sql").And.Contains("203_PartitionAssigner.sql")
      .Because("derived from the shipped files, so the set cannot quietly shrink to nothing");
    await _executeAsync("DELETE FROM public.wh_schema_migrations WHERE file_name = ANY($1)", (object)rerun.ToArray());
    await _executeAsync("DELETE FROM public.wh_bootstrap_closure");
    var held = await _listAsync(
      "SELECT c.relname::text FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relname = ANY($1) ORDER BY 1",
      (object)_instanceAndRoleTables);
    await Assert.That(held.Count).IsGreaterThanOrEqualTo(5);
    await using var worker = await _workerHoldingAsync(held, cancellationToken);

    using var waited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var log = new PassLog(onLine: line => {
      if (line.Contains("55P03", StringComparison.Ordinal) || line.Contains("lock timeout", StringComparison.Ordinal)) {
        waited.Cancel();
      }
    });
    var lockTimeout = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Options = "-c lock_timeout=2000" }.ConnectionString;

    Exception? stopped = null;
    try {
      await _initializeAsync(lockTimeout, log, waited.Token);
    } catch (OperationCanceledException ex) when (waited.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
      stopped = ex;
    }

    await Assert.That(stopped).IsNull().Because("no statement of the pass may wait for the worker; it waited at: "
      + string.Join(" | ", log.Lines.Where(l => l.Contains("55P03", StringComparison.Ordinal) || l.Contains("lock timeout", StringComparison.Ordinal))));

    foreach (var file in rerun) {
      await Assert.That(log.Lines).Contains(l => l.EndsWith($" - Started : {file}", StringComparison.Ordinal))
        .Because($"{file} ran again under the worker, which is the case under test");
    }
    await Assert.That(await _scalarAsync<string>($"SELECT state FROM pg_stat_activity WHERE pid = {worker.ProcessID}"))
      .IsEqualTo("idle in transaction").Because("the pass finished while the worker still held every table");
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM public.wh_schema_migrations WHERE file_name = ANY($1) AND status IN (1, 2)", (object)rerun.ToArray()))
      .IsEqualTo((long)rerun.Count);
  }

  /// <summary>
  /// A deadlock in the middle of a fresh pass is retried in place after a short back-off and said at Warning: the
  /// step that lost it runs again, nothing before it runs again, the pass completes in the same attempt, and nothing
  /// is logged at Error. The deadlock is a real one raised by the server, once, while that file runs.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ADeadlockInAFreshPass_IsRetriedInPlace_AndThePassCompletesInTheSameAttemptAsync(
      CancellationToken cancellationToken) {
    var plan = MigrationStepTransactionTests.PickFailurePoint();
    await _executeAsync($"""
      CREATE SEQUENCE public.test_deadlocks;
      CREATE FUNCTION public.test_inject_deadlock() RETURNS event_trigger LANGUAGE plpgsql AS $$
      DECLARE r record;
      BEGIN
        FOR r IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP
          IF r.object_identity LIKE 'public.{plan.Function}(%' AND nextval('public.test_deadlocks') = 1 THEN
            RAISE EXCEPTION 'injected deadlock creating %', r.object_identity USING ERRCODE = '40P01';
          END IF;
        END LOOP;
      END $$;
      CREATE EVENT TRIGGER test_inject_deadlock ON ddl_command_end
        WHEN TAG IN ('CREATE FUNCTION') EXECUTE FUNCTION public.test_inject_deadlock();
      """);
    var log = new PassLog();

    await _initializeAsync(_database.ConnectionString, log, cancellationToken);

    var deadlocked = log.Lines.Where(l => l.Contains("Deadlocked", StringComparison.Ordinal)).ToList();
    await Assert.That(deadlocked).HasSingleItem();
    await Assert.That(deadlocked[0]).Matches(
      $@"^Warning Migration \d+ of \d+ - Deadlocked : {Regex.Escape(plan.File)} : 40P01 injected deadlock creating public\.{plan.Function}\(.*; retrying the step in \d+\.\d{{3}}s \(deadlock 1 of 5\)$");
    await Assert.That(log.Lines.Count(l => l.EndsWith($" - Started : {plan.File}", StringComparison.Ordinal))).IsEqualTo(2)
      .Because("the step that lost the deadlock is the step that runs again");
    await Assert.That(log.Lines.Count(l => l.EndsWith($" - Started : {plan.Before[^1]}", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the pass resumes at the failed step; nothing before it runs again");
    await Assert.That(log.Lines).DoesNotContain(l => l.StartsWith("Error ", StringComparison.Ordinal));
    await Assert.That(log.Lines).DoesNotContain(l => l.Contains("transient failure", StringComparison.Ordinal))
      .Because("the deadlock is absorbed by the step, not by a whole new attempt of the initializer");
    await Assert.That(log.Lines).Contains(l => _passComplete().IsMatch(l))
      .Because("the pass completed in the attempt that met the deadlock");
    await Assert.That(await _scalarAsync<bool>(
      "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = $1 AND pronamespace = 'public'::regnamespace)", plan.Function)).IsTrue();
  }

  // ─────────────────────────────────────────────────────────────────────────

  [GeneratedRegex(@"^Information Migration complete: (\d+) of \1 succeeded")]
  private static partial Regex _passComplete();

  [GeneratedRegex(@"(?:ALTER\s+TABLE|\bON)\s+public\.(?<table>wh_\w+)", RegexOptions.IgnoreCase)]
  private static partial Regex _alteredOrIndexed();

  /// <summary>Every migration that alters, or builds an index on, one of the instance and role tables.</summary>
  private static List<string> _filesAlteringInstanceOrRoleTables() =>
    [.. MigrationStepTransactionTests.AllMigrations()
      .Where(m => _alteredOrIndexed().Matches(m.Sql).Any(x => _instanceAndRoleTables.Contains(x.Groups["table"].Value)))
      .Select(m => m.Name + ".sql")];

  private static async Task _initializeAsync(string connectionString, ILogger? logger, CancellationToken cancellationToken) {
    await using var context = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
      .UseNpgsql(connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
    await context.EnsureWhizbangDatabaseInitializedAsync(
      logger, initConnectionString: connectionString, cancellationToken: cancellationToken);
  }

  /// <summary>A worker mid-transaction: it has read and written every table, and holds those locks until disposed.</summary>
  private async Task<NpgsqlConnection> _workerHoldingAsync(IEnumerable<string> tables, CancellationToken cancellationToken) {
    var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    var statements = string.Concat(tables.Select(t =>
      $"SELECT count(*) FROM public.{t}; UPDATE public.{t} SET {_firstColumn(t)} = {_firstColumn(t)} WHERE false; "));
    await using var command = new NpgsqlCommand("BEGIN; " + statements, connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
    return connection;
  }

  /// <summary>Any column will do for an UPDATE that touches no row: it still takes the table's write lock.</summary>
  private static string _firstColumn(string table) => table switch {
    "wh_role_assignments" or "wh_role_candidates" or "wh_role_pending_work" => "role",
    "wh_standby_requests" => "one_row",
    _ => "instance_id",
  };

  private async Task<List<string>> _listAsync(string sql, params object[] parameters) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var parameter in parameters) {
      command.Parameters.AddWithValue(parameter);
    }
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

  /// <summary>Every line the pass logs, with its level and any exception's message, in order.</summary>
  private sealed class PassLog(Action<string>? onLine = null) : ILogger {
    private readonly List<string> _lines = [];

    public List<string> Lines {
      get {
        lock (_lines) {
          return [.. _lines];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var line = exception is null
        ? $"{logLevel} {formatter(state, exception)}"
        : $"{logLevel} {formatter(state, exception)} [{exception.GetType().Name}: {exception.Message}]";
      lock (_lines) {
        _lines.Add(line);
      }
      onLine?.Invoke(line);
    }
  }
}
