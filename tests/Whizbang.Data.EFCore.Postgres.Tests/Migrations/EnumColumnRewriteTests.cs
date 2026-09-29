using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// An enumeration in a physical column is stored as its underlying number. A column an earlier release created
/// as text, holding the enum's names, is converted in place by the stored-format rewrite phase: every member name
/// becomes its number, a value that is already a number is kept, and the column is retyped. Idempotent: a column
/// already numeric is left alone. A value that is neither stops startup with the table, the column and a sample of
/// what could not be converted, and the column is left exactly as it was.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#enum-text-columns</docs>
/// <tests>src/Whizbang.Data.Postgres/EnumColumnRewriteSql.cs</tests>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class EnumColumnRewriteTests : IAsyncDisposable {
  private const long LOCK_ID = 987654322;
  private const int TIMEOUT_SECONDS = 30;
  private static readonly (string Name, string Value)[] _stage = [("Draft", "0"), ("Open", "1"), ("Closed", "2")];

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("enumrewrite");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await _executeAsync("""
      CREATE TABLE wh_per_ticket (id int PRIMARY KEY, stage text, title text NOT NULL);
      INSERT INTO wh_per_ticket VALUES (1, 'Draft', 'a'), (2, 'Closed', 'b'), (3, '1', 'c'), (4, NULL, 'd'), (5, 'Open', 'e');
      """);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
    GC.SuppressFinalize(this);
  }

  private static (string Name, string Sql) _rewrite() =>
    ("enum-column:wh_per_ticket.stage",
      EnumColumnRewriteSql.Build("public", "wh_per_ticket", "stage", "Stage", "INTEGER", _stage));

  private Task<bool> _applyAsync() =>
    CanonicalTemporalRewritePhase.ApplyAsync(() => new NpgsqlConnection(_connectionString), LOCK_ID, [_rewrite()], TIMEOUT_SECONDS);

  [Test]
  public async Task ATextColumnOfNames_IsConvertedToNumbers_KeepingEveryRowAsync() {
    await _applyAsync();

    await Assert.That(await _scalarAsync(
      "SELECT data_type FROM information_schema.columns WHERE table_name = 'wh_per_ticket' AND column_name = 'stage'"))
      .IsEqualTo("integer");
    await Assert.That(await _scalarAsync("SELECT string_agg(coalesce(stage::text, 'null') || ':' || title, ',' ORDER BY id) FROM wh_per_ticket"))
      .IsEqualTo("0:a,2:b,1:c,null:d,1:e")
      .Because("Each name becomes its number, a value already numeric is kept, a null stays null, and nothing else changes.");
  }

  [Test]
  public async Task RunningItAgain_IsANoOpAsync() {
    await _applyAsync();
    await _executeAsync("INSERT INTO wh_per_ticket VALUES (6, 2, 'f')");

    await Assert.That(await _applyAsync()).IsTrue();

    await Assert.That(await _scalarAsync("SELECT string_agg(stage::text, ',' ORDER BY id) FROM wh_per_ticket WHERE stage IS NOT NULL"))
      .IsEqualTo("0,2,1,1,2")
      .Because("A column that is already numeric is left alone, and rows written since are untouched.");
  }

  [Test]
  public async Task AValueThatIsNeitherANameNorANumber_StopsStartup_NamingTheColumnAsync() {
    await _executeAsync("INSERT INTO wh_per_ticket VALUES (7, 'Archived', 'g'), (8, 'draft', 'h')");

    var failure = await Assert.That(_applyAsync).Throws<StoredFormConversionBlockedException>();

    await Assert.That(failure!.Message).Contains("wh_per_ticket");
    await Assert.That(failure.Message).Contains("stage");
    await Assert.That(failure.Message).Contains("Archived");
    await Assert.That(failure.Message).Contains("draft")
      .Because("Names match exactly, so a differently cased name is reported rather than guessed at.");
    await Assert.That(await _scalarAsync(
      "SELECT data_type FROM information_schema.columns WHERE table_name = 'wh_per_ticket' AND column_name = 'stage'"))
      .IsEqualTo("text")
      .Because("A conversion that cannot be completed changes nothing.");
  }

  [Test]
  public async Task AMissingColumn_IsANoOpAsync() {
    await _executeAsync("ALTER TABLE wh_per_ticket DROP COLUMN stage");

    await Assert.That(await _applyAsync()).IsTrue()
      .Because("A column the schema pass has not created yet has nothing to convert.");
  }

  [Test]
  public async Task Build_RefusesAnIdentifierOrValueThatIsNotPlainAsync() {
    await Assert.That(() => EnumColumnRewriteSql.Build("public", "t;drop", "c", "E", "INTEGER", _stage)).Throws<ArgumentException>();
    await Assert.That(() => EnumColumnRewriteSql.Build("public", "t", "c", "E", "INTEGER", [("A", "1; drop")])).Throws<ArgumentException>();
    await Assert.That(() => EnumColumnRewriteSql.Build("public", "t", "c", "E", "INTEGER", [("A'", "1")])).Throws<ArgumentException>();
    await Assert.That(() => EnumColumnRewriteSql.Build("public", "t", "c", "E", "INTEGER; x", _stage)).Throws<ArgumentException>();
  }

  [Test]
  public async Task BlockedException_StandardConstructors_CarryTheirMessageAsync() {
    var cause = new InvalidOperationException("cause");
    await Assert.That(new StoredFormConversionBlockedException().Failures).Count().IsEqualTo(1);
    await Assert.That(new StoredFormConversionBlockedException("m").Failures[0]).IsEqualTo("m");
    var wrapped = new StoredFormConversionBlockedException("w", cause);
    await Assert.That(wrapped.InnerException).IsSameReferenceAs(cause);
    await Assert.That(wrapped.Failures[0]).IsEqualTo("w");
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
}
