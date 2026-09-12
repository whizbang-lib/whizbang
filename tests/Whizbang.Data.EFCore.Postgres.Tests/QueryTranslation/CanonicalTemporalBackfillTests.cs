using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// That rows written before the canonical temporal format are rewritten into it correctly.
/// </summary>
/// <remarks>
/// <para>
/// A row written by an earlier release holds a rendering of the date; the mapping now writes a
/// number. A column holding both cannot be indexed and cannot be range-scanned correctly, so the
/// rewrite is not an optimization but the thing that makes the format change safe at all. It runs
/// with the schema, ahead of the index and ahead of the application serving traffic.
/// </para>
/// <para>
/// The statements under test are the ones the generator emits, called here rather than retyped, so a
/// change to either side cannot pass by being mirrored wrongly in the other.
/// </para>
/// <para>
/// Every expectation is the value <see cref="CanonicalTemporalFormat"/> itself produces. That is the
/// point: the row a backfill leaves behind has to be the row the writer would have written, or the
/// two halves of the column disagree in a way nothing detects.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class CanonicalTemporalBackfillTests : IAsyncDisposable {
  private const string TABLE = "wh_per_backfill";

  /// <summary>
  /// The properties as the generator would discover them, in the shapes that matter.
  /// </summary>
  private static readonly ImmutableArray<CanonicalTemporalProperty> _properties = [
    new("OccurredAt", CanonicalTemporalKind.Instant, false),
    new("RecordedAt", CanonicalTemporalKind.OffsetInstant, false),
    new("Day", CanonicalTemporalKind.Day, false),
    new("Clock", CanonicalTemporalKind.TimeOfDay, false),
    new("Elapsed", CanonicalTemporalKind.Duration, false),
    new("MaybeAt", CanonicalTemporalKind.Instant, true),
  ];

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"backfill_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    await _executeAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL
      );
      """);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
          $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
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

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    var value = await command.ExecuteScalarAsync();
    return value?.ToString() ?? "<null>";
  }

  /// <summary>Seeds one row holding the old rendering of every key.</summary>
  private async Task _seedOldFormatAsync(string label, string document) =>
    await _executeAsync(
      $"INSERT INTO {TABLE} (id, data) VALUES (gen_random_uuid(), '{document}'::jsonb)"
        .Replace("__LABEL__", label, StringComparison.Ordinal));

  /// <summary>Runs the backfill the generator emits.</summary>
  private async Task _backfillAsync() {
    foreach (var statement in CanonicalTemporalBackfillSql.Statements(_properties, TABLE)) {
      await _executeAsync(statement);
    }
  }

  private async Task<string> _valueAsync(string key, string label) =>
    await _scalarAsync($"SELECT data ->> '{key}' FROM {TABLE} WHERE data ->> 'Label' = '{label}'");

  private static string _micros(string iso) =>
    CanonicalTemporalFormat.ToEpochMicroseconds(
      DateTime.Parse(iso, CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
      .ToString(CultureInfo.InvariantCulture);

  /// <summary>
  /// An instant is rewritten to the number the writer would have written.
  /// </summary>
  [Test]
  [Arguments("2026-03-04T05:06:07Z")]
  [Arguments("2026-03-04T05:06:07.123456Z")]
  [Arguments("1969-12-31T23:59:59Z")]
  public async Task AnInstantIsRewrittenAsync(string rendering) {
    await _seedOldFormatAsync("a",
      $$"""{"Label": "a", "OccurredAt": "{{rendering}}"}""");

    await _backfillAsync();

    await Assert.That(await _valueAsync("OccurredAt", "a")).IsEqualTo(_micros(rendering))
      .Because("the row a backfill leaves behind has to be the row the writer would have written, "
        + "or the two halves of the column disagree and nothing detects it");
  }

  /// <summary>
  /// The extremes were stored as words rather than dates, and become the numbers the type maps to.
  /// </summary>
  /// <remarks>
  /// This is the case a naive conversion gets wrong loudly rather than quietly, which is the better
  /// half of the bargain: casting the word to a timestamp succeeds, and taking its epoch overflows
  /// the integer rather than storing something plausible.
  /// </remarks>
  [Test]
  [Arguments("infinity", 253402300799999999L)]
  [Arguments("-infinity", -62135596800000000L)]
  public async Task AnExtremeIsRewrittenToItsNumberAsync(string rendering, long expected) {
    await _seedOldFormatAsync("a",
      $$"""{"Label": "a", "OccurredAt": "{{rendering}}"}""");

    await _backfillAsync();

    await Assert.That(await _valueAsync("OccurredAt", "a"))
      .IsEqualTo(expected.ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>The extremes match what the writer produces for the type's own extremes.</summary>
  /// <remarks>
  /// Asserted against the format rather than against the literal above, so the two constants cannot
  /// drift. If the stored form ever changes, this fails alongside the format's own tests instead of
  /// leaving the backfill writing last release's number.
  /// </remarks>
  [Test]
  public async Task TheExtremeNumbersAreTheOnesTheWriterProducesAsync() {
    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(
      DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc))).IsEqualTo(253402300799999999L);
    await Assert.That(CanonicalTemporalFormat.ToEpochMicroseconds(
      DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc))).IsEqualTo(-62135596800000000L);
  }

  /// <summary>An offset is reduced to the instant it names, whatever offset it was written with.</summary>
  [Test]
  public async Task AnOffsetIsReducedToItsInstantAsync() {
    await _seedOldFormatAsync("a",
      """{"Label": "a", "RecordedAt": "2026-03-04T10:36:07+05:30"}""");

    await _backfillAsync();

    var expected = CanonicalTemporalFormat.ToEpochMicroseconds(
      new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

    await Assert.That(await _valueAsync("RecordedAt", "a"))
      .IsEqualTo(expected.ToString(CultureInfo.InvariantCulture))
      .Because("one instant has to have exactly one stored value, which is what makes a query for "
        + "it able to find every row that holds it");
  }

  /// <summary>A date becomes days since the epoch.</summary>
  [Test]
  public async Task ADateIsRewrittenAsync() {
    await _seedOldFormatAsync("a", """{"Label": "a", "Day": "2026-03-04"}""");

    await _backfillAsync();

    await Assert.That(await _valueAsync("Day", "a")).IsEqualTo(
      CanonicalTemporalFormat.ToEpochDays(new DateOnly(2026, 3, 4))
        .ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>
  /// A time of day becomes microseconds since midnight, including the seventh digit it was written
  /// with and which nothing could hold.
  /// </summary>
  [Test]
  public async Task ATimeOfDayIsRewrittenAsync() {
    await _seedOldFormatAsync("a", """{"Label": "a", "Clock": "05:06:07.1234567"}""");

    await _backfillAsync();

    await Assert.That(await _valueAsync("Clock", "a")).IsEqualTo(
      CanonicalTemporalFormat.ToMicrosecondsOfDay(new TimeOnly(5, 6, 7).Add(
        TimeSpan.FromTicks(1_234_567))).ToString(CultureInfo.InvariantCulture))
      .Because("this is the type the writer rendered with seven fractional digits while a date got "
        + "six, so it is the one whose truncation has to agree on both sides");
  }

  /// <summary>
  /// A duration becomes its tick count, through every shape its rendering took.
  /// </summary>
  /// <remarks>
  /// The awkward one, and the reason it was ruled out of the eligible set: a day count present only
  /// when non-zero, a fraction that is trimmed, and a sign that applies to the whole value rather
  /// than to any one component.
  /// </remarks>
  [Test]
  [Arguments("00:01:00", 0, 0, 1, 0, 0)]
  [Arguments("1.05:06:07.1234567", 1, 5, 6, 7, 1_234_567)]
  [Arguments("00:00:30.5", 0, 0, 0, 30, 5_000_000)]
  [Arguments("10.00:00:00", 10, 0, 0, 0, 0)]
  public async Task ADurationIsRewrittenAsync(
    string rendering, int days, int hours, int minutes, int seconds, int ticks) {
    await _seedOldFormatAsync("a", $$"""{"Label": "a", "Elapsed": "{{rendering}}"}""");

    await _backfillAsync();

    var expected = new TimeSpan(days, hours, minutes, seconds).Add(TimeSpan.FromTicks(ticks));

    await Assert.That(await _valueAsync("Elapsed", "a")).IsEqualTo(
      CanonicalTemporalFormat.ToTicks(expected).ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>A negative duration keeps its sign across every component.</summary>
  [Test]
  public async Task ANegativeDurationKeepsItsSignAsync() {
    await _seedOldFormatAsync("a", """{"Label": "a", "Elapsed": "-1.05:06:07.1234567"}""");

    await _backfillAsync();

    var expected = new TimeSpan(1, 5, 6, 7).Add(TimeSpan.FromTicks(1_234_567)).Negate();

    await Assert.That(await _valueAsync("Elapsed", "a")).IsEqualTo(
      CanonicalTemporalFormat.ToTicks(expected).ToString(CultureInfo.InvariantCulture))
      .Because("the sign applies to the whole duration rather than to its first component, which a "
        + "conversion that negated only the days would get wrong for everything but a whole number "
        + "of days");
  }

  /// <summary>An absent optional value is left absent rather than invented.</summary>
  [Test]
  public async Task AnAbsentValueIsLeftAloneAsync() {
    await _seedOldFormatAsync("a", """{"Label": "a", "OccurredAt": "2026-03-04T05:06:07Z"}""");

    await _backfillAsync();

    await Assert.That(await _scalarAsync(
      $"SELECT coalesce(jsonb_typeof(data -> 'MaybeAt'), 'missing') FROM {TABLE}"))
      .IsEqualTo("missing")
      .Because("a key that was never written is not a value to convert, and inventing one would "
        + "turn an absent date into the epoch");
  }

  /// <summary>
  /// Running it twice changes nothing, which is what makes a restored backup and a re-run safe.
  /// </summary>
  /// <remarks>
  /// The guard is the stored type rather than a marker column or a version row: a value that is
  /// already a number is not a string, so it is not selected. That makes the statement describe its
  /// own precondition instead of depending on bookkeeping that a restore could contradict.
  /// </remarks>
  [Test]
  public async Task RunningItTwiceChangesNothingAsync() {
    await _seedOldFormatAsync("a", """
      {"Label": "a", "OccurredAt": "2026-03-04T05:06:07Z", "Day": "2026-03-04",
       "Clock": "05:06:07.1234567", "Elapsed": "1.05:06:07.1234567",
       "RecordedAt": "2026-03-04T10:36:07+05:30"}
      """);

    await _backfillAsync();
    var afterFirst = await _scalarAsync($"SELECT data::text FROM {TABLE}");

    await _backfillAsync();
    var afterSecond = await _scalarAsync($"SELECT data::text FROM {TABLE}");

    await Assert.That(afterSecond).IsEqualTo(afterFirst);
  }

  /// <summary>
  /// A row already in the new format is untouched, which is the case a new installation is entirely
  /// made of.
  /// </summary>
  /// <remarks>
  /// This is what lets the statements ship in the ordinary schema path rather than behind a version
  /// gate. A database created by this release has nothing to convert, so the backfill is a no-op it
  /// pays for once at startup rather than a migration it has to be steered around.
  /// </remarks>
  [Test]
  public async Task ARowAlreadyInTheNewFormatIsUntouchedAsync() {
    var written = CanonicalTemporalFormat.ToEpochMicroseconds(
      new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    await _seedOldFormatAsync("a", $$"""{"Label": "a", "OccurredAt": {{written}}}""");

    await _backfillAsync();

    await Assert.That(await _valueAsync("OccurredAt", "a"))
      .IsEqualTo(written.ToString(CultureInfo.InvariantCulture));
  }
}
