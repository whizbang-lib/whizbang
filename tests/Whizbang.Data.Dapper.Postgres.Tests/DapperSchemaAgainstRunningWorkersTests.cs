// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver's schema initialization against workers using the instance and role tables (#1636). It applies
/// the core tables' ensure on every start, and the ensure re-asserts every column with <c>ADD COLUMN IF NOT EXISTS</c>
/// and every index with <c>CREATE INDEX IF NOT EXISTS</c>, each of which took its table lock before finding nothing
/// to do. A restart therefore queued behind every running instance's heartbeat and claim.
/// </summary>
/// <remarks>
/// The worker is a real session in an open transaction that has read and written each table. The restart's
/// connection carries a <c>lock_timeout</c>, so a statement that waits for the worker fails with <c>55P03</c> rather
/// than hanging; the worker holds its locks until the test is over.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CatalogCheckedDdl.cs</code-under-test>
[Category("Integration")]
[NotInParallel("DapperSchemaInitialization")]
public sealed partial class DapperSchemaAgainstRunningWorkersTests {
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapperbusy");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  [Test]
  [Timeout(300000)]
  public async Task ARestart_WhileAWorkerHoldsTheInstanceAndRoleTables_DoesNotWaitForItAsync(CancellationToken cancellationToken) {
    await new PostgresSchemaInitializer(_database.ConnectionString).InitializeSchemaAsync(cancellationToken);
    await using var worker = new NpgsqlConnection(_database.ConnectionString);
    await worker.OpenAsync(cancellationToken);
    await using (var hold = new NpgsqlCommand("""
        BEGIN;
        SELECT count(*) FROM wh_service_instances; UPDATE wh_service_instances SET instance_id = instance_id WHERE false;
        SELECT count(*) FROM wh_instance_evictions; UPDATE wh_instance_evictions SET instance_id = instance_id WHERE false;
        SELECT count(*) FROM wh_instance_capabilities; UPDATE wh_instance_capabilities SET instance_id = instance_id WHERE false;
        SELECT count(*) FROM wh_role_assignments; UPDATE wh_role_assignments SET role = role WHERE false;
        SELECT count(*) FROM wh_role_candidates; UPDATE wh_role_candidates SET role = role WHERE false;
        """, worker)) {
      await hold.ExecuteNonQueryAsync(cancellationToken);
    }
    var lockTimeout = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Options = "-c lock_timeout=2000" }.ConnectionString;

    await Assert.That(async () => await new PostgresSchemaInitializer(lockTimeout).InitializeSchemaAsync(cancellationToken))
      .ThrowsNothing().Because("a restart against a current schema has nothing to apply to the tables the worker holds");

