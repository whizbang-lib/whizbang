using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Locks the text a date lands in a perspective document as, and locks what PostgreSQL can be asked
/// to produce for the same value.
/// </summary>
/// <remarks>
/// <para>
/// Containment compares documents, and a date is stored as a JSON string. Numbers in jsonb compare by
/// value, so a number written with different trailing digits still matches; strings do not, so a date
/// filter can only be compiled into containment if the query side reproduces the stored text exactly,
/// character for character. That makes the format a contract rather than an implementation detail,
/// and a contract is worth pinning: nothing here is expected to change, and if it ever does, the
/// eligibility decision built on top of it has to be revisited rather than silently broken.
/// </para>
/// <para>
/// Every expectation below was produced by writing a row through the real mapping and reading back
/// what landed, not by reasoning about what ought to land. The assertions are exact strings on
/// purpose; a looser assertion would pass while the thing it guards changed underneath.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PerspectiveDateFormatLockTests : IAsyncDisposable {
  private const string TABLE = "wh_per_date_format";

  /// <summary>
  /// The formula that reproduces the serializer's rendering from a timestamp, if it can be
  /// reproduced. Microseconds are the finest PostgreSQL carries; the trailing-zero trim is what makes
  /// a whole second render without a fraction, and the dot trim removes the point it leaves behind.
  /// </summary>
  private const string FORMULA =
    "rtrim(rtrim(to_char(@p AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US'), '0'), '.') || 'Z'";

  [SuppressIndexAdvisory("format fixture; one row, read back to see exactly what the mapping wrote")]
  public class DateModel {
    /// <summary>A whole second, which is where a fraction has to disappear entirely.</summary>
    public DateTime WholeSecond { get; init; }

    /// <summary>One fractional digit, which is where trailing zeros have to be absent.</summary>
    public DateTime Tenth { get; init; }

    /// <summary>Milliseconds, the precision most values carry.</summary>
    public DateTime Millis { get; init; }

    /// <summary>Microseconds, the finest precision PostgreSQL can hold.</summary>
    public DateTime Micros { get; init; }

    /// <summary>A hundred-nanosecond tick, which is finer than PostgreSQL can hold.</summary>
    public DateTime Ticks { get; init; }

    /// <summary>Midnight, where both the fraction and the time of day are zero.</summary>
    public DateTime Midnight { get; init; }

    /// <summary>The largest value the type carries, which is a seven-digit fraction.</summary>
    public DateTime Max { get; init; }

    /// <summary>The smallest value the type carries, which is a single-digit year.</summary>
    public DateTime Min { get; init; }

    /// <summary>An offset of zero, which is the case that looks like a DateTime and is not.</summary>
    public DateTimeOffset OffsetUtc { get; init; }

    /// <summary>A positive half-hour offset, written from a zone that uses one.</summary>
    public DateTimeOffset OffsetAhead { get; init; }

    /// <summary>A negative offset, the same instant as the one above.</summary>
    public DateTimeOffset OffsetBehind { get; init; }
  }

  // The same instant, written three ways. Equality in .NET compares instants, so all three are equal
  // to each other; what they are stored as is the question.
  private static readonly DateTimeOffset _instantUtc = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
  private static readonly DateTimeOffset _instantAhead = _instantUtc.ToOffset(TimeSpan.FromMinutes(330));
  private static readonly DateTimeOffset _instantBehind = _instantUtc.ToOffset(TimeSpan.FromHours(-8));

  private sealed class DateDbContext(DbContextOptions<DateDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<DateModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      });

      modelBuilder.UseWhizbangJsonbContainment();
    }
  }

  private string _databaseName = null!;
  private string _connectionString = null!;
  private DateDbContext? _context;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"date_format_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }

    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    await using (var db = new NpgsqlConnection(_connectionString)) {
      await db.OpenAsync();
      await using var ddl = new NpgsqlCommand($"""
        CREATE TABLE {TABLE} (
          id uuid PRIMARY KEY,
          created_at timestamptz NOT NULL,
          updated_at timestamptz NOT NULL,
          version integer NOT NULL,
          data jsonb NOT NULL,
          metadata jsonb NOT NULL,
          scope jsonb NOT NULL
        );
        """, db);
      await ddl.ExecuteNonQueryAsync();
    }

    _context = new DateDbContext(new DbContextOptionsBuilder<DateDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    _context.Add(new PerspectiveRow<DateModel> {
      Id = Guid.NewGuid(),
      Data = new DateModel {
        WholeSecond = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        Tenth = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1_000_000),
        Millis = new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc),
        Micros = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1_234_560),
        Ticks = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1_234_567),
        Midnight = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
        Max = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
        Min = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
        OffsetUtc = _instantUtc,
        OffsetAhead = _instantAhead,
        OffsetBehind = _instantBehind,
      },
      Metadata = new PerspectiveMetadata(),
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    });

    await _context.SaveChangesAsync();
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_context is not null) {
      await _context.DisposeAsync();
      _context = null;
    }

    if (_databaseName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
          $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
      } catch (NpgsqlException) {
        // The container is shared and the database is per-test; a failed drop is not a test failure.
      }
    }

    GC.SuppressFinalize(this);
  }

  private async Task<string?> _scalarAsync(
    string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, db);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }

    var result = await command.ExecuteScalarAsync(cancellationToken);
    return result is null or DBNull ? null : result.ToString();
  }

  private Task<string?> _storedAsync(string field, CancellationToken cancellationToken) =>
    _scalarAsync($"SELECT data ->> '{field}' FROM {TABLE}", cancellationToken);

  /// <summary>
  /// The exact text a DateTime lands as, per precision. The fraction is written only when it is not
  /// zero and carries no trailing zeros, which is why a fixed-width SQL format alone does not agree
  /// with it and the formula has to trim.
  /// </summary>
  /// <remarks>
  /// The seven-digit case is the one worth reading. A .NET tick is a hundred nanoseconds, so the value
  /// written carried a seventh fractional digit, and what landed has six: the mapping truncates to
  /// microseconds on the way in. That is the same precision a PostgreSQL timestamp holds, so the two
  /// sides agree by construction and there is no precision gap between what is stored and what a
  /// query parameter can carry.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("WholeSecond", "2026-03-04T05:06:07Z")]
  [Arguments("Tenth", "2026-03-04T05:06:07.1Z")]
  [Arguments("Millis", "2026-03-04T05:06:07.123Z")]
  [Arguments("Micros", "2026-03-04T05:06:07.123456Z")]
  [Arguments("Ticks", "2026-03-04T05:06:07.123456Z")]
  [Arguments("Midnight", "2026-03-04T00:00:00Z")]
  public async Task ADateTime_IsStoredAsThisExactTextAsync(
    string field, string expected, CancellationToken cancellationToken) {
    await Assert.That(await _storedAsync(field, cancellationToken)).IsEqualTo(expected);
  }

  /// <summary>
  /// The two extremes of the range are not stored as dates at all. They map to the PostgreSQL
  /// infinities, and the document holds those words.
  /// </summary>
  /// <remarks>
  /// This is the one place a date genuinely cannot be reproduced by formatting, and it is a pair of
  /// values rather than a range, so it is handled by naming them rather than by standing down from
  /// dates altogether. They are also what a sentinel like "never expires" is usually written as, so
  /// getting them wrong would not be an edge case in practice.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("Max", "infinity")]
  [Arguments("Min", "-infinity")]
  public async Task TheExtremesAreStoredAsTheInfinitiesAsync(
    string field, string expected, CancellationToken cancellationToken) {
    await Assert.That(await _storedAsync(field, cancellationToken)).IsEqualTo(expected);
  }

  /// <summary>
  /// A DateTimeOffset keeps the offset it was written with, so the same instant written three ways is
  /// stored as three different strings.
  /// </summary>
  /// <remarks>
  /// This is the fact that separates the two types. A DateTime is a formatting problem: one instant
  /// has one rendering, and a rendering can be reproduced. A DateTimeOffset is an information problem:
  /// equality compares instants, so all three values below are equal in .NET, yet no query-side
  /// formatting can produce all three strings from one instant, because the offset each row was
  /// written with is not recoverable from the value being compared.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("OffsetUtc", "2026-03-04T05:06:07+00:00")]
  [Arguments("OffsetAhead", "2026-03-04T10:36:07+05:30")]
  [Arguments("OffsetBehind", "2026-03-03T21:06:07-08:00")]
  public async Task ADateTimeOffset_KeepsTheOffsetItWasWrittenWithAsync(
    string field, string expected, CancellationToken cancellationToken) {
    await Assert.That(await _storedAsync(field, cancellationToken)).IsEqualTo(expected);
  }

  /// <summary>
  /// The three offset renderings really are the same instant, so the divergence above is about the
  /// stored text and not about the values being different.
  /// </summary>
  [Test]
  public async Task TheThreeOffsetsAreOneInstantAsync() {
    await Assert.That(_instantAhead).IsEqualTo(_instantUtc);
    await Assert.That(_instantBehind).IsEqualTo(_instantUtc);
    await Assert.That(_instantAhead.Offset).IsNotEqualTo(_instantUtc.Offset);
  }

  /// <summary>
  /// The SQL formula reproduces the stored text for every ordinary value, at every precision the
  /// mapping writes.
  /// </summary>
  /// <remarks>
  /// This is the whole answer for DateTime, and it is an affirmative one. The rendering is a function
  /// of the instant, the instant survives the round trip as a parameter because both sides are
  /// microseconds, and the variable-width fraction is what the trimming handles. Nothing here depends
  /// on the value being coarse.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("WholeSecond")]
  [Arguments("Tenth")]
  [Arguments("Millis")]
  [Arguments("Micros")]
  [Arguments("Ticks")]
  [Arguments("Midnight")]
  public async Task TheFormulaReproducesTheStoredTextAsync(string field, CancellationToken cancellationToken) {
    var stored = await _storedAsync(field, cancellationToken);
    var value = DateTime.Parse(stored!, CultureInfo.InvariantCulture,
      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    var formatted = await _scalarAsync($"SELECT {FORMULA}", cancellationToken, ("p", value));

    await Assert.That(formatted).IsEqualTo(stored);
  }

  /// <summary>
  /// The formula cannot produce the infinities, which is why they are named rather than formatted.
  /// </summary>
  /// <remarks>
  /// What <c>to_char</c> does with an infinite timestamp is the reason these two values need their own
  /// answer: the text it yields is not the word the document holds, so a filter comparing against one
  /// of them would build a document that matches nothing. Recorded here as the requirement the
  /// emission has to satisfy.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("Max", "infinity")]
  [Arguments("Min", "-infinity")]
  public async Task TheFormulaCannotProduceAnInfinityAsync(
    string field, string stored, CancellationToken cancellationToken) {
    var value = string.Equals(field, "Max", StringComparison.Ordinal)
      ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
      : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    var formatted = await _scalarAsync($"SELECT {FORMULA}", cancellationToken, ("p", value));

    await Assert.That(await _storedAsync(field, cancellationToken)).IsEqualTo(stored);
    await Assert.That(formatted).IsNotEqualTo(stored)
      .Because("an infinity has to be produced by name, not by formatting an instant, which is why "
        + "the emission carries an isfinite branch at all. That branch disappears once dates are "
        + "stored as a number, because MaxValue in epoch microseconds is an ordinary integer. IF "
        + "THIS ASSERTION FAILS BECAUSE to_char NOW RENDERS AN INFINITE TIMESTAMP USABLY, the branch "
        + "can go without waiting for the format change.");
  }

  /// <summary>
  /// How PostgreSQL renders an infinite timestamp, and how volatile the parts of the formula are.
  /// Both decide how the emission has to be built.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task InfinityRenderingAndVolatility_AreRecordedAsync(CancellationToken cancellationToken) {
    var lines = new List<string>();

    foreach (var value in new[] { "infinity", "-infinity" }) {
      lines.Add($"isfinite({value}): {await _scalarAsync(
        $"SELECT isfinite('{value}'::timestamptz)::text", cancellationToken)}");
      lines.Add($"to_jsonb({value}): {await _scalarAsync(
        $"SELECT to_jsonb('{value}'::timestamptz) #>> '{{}}'", cancellationToken)}");
      lines.Add($"to_char({value}): {await _scalarAsync(
        $"SELECT to_char(timezone('UTC', '{value}'::timestamptz), 'YYYY-MM-DD\"T\"HH24:MI:SS.US')",
        cancellationToken)}");
    }

    lines.Add($"volatility: {await _scalarAsync(
      """
      SELECT string_agg(format('%s/%s', p.proname, p.provolatile), ' ' ORDER BY p.proname)
      FROM pg_proc p
      WHERE (p.proname = 'to_char' AND pg_get_function_arguments(p.oid) = 'timestamp without time zone, text')
         OR (p.proname = 'timezone' AND pg_get_function_arguments(p.oid) = 'text, timestamp with time zone')
      """, cancellationToken)}");

    var report = string.Join('\n', lines);
    var target = Environment.GetEnvironmentVariable("WHIZ_DATE_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.WriteAllTextAsync(target, report, cancellationToken);
    }

    await Assert.That(report).IsNotEmpty();
  }

  /// <summary>
  /// A containment document built from the formula matches the row, which is the claim that matters:
  /// reproducing the text is sufficient, so a date filter can be compiled into containment.
  /// </summary>
  [Test]
  [Timeout(120000)]
  [Arguments("WholeSecond")]
  [Arguments("Tenth")]
  [Arguments("Millis")]
  [Arguments("Micros")]
  [Arguments("Ticks")]
  [Arguments("Midnight")]
  public async Task ContainmentMatchesWhereTheTextIsReproducedAsync(
    string field, CancellationToken cancellationToken) {
    var stored = await _storedAsync(field, cancellationToken);
    var value = DateTime.Parse(stored!, CultureInfo.InvariantCulture,
      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    var matched = await _scalarAsync(
      $"SELECT count(*) FROM {TABLE} WHERE data @> jsonb_build_object('{field}', {FORMULA})",
      cancellationToken, ("p", value));

    await Assert.That(matched).IsEqualTo("1");
  }

  /// <summary>
  /// Ordering by a date is chronological, across rows whose fractional precision differs.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the question the stored format raises and it has to be answered by executing, not by
  /// reading the format. The rendering trims trailing zeros, so a whole second ends in <c>Z</c> while
  /// a fractional one ends in a digit, and <c>Z</c> is greater than <c>.</c> in every byte ordering.
  /// Sorting the stored strings therefore puts <c>05:06:07.1Z</c> before <c>05:06:07Z</c>, which is
  /// backwards. The second assertion below shows exactly that, so the hazard is on record.
  /// </para>
  /// <para>
  /// Ordering a query is nonetheless correct, because it never sorts the stored text. Entity Framework
  /// casts the extracted value to a timestamp and sorts that, and the containment rewrite is scoped to
  /// predicates and never reaches an ordering key, so nothing about the rewrite changes it. What the
  /// text ordering rules out is a shortcut rather than a behavior: the raw string cannot be used as a
  /// btree key to answer a range or an ordering. A fixed-width stored form could be, which is the one
  /// thing that would argue for changing it.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task OrderingByADate_IsChronologicalAcrossPrecisionsAsync(CancellationToken cancellationToken) {
    var second = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // Deliberately inserted in an order where the text ordering and the chronological one disagree.
    var inserted = new[] {
      second.AddTicks(1_000_000),
      second,
      second.AddTicks(1_234_560),
      second.AddSeconds(1),
    };

    foreach (var value in inserted) {
      _context!.Add(new PerspectiveRow<DateModel> {
        Id = Guid.NewGuid(),
        Data = new DateModel { WholeSecond = value },
        Metadata = new PerspectiveMetadata(),
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1,
      });
    }

    await _context!.SaveChangesAsync(cancellationToken);

    var query = _context.Set<PerspectiveRow<DateModel>>()
      .Where(r => r.Data.WholeSecond >= second)
      .OrderBy(r => r.Data.WholeSecond)
      .Select(r => r.Data.WholeSecond);

    // The ordering key is the parsed instant, not the stored string.
    await Assert.That(query.ToQueryString()).Contains("ORDER BY", StringComparison.Ordinal);

    var ordered = await query.ToListAsync(cancellationToken);

    await Assert.That(string.Join(",", ordered.Select(d => d.Ticks)))
      .IsEqualTo(string.Join(",", inserted.Order().Select(d => d.Ticks)))
      .Because("a date orders by instant, whatever precision each row carries");

    // And the same rows sorted by the stored text come back in a different order, which is why that
    // text cannot stand in as an index key.
    var byText = await _scalarAsync($"""
      SELECT string_agg(t, ',' ORDER BY t COLLATE "C")
      FROM (SELECT data ->> 'WholeSecond' AS t FROM {TABLE}
            WHERE (data ->> 'WholeSecond') LIKE '2026-06-01%') s
      """, cancellationToken);

    // Not merely shifted: a longer fraction sorts before a shorter one because a digit precedes 'Z',
    // and both precede the whole second. Three of the four are out of chronological order.
    await Assert.That(byText).IsEqualTo(
      "2026-06-01T12:00:00.123456Z,2026-06-01T12:00:00.1Z,2026-06-01T12:00:00Z,2026-06-01T12:00:01Z")
      .Because("the trimmed fraction makes the stored text sort by width before it sorts by time");
  }

  /// <summary>The filter a case name refers to, written the way a repository would write it.</summary>
  private static Expression<Func<PerspectiveRow<DateModel>, bool>> _filterFor(string precision) {
    var second = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    return precision switch {
      "whole second" => r => r.Data.WholeSecond == second,
      "one digit" => r => r.Data.Tenth == second.AddTicks(1_000_000),
      "milliseconds" => r => r.Data.Millis == new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc),
      "microseconds" => r => r.Data.Micros == second.AddTicks(1_234_560),
      "finer than a microsecond" => r => r.Data.Ticks == second.AddTicks(1_234_567),
      "midnight" => r => r.Data.Midnight == new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
      "the upper sentinel" => r => r.Data.Max == DateTime.MaxValue,
      "the lower sentinel" => r => r.Data.Min == DateTime.MinValue,
      _ => throw new InvalidOperationException(precision),
    };
  }

  /// <summary>
  /// A date filter written the ordinary way compiles to containment and finds its row, at every
  /// precision and at both sentinels.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the claim the rest of the file exists to support. The two sentinels are included because
  /// they are the only values whose stored text is not a rendering of an instant, and because a
  /// filter comparing against one is what a "never expires" condition looks like; if the emission
  /// mishandled them the result would be zero rows rather than an error.
  /// </para>
  /// <para>
  /// The finer-than-a-microsecond case is asserted for the same reason it is locked above. The value
  /// written and the value compared both truncate to the same microsecond, so the filter finds the
  /// row; the digit the caller supplied is discarded identically on both paths, which is what makes
  /// the rewrite faithful rather than lucky.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("whole second")]
  [Arguments("one digit")]
  [Arguments("milliseconds")]
  [Arguments("microseconds")]
  [Arguments("finer than a microsecond")]
  [Arguments("midnight")]
  [Arguments("the upper sentinel")]
  [Arguments("the lower sentinel")]
  public async Task ADateFilterCompilesToContainmentAndFindsItsRowAsync(
    string precision, CancellationToken cancellationToken) {
    var filter = _filterFor(precision);

    var sql = _context!.Set<PerspectiveRow<DateModel>>().Where(filter).ToQueryString();
    await Assert.That(sql).Contains("@>", StringComparison.Ordinal)
      .Because("a date filter has to reach the index like any other eligible type");

    var found = await _context.Set<PerspectiveRow<DateModel>>().CountAsync(filter, cancellationToken);

    await Assert.That(found).IsEqualTo(1)
      .Because("the rendering has to produce the text the row holds, not merely some text");
  }

  /// <summary>
  /// An offset filter keeps the extraction form, which is correct rather than unfinished: the stored
  /// text cannot be derived from the instant being compared.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task AnOffsetFilterKeepsTheExtractionFormAsync(CancellationToken cancellationToken) {
    var sql = _context!.Set<PerspectiveRow<DateModel>>()
      .Where(r => r.Data.OffsetAhead == _instantAhead)
      .ToQueryString();

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal);

    // And it answers correctly in that form, so what is missing is an index, not a result. It also
    // finds the row when given a different offset for the same instant, which is the behavior a
    // containment test could not reproduce.
    await Assert.That(await _context.Set<PerspectiveRow<DateModel>>()
      .CountAsync(r => r.Data.OffsetAhead == _instantAhead, cancellationToken)).IsEqualTo(1);
    await Assert.That(await _context.Set<PerspectiveRow<DateModel>>()
      .CountAsync(r => r.Data.OffsetAhead == _instantUtc, cancellationToken)).IsEqualTo(1)
      .Because("equality compares instants, and this is the comparison containment cannot express");
  }
}
