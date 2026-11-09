// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Containers;

/// <summary>
/// The migrated schema every <see cref="EFCoreTestBase"/> test starts from is built once, as a template database, and
/// each test gets a copy of it (#2167).
/// </summary>
/// <remarks>
/// Each test here works on a template of its own, named under <see cref="TEST_PREFIX"/>, so it neither waits for nor
/// disturbs the template the rest of the run copies.
/// </remarks>
[Category("Integration")]
[Category("Shard2")]
public class EFCoreSchemaTemplateTests {
  /// <summary>The prefix this class's own templates are named under, apart from the run's.</summary>
  private const string TEST_PREFIX = "eftmplt";

  private static string _newTemplateName() =>
    string.Create(CultureInfo.InvariantCulture, $"{TEST_PREFIX}_{Guid.NewGuid():N}");

  private static Task _migrateAsync(WorkCoordinationDbContext context, CancellationToken cancellationToken) =>
    context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);

  private static async Task<T?> _scalarAsync<T>(string sql, string? databaseName, CancellationToken cancellationToken,
      string? database = null) {
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) { Pooling = false };
    if (database is not null) {
      builder.Database = database;
    }
    await using var connection = new NpgsqlConnection(builder.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, connection);
    if (databaseName is not null) {
      _ = command.Parameters.AddWithValue("name", databaseName);
    }
    var value = await command.ExecuteScalarAsync(cancellationToken);
    return value is null or DBNull ? default : (T)value;
  }

  private static Task<uint?> _oidAsync(string name, CancellationToken cancellationToken) =>
    _scalarAsync<uint?>("SELECT oid FROM pg_database WHERE datname = @name", name, cancellationToken);

  /// <summary>Runs <paramref name="change"/> inside the template, which refuses connections between uses.</summary>
  private static async Task _changeTemplateAsync(string name, string change, CancellationToken cancellationToken) {
    await _scalarAsync<object>($"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS true", null, cancellationToken);
    await _scalarAsync<object>(change, null, cancellationToken, database: name);
    await _scalarAsync<object>($"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS false", null, cancellationToken);
  }

  /// <summary>
  /// Whether a copy of the template is the full schema (the schema pass changes nothing in its ledger, and the core
  /// and perspective tables are there) and which marker it carries.
  /// </summary>
  private static async Task<(bool Current, string? Marker)> _copyIsCurrentAsync(string name, CancellationToken cancellationToken) {
    var copy = await PerTestDatabaseFactory.CreateFromTemplateAsync("eftmpltcopy", name, cancellationToken);
    try {
      var marker = await _scalarAsync<string>(
        "SELECT obj_description('public'::regnamespace, 'pg_namespace')", null, cancellationToken, database: copy.Name);
      var tables = await _scalarAsync<bool>(
        "SELECT to_regclass('public.wh_event_store') IS NOT NULL AND to_regclass('public.wh_per_order') IS NOT NULL",
        null, cancellationToken, database: copy.Name);
      var before = await _scalarAsync<string>(EFCoreSchemaTemplate.LEDGER, null, cancellationToken, database: copy.Name);
      var (dataSource, options) = EFCoreTestBase.CreateContextOptions(
        new NpgsqlConnectionStringBuilder(copy.ConnectionString) { Pooling = false }.ConnectionString);
      await using (dataSource) {
        await using var context = new WorkCoordinationDbContext(options);
        await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
      }
      var after = await _scalarAsync<string>(EFCoreSchemaTemplate.LEDGER, null, cancellationToken, database: copy.Name);
      return (tables && !string.IsNullOrEmpty(before) && before == after, marker);
    } finally {
      await PerTestDatabaseFactory.DropAsync(copy.Name, cancellationToken: CancellationToken.None);
    }
  }

  private static async Task _dropTemplateAsync(string name) {
    await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await admin.OpenAsync();
    await using var unmark = new NpgsqlCommand(
      $"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_database WHERE datname = '{name}') THEN "
      + $"ALTER DATABASE {name} WITH IS_TEMPLATE false; END IF; END $$", admin);
    await unmark.ExecuteNonQueryAsync();
    await PerTestDatabaseFactory.DropAsync(name);
  }

  [Test]
  public async Task TheFingerprint_IsStableAndNamesTheTemplateAsync() {
    var fingerprint = EFCoreSchemaTemplate.Fingerprint;

    await Assert.That(EFCoreSchemaTemplate.ComputeFingerprint()).IsEqualTo(fingerprint)
      .Because("every process with the same migrations has to arrive at the same template");
    await Assert.That(fingerprint).Matches("^[0-9a-f]{64}$");
    await Assert.That(EFCoreSchemaTemplate.TemplateName)
      .IsEqualTo($"{EFCoreSchemaTemplate.TEMPLATE_PREFIX}_{fingerprint[..EFCoreSchemaTemplate.NAME_HASH_LENGTH]}");
    await Assert.That(EFCoreSchemaTemplate.TemplateName.Length).IsLessThanOrEqualTo(63)
      .Because("PostgreSQL truncates a longer identifier silently");
  }

  [Test]
  [Timeout(240_000)]
  public async Task AMissingTemplate_IsBuiltMigratedMarkedAndClosedToConnectionsAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    try {
      var outcome = await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-built", _migrateAsync, cancellationToken);

      await Assert.That(outcome).IsEqualTo(TemplateOutcome.Built);
      await Assert.That(await _scalarAsync<bool>(
          "SELECT datistemplate AND NOT datallowconn FROM pg_database WHERE datname = @name", name, cancellationToken))
        .IsTrue()
        .Because("a connection to the template makes every copy of it fail with 55006, so it accepts none");
      var stamp = await _scalarAsync<string>(
        "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = @name", name, cancellationToken);
      await Assert.That(EFCoreSchemaTemplate.TryReadStamp(stamp, out _)).IsTrue()
        .Because("the sweep ages a template by the stamp its last user left");

      var (current, marker) = await _copyIsCurrentAsync(name, cancellationToken);
      await Assert.That(current).IsTrue()
        .Because("a copy has to be the schema a test would otherwise have migrated for itself");
      await Assert.That(marker).IsEqualTo(EFCoreSchemaTemplate.MarkerFor("fp-built"));
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  [Test]
  [Timeout(240_000)]
  public async Task ACurrentTemplate_IsReusedNotRebuiltAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    try {
      await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-reused", _migrateAsync, cancellationToken);
      var oid = await _oidAsync(name, cancellationToken);

      var outcome = await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-reused", _migrateAsync, cancellationToken);

      await Assert.That(outcome).IsEqualTo(TemplateOutcome.Reused);
      await Assert.That(await _oidAsync(name, cancellationToken)).IsEqualTo(oid)
        .Because("a template that is current is the same database, not one built again");
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  [Test]
  [Timeout(240_000)]
  public async Task ATemplateWhoseSchemaIsNoLongerCurrent_IsRebuiltAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    try {
      await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-stale", _migrateAsync, cancellationToken);
      var oid = await _oidAsync(name, cancellationToken);
      // What a changed perspective looks like to the schema's own currency check: its ledger rows no longer match.
      await _changeTemplateAsync(name, "DELETE FROM public.wh_schema_migrations WHERE owner = 'perspective'",
        cancellationToken);

      var outcome = await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-stale", _migrateAsync, cancellationToken);

      await Assert.That(outcome).IsEqualTo(TemplateOutcome.Rebuilt);
      await Assert.That(await _oidAsync(name, cancellationToken)).IsNotEqualTo(oid);
      var (current, _) = await _copyIsCurrentAsync(name, cancellationToken);
      await Assert.That(current).IsTrue();
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  [Test]
  [Timeout(240_000)]
  public async Task ATemplateBuiltForAnotherFingerprint_IsRebuiltAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    try {
      await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-before", _migrateAsync, cancellationToken);

      var outcome = await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-after", _migrateAsync, cancellationToken);

      await Assert.That(outcome).IsEqualTo(TemplateOutcome.Rebuilt);
      var (_, marker) = await _copyIsCurrentAsync(name, cancellationToken);
      await Assert.That(marker).IsEqualTo(EFCoreSchemaTemplate.MarkerFor("fp-after"));
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  [Test]
  [Timeout(240_000)]
  public async Task ADatabaseUnderTheNameThatIsNotATemplate_IsReplacedAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    try {
      // What a build that stopped between its rename and marking the database a template leaves behind.
      var leftover = await PerTestDatabaseFactory.CreateAsync("eftmpltleft", cancellationToken: cancellationToken);
      await _scalarAsync<object>($"ALTER DATABASE {leftover.Name} RENAME TO {name}", null, cancellationToken);

      var outcome = await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-leftover", _migrateAsync, cancellationToken);

      await Assert.That(outcome).IsEqualTo(TemplateOutcome.Rebuilt);
      var (current, _) = await _copyIsCurrentAsync(name, cancellationToken);
      await Assert.That(current).IsTrue();
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  /// <summary>
  /// Two processes reaching for a template that does not exist yet: one builds it, the other waits for it and uses it.
  /// </summary>
  /// <remarks>
  /// The two calls go straight to the cross-process step, past the in-process gate, which is what two test processes
  /// sharing one container do. Without the advisory lock both would build, and the second rename would fail on the
  /// name the first took (42P04).
  /// </remarks>
  [Test]
  [Timeout(240_000)]
  public async Task CallersRacingForAMissingTemplate_BuildItOnceAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    try {
      var outcomes = await Task.WhenAll(
        EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-race", _migrateAsync, cancellationToken),
        EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-race", _migrateAsync, cancellationToken));

      await Assert.That(outcomes.Order()).IsEquivalentTo([TemplateOutcome.Built, TemplateOutcome.Reused]);
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  [Test]
  [Timeout(240_000)]
  public async Task AFailedBuild_LeavesNeitherTheTemplateNorItsBuildDatabaseAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var name = _newTemplateName();
    string? buildDatabase = null;
    try {
      Task failingMigration(WorkCoordinationDbContext context, CancellationToken ct) {
        buildDatabase = context.Database.GetDbConnection().Database;
        throw new InvalidOperationException("migration failed");
      }

      await Assert.That(async () => await EFCoreSchemaTemplate.EnsureTemplateAsync(
          name, "fp-failed", failingMigration, cancellationToken))
        .Throws<InvalidOperationException>();

      await Assert.That(buildDatabase).IsNotNull();
      await Assert.That(await _oidAsync(buildDatabase!, cancellationToken)).IsNull()
        .Because("a half-migrated build database is dropped, not left for the hourly sweep");
      await Assert.That(await _oidAsync(name, cancellationToken)).IsNull()
        .Because("only a finished build is renamed to the template's name");

      var outcome = await EFCoreSchemaTemplate.EnsureTemplateAsync(name, "fp-failed", _migrateAsync, cancellationToken);
      await Assert.That(outcome).IsEqualTo(TemplateOutcome.Built)
        .Because("a failure is not remembered: the next caller builds");
    } finally {
      await _dropTemplateAsync(name);
    }
  }

  [Test]
  [Timeout(120_000)]
  public async Task TheSweep_DropsOnlyTemplatesUnderThePrefixUnusedForTooLongAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var prefix = string.Create(CultureInfo.InvariantCulture, $"eftmpls{Guid.NewGuid():N}")[..20];
    var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    var old = $"{prefix}_old";
    var recent = $"{prefix}_recent";
    var kept = $"{prefix}_kept";
    var unreadable = $"{prefix}_unreadable";
    var notATemplate = $"{prefix}_plain";
    var all = new[] { old, recent, kept, unreadable, notATemplate };
    try {
      foreach (var name in all) {
        var scratch = await PerTestDatabaseFactory.CreateAsync("eftmplsweep", cancellationToken: cancellationToken);
        await _scalarAsync<object>($"ALTER DATABASE {scratch.Name} RENAME TO {name}", null, cancellationToken);
      }
      async Task stampAsync(string name, string comment, bool template) {
        await _scalarAsync<object>($"COMMENT ON DATABASE {name} IS '{comment}'", null, cancellationToken);
        await _scalarAsync<object>($"ALTER DATABASE {name} WITH IS_TEMPLATE {(template ? "true" : "false")}", null,
          cancellationToken);
      }
      await stampAsync(old, EFCoreSchemaTemplate.FormatStamp(now.AddDays(-2)), template: true);
      await stampAsync(recent, EFCoreSchemaTemplate.FormatStamp(now.AddHours(-1)), template: true);
      await stampAsync(kept, EFCoreSchemaTemplate.FormatStamp(now.AddDays(-30)), template: true);
      await stampAsync(unreadable, "not a stamp", template: true);
      await stampAsync(notATemplate, EFCoreSchemaTemplate.FormatStamp(now.AddDays(-30)), template: false);

      var swept = await EFCoreSchemaTemplate.SweepStaleTemplatesAsync(
        prefix, keep: kept, TimeSpan.FromDays(1), now, cancellationToken);

      await Assert.That(swept).IsEqualTo(1);
      await Assert.That(await _oidAsync(old, cancellationToken)).IsNull();
      await Assert.That((await _oidAsync(recent, cancellationToken)).HasValue).IsTrue()
        .Because("another worktree or process used it within the window");
      await Assert.That((await _oidAsync(kept, cancellationToken)).HasValue).IsTrue()
        .Because("the caller's own template is never swept, whatever its stamp");
      await Assert.That((await _oidAsync(unreadable, cancellationToken)).HasValue).IsTrue()
        .Because("a template whose age cannot be read was not stamped by this code, and is left alone");
      await Assert.That((await _oidAsync(notATemplate, cancellationToken)).HasValue).IsTrue()
        .Because("only templates are swept here; scratch databases have a sweep of their own");
    } finally {
      foreach (var name in all) {
        await _dropTemplateAsync(name);
      }
    }
  }

  /// <summary>
  /// Every test's copy has a service identity of its own, as a database migrated for that test alone did.
  /// </summary>
  /// <remarks>
  /// A migration seeds <c>wh_service_config</c> with a random <c>service_id</c>, and a copy inherits the template's.
  /// Two stores in one test (an origin and a consumer) that shared it read each other's events as their own.
  /// </remarks>
  [Test]
  [Timeout(240_000)]
  public async Task TwoCopies_EachHaveAServiceIdentityOfTheirOwnAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var first = await EFCoreSchemaTemplate.CreateDatabaseAsync("eftmpltid", cancellationToken);
    var second = await EFCoreSchemaTemplate.CreateDatabaseAsync("eftmpltid", cancellationToken);
    try {
      const string SERVICE_ID = "SELECT service_id FROM public.wh_service_config";
      var firstId = await _scalarAsync<Guid>(SERVICE_ID, null, cancellationToken, database: first.Name);
      var secondId = await _scalarAsync<Guid>(SERVICE_ID, null, cancellationToken, database: second.Name);

      await Assert.That(firstId).IsNotEqualTo(Guid.Empty);
      await Assert.That(firstId).IsNotEqualTo(secondId)
        .Because("each test database is its own service, as one migrated for the test was");
    } finally {
      await PerTestDatabaseFactory.DropAsync(first.Name, cancellationToken: CancellationToken.None);
      await PerTestDatabaseFactory.DropAsync(second.Name, cancellationToken: CancellationToken.None);
    }
  }

  /// <summary>
  /// What two migrations from scratch leave differently, beyond when they ran, is exactly what a copy gives a new
  /// value: anything else a migration seeds per database would be shared by every copy.
  /// </summary>
  /// <remarks>
  /// Derived by machine rather than listed by reading the migrations: two empty databases are migrated, and every
  /// table's rows are compared with their date and time columns left out. A new migration that seeds a random or
  /// per-database value fails here until <see cref="EFCoreSchemaTemplate.ReidentifyStatements"/> renews it.
  /// </remarks>
  [Test]
  [Timeout(300_000)]
  public async Task TwoMigrationsFromScratch_DifferOnlyInWhatACopyRenewsAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var first = await PerTestDatabaseFactory.CreateAsync("eftmpltscr", cancellationToken: cancellationToken);
    var second = await PerTestDatabaseFactory.CreateAsync("eftmpltscr", cancellationToken: cancellationToken);
    try {
      var firstColumns = await _migratedColumnsAsync(first, cancellationToken);
      var secondColumns = await _migratedColumnsAsync(second, cancellationToken);

      var differing = firstColumns.Keys.Union(secondColumns.Keys)
        .Where(column => firstColumns.GetValueOrDefault(column) != secondColumns.GetValueOrDefault(column))
        .Order()
        .ToList();
      var accountedFor = EFCoreSchemaTemplate.RenewedPerCopy.Concat(EFCoreSchemaTemplate.LocalRowKeys).Order().ToList();

      await Assert.That(differing).IsEquivalentTo(accountedFor)
        .Because("a value a migration seeds per database is renewed in every copy or is a key local to it, and the "
          + "lists name only what differs: " + string.Join(", ", differing.Select(c => $"{c.Table}.{c.Column}")));
    } finally {
      await PerTestDatabaseFactory.DropAsync(first.Name, cancellationToken: CancellationToken.None);
      await PerTestDatabaseFactory.DropAsync(second.Name, cancellationToken: CancellationToken.None);
    }
  }

  /// <summary>
  /// Migrates <paramref name="database"/> and reads each column's values, sorted, date and time columns left out.
  /// </summary>
  private static async Task<Dictionary<(string Table, string Column), string>> _migratedColumnsAsync(
      PerTestDatabase database, CancellationToken cancellationToken) {
    var connectionString = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(database.ConnectionString)) {
      Pooling = false,
    }.ConnectionString;
    var (dataSource, options) = EFCoreTestBase.CreateContextOptions(connectionString);
    await using (dataSource) {
      await using var context = new WorkCoordinationDbContext(options);
      await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    }

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var columns = new List<(string Table, string Column)>();
    await using (var list = new NpgsqlCommand("""
        SELECT c.table_name, c.column_name
        FROM information_schema.columns c
        JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
          AND c.data_type NOT IN ('timestamp with time zone', 'timestamp without time zone', 'date',
                                  'time with time zone', 'time without time zone')
        """, connection)) {
      await using var reader = await list.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        columns.Add((reader.GetString(0), reader.GetString(1)));
      }
    }

    var values = new Dictionary<(string Table, string Column), string>();
    foreach (var (table, column) in columns) {
      await using var read = new NpgsqlCommand(
        $"SELECT coalesce(string_agg(v, E'\\n' ORDER BY v), '') FROM (SELECT \"{column}\"::text AS v FROM public.\"{table}\") s",
        connection);
      values[(table, column)] = (string)(await read.ExecuteScalarAsync(cancellationToken))!;
    }
    return values;
  }

  [Test]
  public async Task AStamp_RoundTripsAndRejectsWhatItDidNotWriteAsync() {
    var when = new DateTimeOffset(2026, 11, 8, 12, 34, 56, TimeSpan.Zero);

    await Assert.That(EFCoreSchemaTemplate.TryReadStamp(EFCoreSchemaTemplate.FormatStamp(when), out var read)).IsTrue();
    await Assert.That(read).IsEqualTo(when);
    await Assert.That(EFCoreSchemaTemplate.TryReadStamp(null, out _)).IsFalse();
    await Assert.That(EFCoreSchemaTemplate.TryReadStamp("last used yesterday", out _)).IsFalse();
  }

  [Test]
  public async Task ATemplateName_ThatIsNotAPlainIdentifier_IsRefusedAsync() {
    await Assert.That(async () => await EFCoreSchemaTemplate.EnsureTemplateAsync(
        "eftmplt; DROP DATABASE postgres", "fp", _migrateAsync, CancellationToken.None))
      .Throws<ArgumentException>()
      .Because("the name is spliced into DDL, which takes no parameters");
  }

  [Test]
  [Timeout(240_000)]
  public async Task TheRunsTemplate_IsTheOneEveryBaseClassTestCopiesAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);

    var name = await EFCoreSchemaTemplate.EnsureAsync(cancellationToken);

    await Assert.That(name).IsEqualTo(EFCoreSchemaTemplate.TemplateName);
    await Assert.That(await _scalarAsync<bool>(
        "SELECT datistemplate FROM pg_database WHERE datname = @name", name, cancellationToken))
      .IsTrue();
  }

  /// <summary>A failure does not latch the gate; a success does.</summary>
  /// <remarks>
  /// A gate that remembered the failed attempt would hand every later test the same exception, or worse, a template
  /// name for a template that was never built.
  /// </remarks>
  [Test]
  public async Task TheProcessGate_RetriesAfterAFailureAndRemembersASuccessAsync() {
    var calls = 0;
    var gate = new TemplateGate(_ => {
      calls++;
      return calls == 1
        ? Task.FromException<string>(new InvalidOperationException("first build failed"))
        : Task.FromResult("eftmpl_ready");
    });

    await Assert.That(async () => await gate.GetAsync(CancellationToken.None)).Throws<InvalidOperationException>();
    await Assert.That(await gate.GetAsync(CancellationToken.None)).IsEqualTo("eftmpl_ready");
    await Assert.That(await gate.GetAsync(CancellationToken.None)).IsEqualTo("eftmpl_ready");
    await Assert.That(calls).IsEqualTo(2);
  }

  [Test]
  public async Task TheProcessGate_BuildsOnceForCallersArrivingTogetherAsync() {
    var calls = 0;
    var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var gate = new TemplateGate(_ => {
      Interlocked.Increment(ref calls);
      return release.Task;
    });

    var first = gate.GetAsync(CancellationToken.None);
    var second = gate.GetAsync(CancellationToken.None);
    release.SetResult("eftmpl_ready");

    await Assert.That(await Task.WhenAll(first, second)).IsEquivalentTo(["eftmpl_ready", "eftmpl_ready"]);
    await Assert.That(calls).IsEqualTo(1);
  }
}