    await using var probe = new NpgsqlConnection(_database.ConnectionString);
    await probe.OpenAsync(cancellationToken);
    await using var read = new NpgsqlCommand($"SELECT state FROM pg_stat_activity WHERE pid = {worker.ProcessID}", probe);
    await Assert.That((string)(await read.ExecuteScalarAsync(cancellationToken))!).IsEqualTo("idle in transaction")
      .Because("the restart finished while the worker still held every table");
  }

  /// <summary>
  /// A deadlock in a migration file is retried in place, after a short back-off and at Warning, as on the EF Core
  /// driver: the file that lost it runs again, and the start completes in the same call. The deadlock is a real one
  /// raised by the server, once, while that file runs.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ADeadlockInAMigrationFile_IsRetriedInPlace_AndTheStartCompletesAsync(CancellationToken cancellationToken) {
    var (file, function) = _aFileWithAFunctionOfItsOwn();
    await using (var connection = new NpgsqlConnection(_database.ConnectionString)) {
      await connection.OpenAsync(cancellationToken);
      await using var trigger = new NpgsqlCommand($"""
        CREATE SEQUENCE public.test_deadlocks;
        CREATE FUNCTION public.test_inject_deadlock() RETURNS event_trigger LANGUAGE plpgsql AS $$
        DECLARE r record;
        BEGIN
          FOR r IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP
            IF r.object_identity LIKE 'public.{function}(%' AND nextval('public.test_deadlocks') = 1 THEN
              RAISE EXCEPTION 'injected deadlock' USING ERRCODE = '40P01';
            END IF;
          END LOOP;
        END $$;
        CREATE EVENT TRIGGER test_inject_deadlock ON ddl_command_end
          WHEN TAG IN ('CREATE FUNCTION') EXECUTE FUNCTION public.test_inject_deadlock();
        """, connection);
      await trigger.ExecuteNonQueryAsync(cancellationToken);
    }
    var log = new WarningLog();

    await new PostgresSchemaInitializer(_database.ConnectionString) { Logger = log }.InitializeSchemaAsync(cancellationToken);

    var deadlocks = log.Lines.Where(l => l.Contains("deadlock", StringComparison.OrdinalIgnoreCase)).ToList();
    await Assert.That(deadlocks).HasSingleItem().Because("one deadlock, retried in place, and no failed start after it");
    await Assert.That(deadlocks[0]).Matches(
      $@"^Warning Migration {Regex.Escape(file)} deadlocked : 40P01 injected deadlock; retrying it in \d+\.\d{{3}}s \(deadlock 1 of 5\)$");
    await using var probe = new NpgsqlConnection(_database.ConnectionString);
    await probe.OpenAsync(cancellationToken);
    await using var applied = new NpgsqlCommand(
      "SELECT status FROM wh_schema_migrations WHERE file_name = $1", probe) { Parameters = { new() { Value = file } } };
    await Assert.That((short)(await applied.ExecuteScalarAsync(cancellationToken))!).IsEqualTo((short)1)
      .Because("the file that lost the deadlock was applied by its retry");
  }

  /// <summary>
  /// A deadlock that does not clear fails the start once the in-place retries are spent, recorded on the file's ledger
  /// row like any other failure. This start has no logger, so the retries are not said anywhere, and still happen.
  /// </summary>
  [Test]
  [Timeout(300000)]
  public async Task ADeadlockThatKeepsHappening_FailsTheStart_OnceTheRetriesAreSpentAsync(CancellationToken cancellationToken) {
    var (file, function) = _aFileWithAFunctionOfItsOwn();
    await using (var connection = new NpgsqlConnection(_database.ConnectionString)) {
      await connection.OpenAsync(cancellationToken);
      await using var trigger = new NpgsqlCommand($"""
        CREATE SEQUENCE public.test_deadlocks;
        CREATE FUNCTION public.test_inject_deadlock() RETURNS event_trigger LANGUAGE plpgsql AS $$
        DECLARE r record;
        BEGIN
          FOR r IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP
            IF r.object_identity LIKE 'public.{function}(%' THEN
              PERFORM nextval('public.test_deadlocks');
              RAISE EXCEPTION 'injected deadlock' USING ERRCODE = '40P01';
            END IF;
          END LOOP;
        END $$;
        CREATE EVENT TRIGGER test_inject_deadlock ON ddl_command_end
          WHEN TAG IN ('CREATE FUNCTION') EXECUTE FUNCTION public.test_inject_deadlock();
        """, connection);
      await trigger.ExecuteNonQueryAsync(cancellationToken);
    }

    var failure = await Assert.That(async () =>
        await new PostgresSchemaInitializer(_database.ConnectionString).InitializeSchemaAsync(cancellationToken))
      .Throws<PostgresException>();

    await Assert.That(failure!.SqlState).IsEqualTo("40P01");
    await using var probe = new NpgsqlConnection(_database.ConnectionString);
    await probe.OpenAsync(cancellationToken);
    await using var attempts = new NpgsqlCommand("SELECT last_value FROM public.test_deadlocks", probe);
    await Assert.That((long)(await attempts.ExecuteScalarAsync(cancellationToken))!).IsEqualTo(6L)
      .Because("the first attempt and five in-place retries");
    await using var recorded = new NpgsqlCommand(
      "SELECT status FROM wh_schema_migrations WHERE file_name = $1", probe) { Parameters = { new() { Value = file } } };
    await Assert.That((short)(await recorded.ExecuteScalarAsync(cancellationToken))!).IsEqualTo((short)-1)
      .Because("a spent deadlock is recorded as the file's failure");
  }

  [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+public\.(?<name>\w+)\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex _createFunction();

  /// <summary>A file past the middle defining a function no other file defines, derived from the shipped files.</summary>
  private static (string File, string Function) _aFileWithAFunctionOfItsOwn() {
    var migrations = new PostgresMigrationProvider().GetMigrations();
    var definers = migrations.SelectMany(m => _createFunction().Matches(m.Sql).Select(x => x.Groups["name"].Value).Distinct())
      .GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    return migrations.Skip(migrations.Count / 2)
      .Where(m => !m.Sql.Contains("@whizbang:bootstrap", StringComparison.Ordinal))
      .Select(m => (File: m.Name, Function: _createFunction().Matches(m.Sql).Select(x => x.Groups["name"].Value)
        .FirstOrDefault(n => definers[n] == 1)))
      .First(p => p.Function is not null)!;
  }

  /// <summary>The Warning and Error lines a start logs.</summary>
  private sealed class WarningLog : ILogger {
    private readonly List<string> _lines = [];

    public List<string> Lines {
      get {
        lock (_lines) {
          return [.. _lines];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Warning) {
        lock (_lines) {
          _lines.Add($"{logLevel} {formatter(state, exception)}");
        }
      }
    }
  }
}
