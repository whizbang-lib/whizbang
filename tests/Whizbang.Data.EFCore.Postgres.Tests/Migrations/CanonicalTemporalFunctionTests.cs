using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The SQL function that rewrites one temporal path of a document into the canonical form, and the
/// ledger that records which form a table is in.
/// </summary>
/// <remarks>
/// <para>
/// The rewrite used to be one statement per top-level key, which could not reach a temporal inside
/// a nested object or a collection element, and which converted a rendering only: a number was
/// assumed to be in the unit the writer used. The unit changed. A day count and a microsecond count
/// are both integers, so the ledger says which unit a table is in and the function converts from
/// that unit; nothing about a value is inspected to decide.
/// </para>
/// <para>
/// The migration is applied here from its embedded text, schema-substituted the way the runtime
/// applies it, against a scratch database: the function is what is under test, not the initializer.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Integration")]
[Category("Shard1")]
public class CanonicalTemporalFunctionTests : IAsyncDisposable {
  private const string MIGRATION = "153_PerspectiveForms";

  // The kinds as the function takes them; pinned to the enum in StoredTemporalKindTests.
  private const short INSTANT = 0;
  private const short OFFSET_INSTANT = 1;
  private const short DAY = 2;
  private const short TIME_OF_DAY = 3;
  private const short DURATION = 4;

