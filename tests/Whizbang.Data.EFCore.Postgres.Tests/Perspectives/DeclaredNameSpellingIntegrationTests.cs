// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1874 on the Entity Framework driver: rows an earlier release stored under C# names that differ from the
/// declared <c>[JsonPropertyName]</c> only in case are rewritten to the declared spelling by the startup pass, at any
/// depth and inside collections of records, and then read back with their real values on both the mapped and the
/// opaque path. A row holding both spellings with different values stops startup, naming it. A start that finds
/// nothing to change writes no row. A required member missing from a stored row is reported, not read as zero.
/// </summary>
/// <remarks>
/// Each test starts the generated context once to create its tables, writes rows the way the earlier release did
/// (straight into the table, C# names), and starts it again: what an upgrade does.
/// </remarks>
/// <docs>operations/upgrading/declared-json-names</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Perspectives/DeclaredKeySpellingPass.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/DeclaredKeyShapes.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class DeclaredNameSpellingIntegrationTests {
  private const string PICTURE = "wh_per_declared_picture";
  private const string SKETCH = "wh_per_declared_sketch";
  private const string EMPTY = "{}";

  private const string OLD_PICTURE =
    """{"Id":"__ID__","Title":"sketch","Objects":[{"Color":"red","Points":[{"X":327,"Y":362},{"X":330.7060738825059,"Y":362}]},{"Color":"blue","Points":[{"X":1.5,"Y":-2}]}]}""";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("declared_names");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await _startAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  /// <summary>
  /// The consumer's case: points in strokes in a picture, stored as <c>X</c>/<c>Y</c>, declared <c>x</c>/<c>y</c>.
  /// Before, every point read as 0,0 and the row loaded "successfully".
  /// </summary>
  [Test]
  public async Task RowsStoredWithCSharpNames_AreRewrittenOnStartup_AndReadBackWithTheirValuesAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(PICTURE, id, OLD_PICTURE.Replace("__ID__", id.ToString(), StringComparison.Ordinal));
    var log = new ListLogger();

    await _startAsync(log);

    var stored = JsonDocument.Parse(await _scalarAsync($"SELECT data::text FROM {PICTURE} WHERE id = '{id}'")).RootElement;
    await Assert.That(stored.TryGetProperty("title", out _)).IsTrue();
    await Assert.That(stored.TryGetProperty("Title", out _)).IsFalse();
    var point = stored.GetProperty("Objects")[0].GetProperty("Points")[1];
    await Assert.That(point.GetProperty("x").GetDouble()).IsEqualTo(330.7060738825059d)
      .Because("the value moves under the declared key exactly as stored");
    await Assert.That(point.TryGetProperty("X", out _)).IsFalse();
    await Assert.That(stored.GetProperty("Objects")[1].GetProperty("color").GetString()).IsEqualTo("blue");

    await using var context = _context();
    var read = await new EFCorePostgresPerspectiveStore<DeclaredPicture.Model>(context, PICTURE).GetByStreamIdAsync(id);
    await Assert.That(read).IsNotNull();
    await Assert.That(read!.Title).IsEqualTo("sketch");
    await Assert.That(read.Objects[0].Color).IsEqualTo("red");
    await Assert.That(read.Objects[0].Points[0].X).IsEqualTo(327d)
      .Because("the mapped reader matches the declared name exactly, and the row now carries it");
    await Assert.That(read.Objects[0].Points[0].Y).IsEqualTo(362d);
    await Assert.That(read.Objects[1].Points[0].Y).IsEqualTo(-2d);

    var line = log.Entries.FirstOrDefault(e => e.Level == LogLevel.Warning
      && e.Message.Contains("declared spelling", StringComparison.Ordinal) && e.Message.Contains(PICTURE, StringComparison.Ordinal));
    await Assert.That(line.Message).IsNotNull()
      .Because("a rewrite of stored data says what it changed, once per table");
    await Assert.That(line.Message).Contains("1 row(s)")
      .And.Contains("$.Objects[*].Points[*]: X -> x")
      .And.Contains("$.Objects[*]: Color -> color")
      .And.Contains("$: Title -> title");
  }

  [Test]
  public async Task AnOpaqueRowStoredWithCSharpNames_IsRewrittenAndReadBackAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(SKETCH, id, OLD_PICTURE.Replace("__ID__", id.ToString(), StringComparison.Ordinal));

    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT data #>> '{{Objects,0,Points,0,x}}' FROM {SKETCH} WHERE id = '{id}'"))
      .IsEqualTo("327");
    await using var context = _context();
    var read = await new EFCorePostgresPerspectiveStore<DeclaredSketch.Model>(context, SKETCH).GetByStreamIdAsync(id);
    await Assert.That(read!.Objects[0].Points[1].X).IsEqualTo(330.7060738825059d);
    await Assert.That(read.Objects[1].Color).IsEqualTo("blue");
  }

  /// <summary>
  /// Both spellings with different values: which one is the real value is not something the pass can decide, so
  /// startup stops naming the table, the row, the path and both keys, and the row is left exactly as it was. The other
  /// rows are still rewritten.
  /// </summary>
  [Test]
  public async Task ARowHoldingBothSpellings_StopsStartup_NamingItAsync() {
    var conflicting = Guid.CreateVersion7();
    var plain = Guid.CreateVersion7();
    const string BOTH = """{"title":"t","Objects":[{"color":"red","Points":[{"x":1,"y":2},{"x":5,"X":6,"y":7}]}]}""";
    await _insertAsync(PICTURE, conflicting, BOTH);
    await _insertAsync(PICTURE, plain, OLD_PICTURE.Replace("__ID__", plain.ToString(), StringComparison.Ordinal));

    var failure = await Assert.That(() => _startAsync()).Throws<StoredFormConversionBlockedException>();

    await Assert.That(failure!.Message).Contains($"public.{PICTURE}.data")
      .And.Contains(conflicting.ToString())
      .And.Contains("$.Objects[0].Points[1]")
      .And.Contains("\"X\"")
      .And.Contains("\"x\"");
    await Assert.That(JsonDocument.Parse(await _scalarAsync($"SELECT data::text FROM {PICTURE} WHERE id = '{conflicting}'"))
        .RootElement.GetProperty("Objects")[0].GetProperty("Points")[1].GetProperty("X").GetDouble())
      .IsEqualTo(6d).Because("a row that cannot be rewritten is not touched");
    await Assert.That(await _scalarAsync($"SELECT data #>> '{{Objects,0,Points,0,x}}' FROM {PICTURE} WHERE id = '{plain}'"))
      .IsEqualTo("327").Because("one row that cannot be rewritten does not hold back the rows that can");
  }

  /// <summary>Both spellings holding the same value are one value: the old key is dropped and startup continues.</summary>
  [Test]
  public async Task BothSpellingsWithTheSameValue_MergeAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(PICTURE, id, """{"title":"t","Objects":[{"color":"red","Points":[{"x":5,"X":5,"y":7}]}]}""");

    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT data #> '{{Objects,0,Points,0}}' FROM {PICTURE} WHERE id = '{id}'"))
      .IsEqualTo("""{"x": 5, "y": 7}""");
  }

  /// <summary>
  /// The second start after a rewrite changes no row (each row's xmin is the one the first start left), records the
  /// table settled, and a third start does not scan it at all.
  /// </summary>
  [Test]
  public async Task ASecondStart_ChangesNoRow_AndSettlesTheTableAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(PICTURE, id, OLD_PICTURE.Replace("__ID__", id.ToString(), StringComparison.Ordinal));
    await _startAsync();
    var afterRewrite = await _scalarAsync($"SELECT xmin::text FROM {PICTURE} WHERE id = '{id}'");
    await Assert.That(await _journalAsync(PICTURE)).IsEqualTo("open")
      .Because("a pass that changed rows runs once more, catching rows an older instance wrote during a rollout");

    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT xmin::text FROM {PICTURE} WHERE id = '{id}'")).IsEqualTo(afterRewrite)
      .Because("nothing differs, so no row is updated");
    await Assert.That(await _journalAsync(PICTURE)).IsEqualTo("settled");

    var log = new ListLogger();
    await _startAsync(log);
    await Assert.That(await _scalarAsync($"SELECT xmin::text FROM {PICTURE} WHERE id = '{id}'")).IsEqualTo(afterRewrite);
    await Assert.That(log.Entries.Any(e => e.Message.Contains("declared spelling", StringComparison.Ordinal)
        && e.Message.Contains($"{PICTURE}: settled, skipped", StringComparison.Ordinal))).IsTrue()
      .Because("a settled table is skipped with one lookup");
  }

  /// <summary>Rows already in the declared spelling are never updated: the first start finds nothing and settles.</summary>
  [Test]
  public async Task RowsAlreadyInTheDeclaredSpelling_AreNeverUpdatedAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(PICTURE, id, """{"title":"t","Objects":[{"color":"red","Points":[{"x":1,"y":2}]}]}""");
    var before = await _scalarAsync($"SELECT xmin::text FROM {PICTURE} WHERE id = '{id}'");

    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT xmin::text FROM {PICTURE} WHERE id = '{id}'")).IsEqualTo(before);
    await Assert.That(await _journalAsync(PICTURE)).IsEqualTo("settled");
  }

  /// <summary>
  /// A required member missing from a stored row. The mapped reader cannot refuse it (Entity Framework reads a
  /// missing key as the default), so the startup pass reports it at Error, naming the table, the path and the row
  /// count, and leaves the table unsettled so every start says so until the rows are fixed.
  /// </summary>
  [Test]
  public async Task ARequiredMemberMissingFromAMappedRow_IsReportedAtErrorAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(PICTURE, id, """{"title":"t","Objects":[{"color":"red","Points":[{"x":1,"y":2},{"x":3}]}]}""");
    var log = new ListLogger();

    await _startAsync(log);

    var error = log.Entries.FirstOrDefault(e => e.Level == LogLevel.Error && e.Message.Contains(PICTURE, StringComparison.Ordinal));
    await Assert.That(error.Message).IsNotNull();
    await Assert.That(error.Message).Contains("$.Objects[*].Points[*].y").And.Contains("1 row(s)");
    await Assert.That(await _journalAsync(PICTURE)).IsEqualTo("open");
  }

  /// <summary>On the opaque path the reader itself refuses the row, with a <see cref="JsonException"/>.</summary>
  [Test]
  public async Task ARequiredMemberMissingFromAnOpaqueRow_FailsTheReadAsync() {
    var id = Guid.CreateVersion7();
    await _insertAsync(SKETCH, id, """{"title":"t","Objects":[{"color":"red","Points":[{"x":3}]}]}""");
    await _startAsync();

    await using var context = _context();
    var store = new EFCorePostgresPerspectiveStore<DeclaredSketch.Model>(context, SKETCH);
    Exception? failure = null;
    try {
      await store.GetByStreamIdAsync(id);
    } catch (Exception ex) {
      failure = ex;
    }

    await Assert.That(failure).IsNotNull().Because("a missing required member is not read as zero");
    await Assert.That(_chain(failure!).OfType<JsonException>().Any()).IsTrue()
      .Because("the refusal is a JsonException, which the worker classifies as a stored document it cannot read");
  }

  private static IEnumerable<Exception> _chain(Exception failure) {
    for (var current = failure; current is not null; current = current.InnerException) {
      yield return current;
    }
  }

  private DeclaredNamesDbContext _context() {
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(DeclaredNamesDbContext), new PostgresUpsertStrategy());
    return new DeclaredNamesDbContext(new DbContextOptionsBuilder<DeclaredNamesDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
  }

  private async Task _startAsync(ILogger? logger = null) {
    await using var context = _context();
    await context.EnsureWhizbangDatabaseInitializedAsync(logger);
  }

  private Task _insertAsync(string table, Guid id, string data) => _execAsync($"""
    INSERT INTO {table} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
    VALUES ('{id}', '{data}', '{EMPTY}', '{EMPTY}', now(), now(), now(), now(), 1)
    """);

  private Task<string> _journalAsync(string table) => _scalarAsync($"""
    SELECT coalesce(string_agg(CASE WHEN settled_at IS NULL THEN 'open' ELSE 'settled' END, ','), 'none')
    FROM wh_stored_form_migrations WHERE name LIKE 'declared-names:{table}:%'
    """);

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
        Entries.Add((logLevel, formatter(state, exception)));
      }
    }
  }
}
