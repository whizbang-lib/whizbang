// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Each step of the EF Core schema pass is a transaction of its own: one per migration file with that file's
/// ledger row, and one each for the core tables, the perspective tables, the constraints, the associations and the
/// registries.
/// </summary>
/// <remarks>
/// <para>
/// The pass used to be one transaction. A failure anywhere (a deadlock in one file on a fresh database) rolled back
/// every file that had succeeded, ledger rows included, so each retry started from nothing, took every lock again in
/// the same order and held all of them until the final commit (#1637).
/// </para>
/// <para>
/// The witnesses here are the ledger rows and a trigger that records every write applying a row, because "the retry
/// applied only file N onward" and "nothing was applied twice" are statements about which writes happened, and no
/// final state can tell a step applied once from the same step applied again.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/SchemaStepRunner.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard2")]
public partial class MigrationStepTransactionTests {
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("stepxact");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  /// <summary>
  /// A failure in migration file N keeps every file before it, recorded and in place, and the retry applies only N
  /// onward.
  /// </summary>
  /// <remarks>
  /// The failure is a real one, raised by the server while file N runs: an event trigger refuses to create a
  /// function only file N defines. It fails if the pass is one transaction (nothing before N survives), if a file's
  /// ledger row is written outside that file's transaction (rows without objects, or objects without rows), if the
  /// failure record makes N look current (the retry would skip it), or if the retry re-runs anything before N or
  /// the core tables it already committed.
  /// </remarks>
  [Test]
  [Timeout(300000)]
  public async Task AFailureInOneFile_KeepsTheFilesBeforeIt_AndTheRetryAppliesOnlyFromItOnAsync(
      CancellationToken cancellationToken) {
    var plan = PickFailurePoint();
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

    var firstAttempt = new LineLogger();
    await Assert.That(async () => await _initializeAsync(logger: firstAttempt, cancellationToken: cancellationToken))
      .Throws<PostgresException>()
      .Because("precondition: file N fails, and a failure that is not transient ends the attempt");

    var recorded = await _appliedFilesAsync();
    await Assert.That(recorded).IsEquivalentTo(plan.Before)
      .Because("every file before N committed with its own ledger row; nothing at or after N did");
    await Assert.That(await _functionExistsAsync(plan.EarlierFunction)).IsTrue()
      .Because($"{plan.EarlierFile} created {plan.EarlierFunction} and committed it before N failed");
    await Assert.That(await _functionExistsAsync(plan.Function)).IsFalse()
      .Because("file N rolled back");
    await Assert.That(await _scalarAsync<short>(
        "SELECT status FROM public.wh_schema_migrations WHERE file_name = $1", plan.File))
      .IsEqualTo((short)-1)
      .Because("the failure is recorded in a transaction of its own, after N's rolled back");

    // File N is step N + 2: the core tables come first. Y is checked against the ledger once the retry has finished.
    var lines = firstAttempt.Migration;
    var total = int.Parse(_totalLine().Match(lines[0]).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    var position = plan.Before.Length + 2;
    await Assert.That(lines).Contains($"Information Migration {position} of {total} - Started : {plan.File}")
      .Because("file N is counted after the core tables and every file before it; the lines were:\n"
        + string.Join("\n", lines.TakeLast(4)));
    var failedAt = lines.FindIndex(l => l.StartsWith(
      $"Error Migration {position} of {total} - Failed : {plan.File} : P0001 injected failure creating public.{plan.Function}(", StringComparison.Ordinal));
    await Assert.That(failedAt).IsGreaterThan(0).Because("file N's failure is logged at Error with its SQLSTATE");
    await Assert.That(lines.Count).IsEqualTo(failedAt + 2).Because("nothing after N runs, so nothing after it is logged but the summary");
    await Assert.That(lines[^1]).StartsWith(
      $"Error Migration stopped at {position} of {total}: {plan.File} : P0001 injected failure creating public.{plan.Function}(");
    await Assert.That(await _scalarAsync<string>(
        "SELECT status_description FROM public.wh_schema_migrations WHERE file_name = $1", plan.File))
      .StartsWith($"Failed: P0001 injected failure creating public.{plan.Function}(")
      .Because("the ledger says which step failed and why");

    await _executeAsync("""
      DROP EVENT TRIGGER test_refuse_one_function;
      DROP FUNCTION public.test_refuse_one_function();
      """);
    await _recordAppliedWritesAsync();

    var retry = new LineLogger();
    await _initializeAsync(logger: retry, cancellationToken: cancellationToken);

    await Assert.That(retry.Migration).Contains($"Information Migration {position} of {total} - Started : {plan.File}")
      .Because("the retry resumes at N with a fresh Started line");
    await Assert.That(retry.Migration).Contains($"Information Migration {position - 1} of {total} - Skipped : {plan.Before[^1]} (already current)");
    await Assert.That(retry.Migration).DoesNotContain(l => l.Contains($"Started : {plan.Before[^1]}", StringComparison.Ordinal));
    await Assert.That(retry.Migration[^1]).StartsWith($"Information Migration complete: {total} of {total} succeeded (");
    await Assert.That(total).IsEqualTo(MigrationFiles().Length + (int)await _scalarAsync<long>(
        "SELECT count(*) FROM public.wh_schema_migrations WHERE file_name LIKE 'perspective:%'") + 4)
      .Because("Y counts every file, every perspective table, and the core-table, constraint, association and registry steps");
    await Assert.That(await _loggedAsync("whizbang")).IsEquivalentTo(plan.FromN)
      .Because("the retry resumes at N: each file before it is current, and N's failure record does not make it look so");
    await Assert.That(await _loggedAsync("step")).DoesNotContain(s => s.EndsWith(":core-infrastructure", StringComparison.Ordinal))
      .Because("the core tables committed in the failed attempt, so the retry does not apply them again");
    await Assert.That(await _outstandingStepsAsync()).IsEqualTo(0L)
      .Because("the retry finished every step the failed attempt left outstanding");
  }

  /// <summary>
  /// An instance that starts while another is part way through a pass finishes it, and neither applies any step
  /// the other already applied.
  /// </summary>
  /// <remarks>
  /// The first instance is held between two of its steps by an observer, the only point at which it holds no
  /// lock, so the second runs into a pass that is genuinely half done. The second completes it; the first is then
  /// released into steps that are all already applied. This fails if either instance trusts a plan it made before
  /// taking a step's lock instead of re-checking that step's ledger row under it.
  /// </remarks>
  [Test]
  [Timeout(300000)]
  public async Task ASecondInstance_FinishesAPassPartWayThrough_AndNothingIsAppliedTwiceAsync(
      CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken: cancellationToken);
    var drifted = _driftableFiles();
    object driftedNames = drifted.ToArray();
    await _executeAsync(
      "UPDATE public.wh_schema_migrations SET content_hash = 'an-earlier-release' WHERE file_name = ANY($1)", driftedNames);
    await _recordAppliedWritesAsync();

    var observer = new HoldAfterStep(drifted[2]);
    var services = new ServiceCollection();
    services.AddSingleton<ISchemaInitializationObserver>(observer);
    await using var provider = services.BuildServiceProvider();

    var first = _initializeAsync(provider, cancellationToken: cancellationToken);
    var reached = await Task.WhenAny(observer.Holding, first);
    await Assert.That(ReferenceEquals(reached, observer.Holding)).IsTrue()
      .Because("the first instance commits each file on its own, so it is between two steps after the third drifted file");

    await _initializeAsync(cancellationToken: cancellationToken);
    observer.Release();
    await first;

    await Assert.That(await _appliedMoreThanOnceAsync()).IsEmpty()
      .Because("each step re-checks its ledger row under the lock, so neither instance applies what the other did");
    await Assert.That(await _loggedAsync("whizbang")).Contains(drifted[^1])
      .Because("the second instance applied the drifted files the first had not reached");
    await Assert.That(await _scalarAsync<long>(
        "SELECT count(*) FROM public.wh_schema_migrations WHERE content_hash = 'an-earlier-release'"))
      .IsEqualTo(0L);
    await Assert.That(await _outstandingStepsAsync()).IsEqualTo(0L);
  }

