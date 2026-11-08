// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The declared-spelling pass against plain tables (#1874): it walks a table in bounded chunks and rewrites every
/// row whatever chunk it falls in, quotes names that are not plain identifiers, leaves a table that does not exist
/// alone, reports a table it cannot check without stopping, samples the conflicts it reports, and reports a required
/// member missing from stored rows with a bounded sample of the rows.
/// </summary>
/// <remarks>
/// The journal comes from its migration's embedded text against a scratch database; the tables are plain tables
/// with the columns a perspective table has. The pass is what is under test, not the initializer.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeySpellingPass.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeySpellingSql.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class DeclaredKeySpellingPassTests {
  private const int TIMEOUT_SECONDS = 30;
  private const string TABLE = "wh_per_shapes";

  private static readonly DeclaredKeyShape _point = DeclaredKeyShape.ForObject(
    new DeclaredMember("x", IsRequired: true), new DeclaredMember("y.z"), new DeclaredMember("w'q\"\\"));

  private static readonly DeclaredKeyTable _table = new(TABLE, [
    new DeclaredKeyColumn("data", DeclaredKeyShape.ForObject(new DeclaredMember("pts", Shape: DeclaredKeyShape.ForArray(_point)))),
    new DeclaredKeyColumn("metadata", DeclaredKeyShape.ForObject(new DeclaredMember("tag"))),
  ]);

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("declared_pass");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await _execAsync($"CREATE TABLE {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb NULL)");
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task EveryChunk_IsRewritten_AndNamesThatAreNotIdentifiers_MatchLiterallyAsync() {
    await _journalAsync();
    var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();
    foreach (var id in ids) {
      await _insertAsync(id, """{"pts":[{"X":1,"Y.Z":2,"W'Q\"\\":3}]}""", metadata: null);
    }
    var clean = Guid.CreateVersion7();
    await _insertAsync(clean, """{"pts":[{"x":1,"y.z":2,"yaz":9}]}""", """{"Tag":"a"}""");
    var log = new ListLogger();

    var result = await DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS, batchSize: 2, log, CancellationToken.None);
    await DeclaredKeySpellingPass.SettleAsync(_factory, "public", result, TIMEOUT_SECONDS, batchSize: 2, log, CancellationToken.None);

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE data @> '{{\"pts\":[{{\"x\":1,\"y.z\":2}}]}}'")).IsEqualTo("6")
      .Because("rows in every chunk of two are rewritten, and the dot in a declared name is not a wildcard");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE data -> 'pts' -> 0 ? 'w''q\"\\'")).IsEqualTo("5");
    await Assert.That(await _scalarAsync($"SELECT data -> 'pts' -> 0 ->> 'yaz' FROM {TABLE} WHERE id = '{clean}'")).IsEqualTo("9")
      .Because("a key the dot would have matched as a wildcard is someone else's");
    await Assert.That(await _scalarAsync($"SELECT metadata::text FROM {TABLE} WHERE id = '{clean}'")).IsEqualTo("{\"tag\": \"a\"}");
    await Assert.That(log.Entries.Single(e => e.Level == LogLevel.Warning).Message)
      .Contains("public.wh_per_shapes: 6 row(s) updated")
      .And.Contains("data $.pts[*]: X -> x in 5 row(s)")
      .And.Contains("metadata $: Tag -> tag in 1 row(s)");
    await Assert.That(await _scalarAsync("SELECT rows_converted::text || coalesce(settled_at::text, '-open') FROM wh_stored_form_migrations"))
      .IsEqualTo("6-open");
  }

  [Test]
  public async Task AColumnThatDeclaresNoMembers_IsNeverACandidate_AndTheOtherColumnsStillRewriteAsync() {
    var undeclared = _table with {
      Columns = [_table.Columns[0], new DeclaredKeyColumn("metadata", DeclaredKeyShape.ForObject())],
    };
    var id = Guid.CreateVersion7();
    await _insertAsync(id, """{"pts":[{"X":1}]}""", """{"Tag":"a"}""");

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [undeclared], TIMEOUT_SECONDS);

    await Assert.That(await _scalarAsync($"SELECT data::text FROM {TABLE} WHERE id = '{id}'")).IsEqualTo("{\"pts\": [{\"x\": 1}]}");
    await Assert.That(await _scalarAsync($"SELECT metadata::text FROM {TABLE} WHERE id = '{id}'")).IsEqualTo("{\"Tag\": \"a\"}")
      .Because("a column that declares no names has no spelling to move its keys to");
  }

  [Test]
  public async Task WithoutAJournal_TheRowsAreStillRewritten_AndNothingIsRecordedAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(id, """{"pts":[{"X":1}]}""", metadata: null);

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS);

    await Assert.That(await _scalarAsync($"SELECT data::text FROM {TABLE} WHERE id = '{id}'")).IsEqualTo("{\"pts\": [{\"x\": 1}]}");
    await Assert.That(await _scalarAsync("SELECT to_regclass('wh_stored_form_migrations') IS NULL")).IsEqualTo("True");
  }

  [Test]
  public async Task ATableThatDoesNotExist_IsLeftAloneAsync() {
    await _journalAsync();
    var log = new ListLogger();

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table with { Table = "wh_per_absent" }], TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Any(e => e.Message.Contains("public.wh_per_absent: table absent", StringComparison.Ordinal))).IsTrue();
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_stored_form_migrations")).IsEqualTo("0")
      .Because("nothing is recorded for a table there was nothing to check in");
  }

  [Test]
  public async Task ATableThatCannotBeChecked_IsReported_AndTheOthersStillRunAsync() {
    await _execAsync("CREATE TABLE wh_per_no_id (key uuid PRIMARY KEY, data jsonb NOT NULL)");
    var id = Guid.CreateVersion7();
    await _insertAsync(id, """{"pts":[{"X":1}]}""", metadata: null);
    var log = new ListLogger();

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public",
      [_table with { Table = "wh_per_no_id" }, _table], TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Single(e => e.Level == LogLevel.Warning && e.Message.Contains("wh_per_no_id", StringComparison.Ordinal)).Message)
      .Contains("next start tries again");
    await Assert.That(await _scalarAsync($"SELECT data::text FROM {TABLE} WHERE id = '{id}'")).IsEqualTo("{\"pts\": [{\"x\": 1}]}");
  }

  [Test]
  public async Task ATableDroppedBeforeItSettles_IsReported_NotThrownAsync() {
    await _journalAsync();
    var result = await DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS);
    await _execAsync($"DROP TABLE {TABLE}");
    var log = new ListLogger();

    await DeclaredKeySpellingPass.SettleAsync(_factory, "public", result, TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(TABLE, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ManyConflicts_AreSampled_AndAnAmbiguousKeyIsNamedAsSuchAsync() {
    for (var i = 0; i < 11; i++) {
      await _insertAsync(Guid.CreateVersion7(), $$"""{"pts":[{"x":1,"X":{{i + 2}}}]}""", metadata: null);
    }
    var ambiguous = new DeclaredKeyTable(TABLE, [new DeclaredKeyColumn("metadata",
      DeclaredKeyShape.ForObject(new DeclaredMember("ab"), new DeclaredMember("AB")))]);
    await _insertAsync(Guid.CreateVersion7(), """{"pts":[]}""", """{"Ab":1}""");

    var log = new ListLogger();
    var failure = await Assert.That(() => DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table, ambiguous], TIMEOUT_SECONDS, log))
      .Throws<StoredFormConversionBlockedException>();
    await Assert.That(log.Entries.Where(e => e.Level >= LogLevel.Warning)).IsEmpty();

    await Assert.That(failure!.Message).Contains("11 stored key(s) cannot be rewritten").And.Contains("; and 1 more")
      .And.Contains("holds \"Ab\", which matches more than one declared name (ab or AB)");
    await Assert.That(failure.Failures.Count).IsEqualTo(2).Because("one entry per table");
  }

  [Test]
  public async Task MissingRequiredMembers_AreReported_WithABoundedSample_AndTheTableIsSettledAsync() {
    await _journalAsync();
    for (var i = 0; i < 4; i++) {
      await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"y.z":1}]}""", metadata: null);
    }
    var log = new ListLogger();

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning)).IsEmpty();
    var error = log.Entries.Single(e => e.Level == LogLevel.Error).Message;
    await Assert.That(error).Contains("data $.pts[*].x in 4 row(s) (for example ").And.StartsWith("4 stored document(s) in ");
    await Assert.That(error.Split("for example ")[1].Split(')')[0].Split(", ").Length).IsEqualTo(3);
    await Assert.That(await _scalarAsync("SELECT coalesce(settled_at::text, 'open') FROM wh_stored_form_migrations")).IsNotEqualTo("open")
      .Because("#2128: rows nothing at startup can repair are reported once, not rescanned at every start");
  }

  [Test]
  public async Task ACanceledStart_IsNotSwallowedAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    var result = await DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS);

    await Assert.That(() => (Task)DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS, cancellationToken: canceled.Token))
      .Throws<OperationCanceledException>().Because("a stopping host is not a table that could not be checked");
    await Assert.That(() => DeclaredKeySpellingPass.SettleAsync(_factory, "public", result, TIMEOUT_SECONDS, cancellationToken: canceled.Token))
      .Throws<OperationCanceledException>();
  }

  // ─────────────────────────────────────────────────────────────────────────
  // #2128: progress at Information on a steady cadence, and unrepairable rows reported once.
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>A clock each reading of which is one cadence later than the last, so every check finds a line due.</summary>
  private sealed class OneCadencePerReading : TimeProvider {
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Add(ref _ticks, StartupProgress.Cadence.Ticks);
  }

  private static string[] _information(ListLogger log) =>
    [.. log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message)];

  /// <summary>
  /// Both scans say when they start, which table of how many they are on and how many rows they have read each time a
  /// line is due, and their totals when they finish; with a line due at every chunk, every chunk says so.
  /// </summary>
  [Test]
  public async Task BothScans_SayWhereTheyAre_AtInformation_EachTimeALineIsDueAsync() {
    await _journalAsync();
    for (var i = 0; i < 5; i++) {
      await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"x":1}]}""", metadata: null);
    }
    var log = new ListLogger();
    var clock = new OneCadencePerReading();

    var result = await DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS, batchSize: 2, clock, log, CancellationToken.None);
    await DeclaredKeySpellingPass.SettleAsync(_factory, "public", result, TIMEOUT_SECONDS, batchSize: 2, clock, log, CancellationToken.None);

    var lines = _information(log);
    string[] expected = [
      "The declared-key spelling scan of public started: 1 perspective table(s) to look at",
      $"The declared-key spelling scan of public: table 1 of 1 (public.{TABLE}), 2 row(s) read in it, 2 in all, ",
      $"The declared-key spelling scan of public: table 1 of 1 (public.{TABLE}), 4 row(s) read in it, 4 in all, ",
      $"The declared-key spelling scan of public: table 1 of 1 (public.{TABLE}), 5 row(s) read in it, 5 in all, ",
      "The declared-key spelling scan of public finished: 1 table(s) scanned, 5 row(s) read, 0 row(s) rewritten, in ",
      "The required-member check of public started: 1 perspective table(s) to look at",
      $"The required-member check of public: table 1 of 1 (public.{TABLE}), 2 row(s) read in it, 2 in all, ",
      $"The required-member check of public: table 1 of 1 (public.{TABLE}), 4 row(s) read in it, 4 in all, ",
      $"The required-member check of public: table 1 of 1 (public.{TABLE}), 5 row(s) read in it, 5 in all, ",
      "The required-member check of public finished: 1 table(s) checked, 5 row(s) read, 0 row(s) lacking a required member, in ",
    ];
    await Assert.That(lines.Length).IsEqualTo(expected.Length);
    for (var i = 0; i < expected.Length; i++) {
      await Assert.That(lines[i]).StartsWith(expected[i]);
    }
  }

  /// <summary>Before a cadence has passed, a scan says only that it started and that it finished.</summary>
  [Test]
  public async Task AScanShorterThanTheCadence_SaysOnlyItsFirstAndLastLinesAsync() {
    await _journalAsync();
    for (var i = 0; i < 5; i++) {
      await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"x":1}]}""", metadata: null);
    }
    var log = new ListLogger();
    var clock = new FakeTimeProvider();

    var result = await DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS, batchSize: 2, clock, log, CancellationToken.None);
    await DeclaredKeySpellingPass.SettleAsync(_factory, "public", result, TIMEOUT_SECONDS, batchSize: 2, clock, log, CancellationToken.None);

    await Assert.That(string.Join("\n", _information(log))).IsEqualTo(string.Join("\n",
      "The declared-key spelling scan of public started: 1 perspective table(s) to look at",
      "The declared-key spelling scan of public finished: 1 table(s) scanned, 5 row(s) read, 0 row(s) rewritten, in 0s",
      "The required-member check of public started: 1 perspective table(s) to look at",
      "The required-member check of public finished: 1 table(s) checked, 5 row(s) read, 0 row(s) lacking a required member, in 0s"));
  }

  /// <summary>A start on which every table is settled says nothing at Information.</summary>
  [Test]
  public async Task AStartWithEveryTableSettled_SaysNothingAsync() {
    await _journalAsync();
    await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"x":1}]}""", metadata: null);
    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, new FakeTimeProvider());
    var log = new ListLogger();

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, new FakeTimeProvider(), log);

    await Assert.That(_information(log)).IsEmpty();
  }

  /// <summary>
  /// Rows missing a required member, which nothing at startup can repair, are reported once with their count and the
  /// table, and the table is recorded as checked: the next start does not scan it, and does not say it again (#2128).
  /// </summary>
  [Test]
  public async Task UnrepairableRows_AreReportedOnce_AndTheirTableIsNotScannedAgainAsync() {
    await _journalAsync();
    for (var i = 0; i < 4; i++) {
      await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"y.z":1}]}""", metadata: null);
    }
    var first = new ListLogger();
    var second = new ListLogger();

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, first);
    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, second);

    var error = first.Entries.Single(e => e.Level == LogLevel.Error).Message;
    await Assert.That(error).StartsWith($"4 stored document(s) in public.{TABLE} lack a required member")
      .And.Contains("said once");
    await Assert.That(await _scalarAsync("SELECT coalesce(settled_at::text, 'open') FROM wh_stored_form_migrations")).IsNotEqualTo("open")
      .Because("rows a start cannot repair are no reason to scan the table at every start");
    await Assert.That(second.Entries.Where(e => e.Level >= LogLevel.Information)).IsEmpty()
      .Because("the rows were reported once; the second start skips the table with one lookup");
    await Assert.That(second.Entries).Contains(e => e.Message == $"public.{TABLE}: settled, skipped; every stored key is in its declared spelling");
  }

  /// <summary>
  /// A table whose keys were rewritten on this start stays open, so the next start scans it once more; that start
  /// checks for missing members, reports them once, and settles it.
  /// </summary>
  [Test]
  public async Task ATableRewrittenThisStart_ReportsItsMissingRowsOnTheNextStart_OnceAsync() {
    await _journalAsync();
    await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"X":1}]}""", metadata: null);
    await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"y.z":1}]}""", metadata: null);
    var starts = new[] { new ListLogger(), new ListLogger(), new ListLogger() };
    var journal = new List<string>();

    foreach (var log in starts) {
      await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, log);
      journal.Add(await _scalarAsync("SELECT CASE WHEN settled_at IS NULL THEN 'open' ELSE 'settled' END FROM wh_stored_form_migrations"));
    }

    await Assert.That(starts[0].Entries.Where(e => e.Level == LogLevel.Error)).IsEmpty()
      .Because("rows just rewritten are checked on the scan that follows, not twice");
    await Assert.That(starts[1].Entries.Count(e => e.Level == LogLevel.Error)).IsEqualTo(1);
    await Assert.That(starts[1].Entries.Single(e => e.Level == LogLevel.Error).Message).StartsWith($"1 stored document(s) in public.{TABLE}");
    await Assert.That(starts[2].Entries.Where(e => e.Level >= LogLevel.Information)).IsEmpty();
    await Assert.That(string.Join(",", journal)).IsEqualTo("open,settled,settled");
  }

  [Test]
  public async Task NoClock_IsRefusedAsync() {
    await Assert.That(() => (Task)DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS, (TimeProvider)null!))
      .Throws<ArgumentNullException>();
    var result = await DeclaredKeySpellingPass.RewriteAsync(_factory, "public", [_table], TIMEOUT_SECONDS);
    await Assert.That(() => DeclaredKeySpellingPass.SettleAsync(_factory, "public", result, TIMEOUT_SECONDS, (TimeProvider)null!))
      .Throws<ArgumentNullException>();
  }

  private NpgsqlConnection _factory() => new(_connectionString);

  private async Task _journalAsync() {
    var journal = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigration("176_StoredFormMigrations");
    await _execAsync(journal!.Sql);
  }

  private async Task _insertAsync(Guid id, string data, string? metadata) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand($"INSERT INTO {TABLE} (id, data, metadata) VALUES ($1, $2::jsonb, $3::jsonb)", db);
    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(data);
    command.Parameters.AddWithValue((object?)metadata ?? DBNull.Value);
    await command.ExecuteNonQueryAsync();
  }

  private async Task _execAsync(string sql) {
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

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add((logLevel, exception is null ? formatter(state, exception) : $"{formatter(state, exception)} | {exception.Message}"));
      }
    }
  }
}