  private const short FORM_MIXED_UNITS = 1;
  private const short FORM_MICROSECONDS = 2;

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"forms_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      // Deliberately not UTC: a rendering with no zone must still be read as UTC by the function
      // itself, not by the session it happens to run in.
      Timezone = "America/New_York",
    }.ConnectionString;

    var migration = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public")
      .GetMigration(MIGRATION);
    await Assert.That(migration).IsNotNull().Because($"migration {MIGRATION} must be embedded");
    await _executeAsync(migration!.Sql);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }
    GC.SuppressFinalize(this);
  }

  private async Task _executeAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>Applies the function to a document and returns the result as text.</summary>
  private async Task<string> _canonicalizeAsync(string document, string[] path, short kind, short fromForm) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT public.wh_canonicalize_temporal($1::jsonb, $2, $3, $4)::text", db);
    command.Parameters.AddWithValue(document);
    command.Parameters.AddWithValue(path);
    command.Parameters.AddWithValue(kind);
    command.Parameters.AddWithValue(fromForm);
    return (string)(await command.ExecuteScalarAsync())!;
  }

  private static string _micros(DateTime utc) =>
    CanonicalTemporalFormat.ToEpochMicroseconds(utc).ToString(CultureInfo.InvariantCulture);

  /// <summary>The ledger exists with the columns the rewrite reads and writes.</summary>
  [Test]
  public async Task TheLedgerExistsAsync() {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY ordinal_position) "
      + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'wh_perspective_forms'", db);

    await Assert.That((string?)await command.ExecuteScalarAsync())
      .IsEqualTo("table_name:text,temporal_form:smallint,applied_at:timestamp with time zone,settled_at:timestamp with time zone");
  }

  /// <summary>A rendering at the top level becomes the canonical number, for every kind.</summary>
  [Test]
  public async Task ARenderingAtTheTopLevelBecomesTheNumberAsync() {
    var at = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    await Assert.That(await _canonicalizeAsync("""{"At":"2026-03-04T05:06:07Z"}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"At": {{_micros(at)}}}""");
    await Assert.That(await _canonicalizeAsync("""{"At":"2026-03-04T05:06:07"}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"At": {{_micros(at)}}}""")
      .Because("a rendering with no zone is UTC, whatever zone the session runs in");
    await Assert.That(await _canonicalizeAsync("""{"At":"2026-03-04T10:36:07+05:30"}""", ["At"], OFFSET_INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"At": {{_micros(at)}}}""")
      .Because("an offset reduces to the instant it names");
    await Assert.That(await _canonicalizeAsync("""{"Day":"2026-03-04"}""", ["Day"], DAY, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"Day": {{CanonicalTemporalFormat.ToEpochMicroseconds(new DateOnly(2026, 3, 4))}}}""");
    await Assert.That(await _canonicalizeAsync("""{"Clock":"05:06:07.1234567"}""", ["Clock"], TIME_OF_DAY, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"Clock": {{CanonicalTemporalFormat.ToMicrosecondsOfDay(new TimeOnly(5, 6, 7).Add(TimeSpan.FromTicks(1_234_567)))}}}""")
      .Because("the seventh digit truncates, as the writer truncates it");
    await Assert.That(await _canonicalizeAsync("""{"Elapsed":"1.05:06:07.1234567"}""", ["Elapsed"], DURATION, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"Elapsed": {{CanonicalTemporalFormat.ToMicroseconds(new TimeSpan(1, 5, 6, 7).Add(TimeSpan.FromTicks(1_234_567)))}}}""");
    await Assert.That(await _canonicalizeAsync("""{"Elapsed":"-00:00:30.5"}""", ["Elapsed"], DURATION, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"Elapsed": {{CanonicalTemporalFormat.ToMicroseconds(TimeSpan.FromSeconds(-30.5))}}}""")
      .Because("the sign applies to the whole duration");
  }

  /// <summary>The words the database uses for its extremes become the sentinels the reader knows.</summary>
  [Test]
  public async Task TheRenderedExtremesBecomeTheSentinelsAsync() {
    var max = CanonicalTemporalFormat.ToEpochMicroseconds(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    var min = CanonicalTemporalFormat.ToEpochMicroseconds(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));

    await Assert.That(await _canonicalizeAsync("""{"At":"infinity"}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"At": {{max}}}""");
    await Assert.That(await _canonicalizeAsync("""{"At":"-infinity"}""", ["At"], OFFSET_INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"At": {{min}}}""");
  }

  /// <summary>A path through a nested object reaches the value and leaves its siblings alone.</summary>
  [Test]
  public async Task ANestedPathReachesTheValueAsync() {
    var result = await _canonicalizeAsync(
      """{"Label":"x","Window":{"Opens":"09:30:00","Kept":"as is"}}""", ["Window", "Opens"], TIME_OF_DAY, FORM_MIXED_UNITS);

    var opens = CanonicalTemporalFormat.ToMicrosecondsOfDay(new TimeOnly(9, 30)).ToString(CultureInfo.InvariantCulture);
    await Assert.That(result).IsEqualTo(
      "{\"Label\": \"x\", \"Window\": {\"Kept\": \"as is\", \"Opens\": " + opens + "}}");
  }

  /// <summary>A path through a collection converts every element, in order, and nothing else.</summary>
  [Test]
  public async Task ACollectionPathConvertsEveryElementAsync() {
    var first = new DateTime(2026, 3, 4, 1, 0, 0, DateTimeKind.Utc);
    var second = new DateTime(2026, 3, 4, 2, 0, 0, DateTimeKind.Utc);
    var result = await _canonicalizeAsync(
      """{"Turns":[{"At":"2026-03-04T01:00:00Z","Text":"a"},{"At":"2026-03-04T02:00:00Z","Text":"b"}]}""",
      ["Turns", "[]", "At"], INSTANT, FORM_MIXED_UNITS);

    await Assert.That(result).IsEqualTo(
      $$"""{"Turns": [{"At": {{_micros(first)}}, "Text": "a"}, {"At": {{_micros(second)}}, "Text": "b"}]}""");
  }

  /// <summary>An empty collection, a missing key, a null and a value of another type are left alone.</summary>
  [Test]
  public async Task WhatIsNotThereIsLeftAloneAsync() {
    await Assert.That(await _canonicalizeAsync("""{"Turns":[]}""", ["Turns", "[]", "At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo("""{"Turns": []}""");
    await Assert.That(await _canonicalizeAsync("""{"Label":"x"}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo("""{"Label": "x"}""");
    await Assert.That(await _canonicalizeAsync("""{"At":null}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo("""{"At": null}""");
    await Assert.That(await _canonicalizeAsync("""{"At":true}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo("""{"At": true}""")
      .Because("a value the reader will refuse is left for the reader to refuse, loudly, rather than replaced");
    await Assert.That(await _canonicalizeAsync("""{"At":"not a date"}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo("""{"At": "not a date"}""")
      .Because("an unrecognized rendering is a thing to look at, not a thing to write over");
  }

  /// <summary>
  /// A number in the mixed-unit form converts its unit for a day and a duration, and nothing else.
  /// </summary>
  /// <remarks>
  /// A day count and a tick count are both integers; only the ledger says which a number is. From
  /// the mixed-unit form a day multiplies up and a duration divides down; an instant was always in
  /// microseconds and does not move.
  /// </remarks>
  [Test]
  public async Task ANumberInTheMixedUnitFormConvertsItsUnitAsync() {
    await Assert.That(await _canonicalizeAsync("""{"Day":20516}""", ["Day"], DAY, FORM_MIXED_UNITS))
      .IsEqualTo($$"""{"Day": {{20516L * CanonicalTemporalFormat.MICROSECONDS_PER_DAY}}}""");
    await Assert.That(await _canonicalizeAsync("""{"Elapsed":1800000000}""", ["Elapsed"], DURATION, FORM_MIXED_UNITS))
      .IsEqualTo("""{"Elapsed": 180000000}""")
      .Because("ticks divide down to microseconds, truncating toward zero as the writer does");
    await Assert.That(await _canonicalizeAsync("""{"Elapsed":-1234567}""", ["Elapsed"], DURATION, FORM_MIXED_UNITS))
      .IsEqualTo("""{"Elapsed": -123456}""");
    await Assert.That(await _canonicalizeAsync("""{"At":1772600767000000}""", ["At"], INSTANT, FORM_MIXED_UNITS))
      .IsEqualTo("""{"At": 1772600767000000}""")
      .Because("an instant was microseconds in every form");
  }

  /// <summary>A number in the microsecond form is never touched, whatever its kind.</summary>
  [Test]
  public async Task ANumberInTheMicrosecondFormIsNeverTouchedAsync() {
    await Assert.That(await _canonicalizeAsync("""{"Day":20516}""", ["Day"], DAY, FORM_MICROSECONDS))
      .IsEqualTo("""{"Day": 20516}""")
      .Because("running twice must change nothing, and the form is what says the first run happened");
    await Assert.That(await _canonicalizeAsync("""{"Elapsed":180000000}""", ["Elapsed"], DURATION, FORM_MICROSECONDS))
      .IsEqualTo("""{"Elapsed": 180000000}""");
  }

  /// <summary>Running the function over its own output changes nothing.</summary>
  [Test]
  public async Task TheFunctionIsIdempotentOverItsOwnOutputAsync() {
    var once = await _canonicalizeAsync(
      """{"Turns":[{"At":"2026-03-04T01:00:00Z"}],"Day":"2026-03-04"}""", ["Turns", "[]", "At"], INSTANT, FORM_MIXED_UNITS);
    var twice = await _canonicalizeAsync(once, ["Turns", "[]", "At"], INSTANT, FORM_MICROSECONDS);

    await Assert.That(twice).IsEqualTo(once);
  }
}