  /// <summary>
  /// Two instances starting together on an empty database both succeed and leave every file recorded.
  /// </summary>
  /// <remarks>
  /// A guard rather than a reproduction: it passed before the pass was split. Splitting releases the schema lock
  /// between steps, which is exactly when the other instance can take it, so the empty-database case is where a
  /// split that let them interleave badly would show.
  /// </remarks>
  [Test]
  [Timeout(300000)]
  public async Task TwoInstancesOnAnEmptyDatabase_ConvergeAsync(CancellationToken cancellationToken) {
    await Task.WhenAll(
      _initializeAsync(cancellationToken: cancellationToken),
      _initializeAsync(cancellationToken: cancellationToken));

    await Assert.That(await _appliedFilesAsync()).IsEquivalentTo(MigrationFiles())
      .Because("every migration is recorded as applied exactly as one instance alone would leave it");
    await Assert.That(await _outstandingStepsAsync()).IsEqualTo(0L);
  }

  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>Holds the instance that reports <paramref name="step"/> committed until released.</summary>
  private sealed class HoldAfterStep(string step) : ISchemaInitializationObserver {
    private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Holding => _holding.Task;

    public void Release() => _released.TrySetResult();

    public async ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) {
      if (stepName == step) {
        _holding.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
      }
    }
  }

  /// <summary>Keeps the pass's step lines, with their level, in the order they were written.</summary>
  private sealed class LineLogger : ILogger {
    private readonly List<string> _lines = [];

    public List<string> Migration {
      get {
        lock (_lines) {
          return [.. _lines.Where(l => _stepLine().IsMatch(l))];
        }
      }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lines) {
        _lines.Add($"{logLevel} {formatter(state, exception)}");
      }
    }
    private sealed class Scope : IDisposable { public void Dispose() { } }
  }

  internal sealed record FailurePoint(
    string File, string Function, string EarlierFile, string EarlierFunction, string[] Before, string[] FromN);

  [GeneratedRegex(@"^\w+ Migration (\d+ of \d+ - |complete: |stopped at )")]
  private static partial Regex _stepLine();

  [GeneratedRegex(@" of (\d+) - ")]
  private static partial Regex _totalLine();

  [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:[\w""]+\.)?""?(?<name>\w+)""?\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex _createFunction();

  internal static IReadOnlyList<MigrationScript> AllMigrations() => new PostgresMigrationProvider().GetMigrations();

  /// <summary>The ledger names of every migration, in the order the pass runs them.</summary>
  internal static string[] MigrationFiles() => [.. AllMigrations().Select(m => m.Name + ".sql")];

  private static IEnumerable<string> _functionsCreatedBy(string sql) =>
    _createFunction().Matches(sql).Select(m => m.Groups["name"].Value.ToLowerInvariant()).Distinct();

  internal static bool IsBootstrapFile(string sql) => sql.Contains("@whizbang:bootstrap", StringComparison.Ordinal);

  /// <summary>
  /// A file past the middle with a function no other file defines, and an earlier one whose function nothing after
  /// it drops; derived from the shipped files, so it moves with them rather than naming one.
  /// </summary>
  internal static FailurePoint PickFailurePoint() {
    var migrations = AllMigrations();
    var definers = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var name in migrations.SelectMany(m => _functionsCreatedBy(m.Sql))) {
      definers[name] = definers.GetValueOrDefault(name) + 1;
    }

    for (var n = migrations.Count / 2; n < migrations.Count; n++) {
      var function = IsBootstrapFile(migrations[n].Sql)
        ? null
        : _functionsCreatedBy(migrations[n].Sql).FirstOrDefault(f => definers[f] == 1);
      if (function is null) {
        continue;
      }

      for (var e = n - 1; e > 0; e--) {
        var earlier = _functionsCreatedBy(migrations[e].Sql).FirstOrDefault(f =>
          definers[f] == 1 && !migrations.Where((m, i) => i != e).Any(m => _drops(m.Sql, f)));
        if (earlier is not null) {
          var files = MigrationFiles();
          return new FailurePoint(files[n], function, files[e], earlier, files[..n], files[n..]);
        }
      }
    }

    throw new InvalidOperationException("no migration defines a function of its own; the arrangement cannot be made");
  }

  private static bool _drops(string sql, string function) =>
    Regex.IsMatch(sql, $@"DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?[\w"".]*\b{function}\b|drop_all_overloads\('{function}'\)",
      RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

  /// <summary>Six consecutive files from the middle that no bootstrap region touches.</summary>
  private static List<string> _driftableFiles() {
    var migrations = AllMigrations();
    var start = migrations.Count / 2;
    while (migrations.Skip(start).Take(6).Any(m => IsBootstrapFile(m.Sql))) {
      start++;
    }
    return [.. migrations.Skip(start).Take(6).Select(m => m.Name + ".sql")];
  }

  private async Task _initializeAsync(
      IServiceProvider? services = null, ILogger? logger = null, CancellationToken cancellationToken = default) {
    await using var context = new MinimalDbContext(new DbContextOptionsBuilder<MinimalDbContext>()
      .UseNpgsql(_database.ConnectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
    await context.EnsureWhizbangDatabaseInitializedAsync(
      logger, initConnectionString: _database.ConnectionString, serviceProvider: services, cancellationToken: cancellationToken);
  }

  /// <summary>Records every write that applies a ledger row: a first apply, or an update.</summary>
  private Task _recordAppliedWritesAsync() => _executeAsync("""
    CREATE TABLE public.test_applied_log (id bigserial PRIMARY KEY, file_name text NOT NULL, owner text NOT NULL);
    CREATE FUNCTION public.test_log_applied() RETURNS trigger LANGUAGE plpgsql AS $$
    BEGIN
      INSERT INTO public.test_applied_log (file_name, owner) VALUES (NEW.file_name, NEW.owner);
      RETURN NEW;
    END $$;
    CREATE TRIGGER test_log_applied AFTER INSERT OR UPDATE ON public.wh_schema_migrations
      FOR EACH ROW WHEN (NEW.status IN (1, 2)) EXECUTE FUNCTION public.test_log_applied();
    """);

  private async Task<List<string>> _loggedAsync(string owner) =>
    await _listAsync("SELECT file_name FROM public.test_applied_log WHERE owner = $1 ORDER BY id", owner);

  private Task<List<string>> _appliedMoreThanOnceAsync() =>
    _listAsync("SELECT file_name FROM public.test_applied_log GROUP BY file_name HAVING count(*) > 1");

  private async Task<List<string>> _appliedFilesAsync() {
    // The bootstrap creates the ledger without its owner column, so a pass that recorded nothing leaves neither.
    if (!await _scalarAsync<bool>(
        "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'wh_schema_migrations' AND column_name = 'owner')")) {
      return [];
    }
    return await _listAsync(
      "SELECT file_name FROM public.wh_schema_migrations WHERE owner = 'whizbang' AND status IN (1, 2) ORDER BY file_name");
  }

  private Task<long> _outstandingStepsAsync() =>
    _scalarAsync<long>("SELECT count(*) FROM public.wh_schema_migrations WHERE owner = 'step' AND status <= 0");

  private Task<bool> _functionExistsAsync(string name) =>
    _scalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname = $1)", name);

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
}
