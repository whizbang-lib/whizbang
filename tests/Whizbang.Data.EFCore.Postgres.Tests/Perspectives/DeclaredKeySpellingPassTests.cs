// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
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
[NotInParallel("EFCorePostgresTests")]
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
  public async Task MissingRequiredMembers_AreReported_WithABoundedSample_AndTheTableStaysOpenAsync() {
    await _journalAsync();
    for (var i = 0; i < 4; i++) {
      await _insertAsync(Guid.CreateVersion7(), """{"pts":[{"y.z":1}]}""", metadata: null);
    }
    var log = new ListLogger();

    await DeclaredKeySpellingPass.ApplyAsync(_factory, "public", [_table], TIMEOUT_SECONDS, log);

    await Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning)).IsEmpty();
    var error = log.Entries.Single(e => e.Level == LogLevel.Error).Message;
    await Assert.That(error).Contains("data $.pts[*].x in 4 row(s) (for example ");
    await Assert.That(error.Split("for example ")[1].Split(')')[0].Split(", ").Length).IsEqualTo(3);
    await Assert.That(await _scalarAsync("SELECT coalesce(settled_at::text, 'open') FROM wh_stored_form_migrations")).IsEqualTo("open");
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
