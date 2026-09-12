using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// That a date, a time and a duration reach the document as numbers, come back unchanged, and can
/// carry the index that was the whole point of changing their stored form.
/// </summary>
/// <remarks>
/// <para>
/// The generator emits this configuration; those tests assert the text it emits. This asserts that
/// the text describes something that works. Neither is sufficient alone: a generator test passes on
/// output that no database would accept, and a hand-written mapping proves nothing about what a
/// model author actually gets.
/// </para>
/// <para>
/// The seam between them is real and worth naming. The configuration here is written by hand to
/// mirror what the generator emits, because a value conversion needs a property expression per
/// property and those exist only at compile time. <c>CanonicalTemporalConfigurationTests</c> pins the
/// emitted line exactly so the two cannot drift apart quietly; if this file changes shape, that
/// assertion has to change with it.
/// </para>
/// <para>
/// The index assertion is the reason any of this happened. A text rendering of a date casts out of
/// the document through a stable expression, which PostgreSQL refuses to index; a number casts
/// through an immutable one. That the index can be created at all is the property being bought.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class CanonicalTemporalStorageTests : IAsyncDisposable {
  private const string TABLE = "wh_per_canonical_temporal";

  [SuppressIndexAdvisory("storage fixture; rows are read back to see exactly what the mapping wrote")]
  public class TemporalModel {
    /// <summary>An instant, the case the whole family is modeled on.</summary>
    public DateTime OccurredAt { get; init; }

    /// <summary>An instant carrying an offset, which normalizes to the instant it names.</summary>
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>A date without a time.</summary>
    public DateOnly Day { get; init; }

    /// <summary>A time of day, the type whose rendering carried a seventh fractional digit.</summary>
    public TimeOnly Clock { get; init; }

    /// <summary>A duration, which keeps full precision because nothing renders it.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>An optional instant, absent on some rows.</summary>
    public DateTime? MaybeAt { get; init; }

    /// <summary>A label, so the row can be found without touching a converted key.</summary>
    public string Label { get; init; } = string.Empty;
  }

  private sealed class TemporalDbContext(DbContextOptions<TemporalDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<TemporalModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          // Mirrors CanonicalTemporalDiscovery.ConfigurationFor exactly. See the note on the class.
          d.Property(p => p.OccurredAt).HasConversion<long>(
            v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
            v => CanonicalTemporalFormat.FromEpochMicroseconds(v));
          d.Property(p => p.RecordedAt).HasConversion<long>(
            v => CanonicalTemporalFormat.ToEpochMicroseconds(v),
            v => CanonicalTemporalFormat.OffsetFromEpochMicroseconds(v));
          d.Property(p => p.Day).HasConversion<int>(
            v => CanonicalTemporalFormat.ToEpochDays(v),
            v => CanonicalTemporalFormat.FromEpochDays(v));
          d.Property(p => p.Clock).HasConversion<long>(
            v => CanonicalTemporalFormat.ToMicrosecondsOfDay(v),
            v => CanonicalTemporalFormat.FromMicrosecondsOfDay(v));
          d.Property(p => p.Elapsed).HasConversion<long>(
            v => CanonicalTemporalFormat.ToTicks(v),
            v => CanonicalTemporalFormat.FromTicks(v));
          d.Property(p => p.MaybeAt).HasConversion<long?>(
            v => v == null ? (long?)null : CanonicalTemporalFormat.ToEpochMicroseconds(v.Value),
            v => v == null ? (DateTime?)null : CanonicalTemporalFormat.FromEpochMicroseconds(v.Value));
        });
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

  private static readonly DateTime _origin = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

  /// <summary>The only row past the range boundary.</summary>
  private static readonly string[] _pastTheBoundary = ["row-2"];

  /// <summary>Every row, newest first.</summary>
  private static readonly string[] _newestFirst = ["row-2", "row-1", "row-0"];

  /// <summary>The single row an equality filter should find.</summary>
  private static readonly string[] _theMiddleRow = ["row-1"];

  private string _databaseName = null!;
  private string _connectionString = null!;
  private TemporalDbContext? _context;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"canonical_temporal_{Guid.NewGuid():N}";
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

    _context = new TemporalDbContext(new DbContextOptionsBuilder<TemporalDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    // Three rows an hour apart, so a range has something to include and something to exclude.
    for (var hour = 0; hour < 3; hour++) {
      _context.Add(new PerspectiveRow<TemporalModel> {
        Id = Guid.NewGuid(),
        Data = new TemporalModel {
          OccurredAt = _origin.AddHours(hour),
          RecordedAt = new DateTimeOffset(_origin.AddHours(hour), TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(330)),
          Day = new DateOnly(2026, 3, 4).AddDays(hour),
          Clock = new TimeOnly(5, 6, 7).AddHours(hour),
          Elapsed = TimeSpan.FromMinutes(hour).Add(TimeSpan.FromTicks(1_234_567)),
          MaybeAt = hour == 0 ? null : _origin.AddHours(hour),
          Label = $"row-{hour}",
        },
        Metadata = new PerspectiveMetadata(),
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1,
      });
    }

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
        // The container is torn down with the run; a database left behind costs nothing.
      }
    }

    GC.SuppressFinalize(this);
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    var value = await command.ExecuteScalarAsync();
    return value?.ToString() ?? "<null>";
  }

  /// <summary>
  /// Every temporal key is a JSON number, which is what makes the rest of it possible.
  /// </summary>
  /// <remarks>
  /// jsonb compares numbers by value and text by bytes, so this single fact is what removes the
  /// trailing-zero problem, the varying fraction width, and the infinity rendering all at once.
  /// </remarks>
  [Test]
  [Arguments("OccurredAt")]
  [Arguments("RecordedAt")]
  [Arguments("Day")]
  [Arguments("Clock")]
  [Arguments("Elapsed")]
  [Arguments("MaybeAt")]
  public async Task ATemporalKeyIsStoredAsANumberAsync(string key) {
    var type = await _scalarAsync(
      $"SELECT jsonb_typeof(data -> '{key}') FROM {TABLE} WHERE data ->> 'Label' = 'row-2'");

    await Assert.That(type).IsEqualTo("number")
      .Because($"'{key}' has to be a number for its extraction to cast through an immutable "
        + "expression, which is the only kind PostgreSQL will build an index over");
  }

  /// <summary>An absent optional value is absent, not a number and not a JSON null.</summary>
  /// <remarks>
  /// Worth its own assertion because the conversion has an explicit null branch, and a converter that
  /// wrote a zero for a missing value would be indistinguishable from the epoch.
  /// </remarks>
  [Test]
  public async Task AnAbsentOptionalValueStaysAbsentAsync() {
    var type = await _scalarAsync(
      $"SELECT coalesce(jsonb_typeof(data -> 'MaybeAt'), 'missing') FROM {TABLE} "
      + "WHERE data ->> 'Label' = 'row-0'");

    await Assert.That(type).IsIn("missing", "null")
      .Because("a null has to stay a null rather than becoming the epoch, which is what a converter "
        + "that forgot its null branch would write");
  }

  /// <summary>The value read back is the value written.</summary>
  [Test]
  public async Task ARowRoundTripsAsync() {
    var row = await _context!.Set<PerspectiveRow<TemporalModel>>()
      .AsNoTracking()
      .FirstAsync(r => r.Data.Label == "row-1");

    await Assert.That(row.Data.OccurredAt).IsEqualTo(_origin.AddHours(1));
    await Assert.That(row.Data.Day).IsEqualTo(new DateOnly(2026, 3, 5));
    await Assert.That(row.Data.Clock).IsEqualTo(new TimeOnly(6, 6, 7));
    await Assert.That(row.Data.Elapsed)
      .IsEqualTo(TimeSpan.FromMinutes(1).Add(TimeSpan.FromTicks(1_234_567)))
      .Because("a duration is never compared against a PostgreSQL interval, so it keeps full .NET "
        + "precision where the instants truncate to microseconds");
    await Assert.That(row.Data.MaybeAt).IsEqualTo(_origin.AddHours(1));
  }

  /// <summary>
  /// An offset comes back as the instant it named, which is the deliberate loss.
  /// </summary>
  /// <remarks>
  /// Written with a five and a half hour offset and read back at zero. Equality on the type compares
  /// instants, so nothing about a comparison changes; a model that needs the original offset back
  /// keeps it in a field of its own. Asserted rather than left implicit because it is the one thing
  /// this format does not preserve.
  /// </remarks>
  [Test]
  public async Task AnOffsetComesBackAsItsInstantAsync() {
    var row = await _context!.Set<PerspectiveRow<TemporalModel>>()
      .AsNoTracking()
      .FirstAsync(r => r.Data.Label == "row-1");

    await Assert.That(row.Data.RecordedAt)
      .IsEqualTo(new DateTimeOffset(_origin.AddHours(1), TimeSpan.Zero));
    await Assert.That(row.Data.RecordedAt.Offset).IsEqualTo(TimeSpan.Zero)
      .Because("the stored form is the instant, so one instant has exactly one stored value and a "
        + "query for it does not have to produce every offset it might have been written with");
  }

  /// <summary>
  /// A range over a date returns the right rows, which the text form could not do.
  /// </summary>
  /// <remarks>
  /// The stored text sorted by fraction width before it sorted by time, so a range over it was not
  /// merely unindexed but wrong. This is the behavior the format change buys, so it is asserted on
  /// results rather than on a plan.
  /// </remarks>
  [Test]
  public async Task ARangeOverADateReturnsTheRightRowsAsync() {
    var boundary = _origin.AddMinutes(90);

    var labels = await _context!.Set<PerspectiveRow<TemporalModel>>()
      .AsNoTracking()
      .Where(r => r.Data.OccurredAt > boundary)
      .OrderBy(r => r.Data.OccurredAt)
      .Select(r => r.Data.Label)
      .ToListAsync();

    await Assert.That(labels).IsEquivalentTo(_pastTheBoundary);
  }

  /// <summary>
  /// The writer a perspective actually uses stores a temporal value the same way the mapping does.
  /// </summary>
  /// <remarks>
  /// <para>
  /// There are two writers, and only one of them is Entity Framework. A perspective row is written by
  /// the upsert strategy, which serializes the model with System.Text.Json and sends the document as
  /// a parameter; Entity Framework's mapping is what reads it back and what compiles a filter over
  /// it. A value conversion attaches to the mapping, so on its own it changes the reading and not the
  /// writing.
  /// </para>
  /// <para>
  /// If the two disagree the failure is total rather than partial: the upsert writes a rendering, the
  /// mapping reads a number, and every row written after the change is unreadable by the code meant
  /// to read it. This is the assertion that keeps them honest, and it is deliberately made through
  /// the real store rather than through change tracking, because change tracking is the path
  /// production does not take.
  /// </para>
  /// </remarks>
  [Test]
  [Arguments("OccurredAt")]
  [Arguments("Day")]
  [Arguments("Clock")]
  [Arguments("Elapsed")]
  public async Task TheUpsertWriterAgreesWithTheMappingAsync(string key) {
    var strategy = new PostgresUpsertStrategy();
    var id = Guid.CreateVersion7();

    await strategy.UpsertPerspectiveRowAsync(
      _context!,
      TABLE,
      id,
      new TemporalModel {
        OccurredAt = _origin,
        RecordedAt = new DateTimeOffset(_origin, TimeSpan.Zero),
        Day = new DateOnly(2026, 3, 4),
        Clock = new TimeOnly(5, 6, 7),
        Elapsed = TimeSpan.FromMinutes(3),
        Label = "upserted",
      },
      new PerspectiveMetadata(),
      new PerspectiveScope());

    var type = await _scalarAsync(
      $"SELECT jsonb_typeof(data -> '{key}') FROM {TABLE} WHERE data ->> 'Label' = 'upserted'");

    await Assert.That(type).IsEqualTo("number")
      .Because("the upsert is the writer a perspective actually uses, so a stored form it does not "
        + "produce is a stored form nothing produces");
  }

  /// <summary>
  /// A row written by the real writer is readable by the mapping, which is the whole point of the
  /// two agreeing.
  /// </summary>
  [Test]
  public async Task ARowWrittenByTheUpsertReadsBackThroughTheMappingAsync() {
    var strategy = new PostgresUpsertStrategy();

    await strategy.UpsertPerspectiveRowAsync(
      _context!,
      TABLE,
      Guid.CreateVersion7(),
      new TemporalModel {
        OccurredAt = _origin,
        RecordedAt = new DateTimeOffset(_origin, TimeSpan.Zero),
        Day = new DateOnly(2026, 3, 4),
        Clock = new TimeOnly(5, 6, 7),
        Elapsed = TimeSpan.FromMinutes(3),
        Label = "upserted",
      },
      new PerspectiveMetadata(),
      new PerspectiveScope());

    var row = await _context!.Set<PerspectiveRow<TemporalModel>>()
      .AsNoTracking()
      .FirstAsync(r => r.Data.Label == "upserted");

    await Assert.That(row.Data.OccurredAt).IsEqualTo(_origin);
    await Assert.That(row.Data.Day).IsEqualTo(new DateOnly(2026, 3, 4));
    await Assert.That(row.Data.Clock).IsEqualTo(new TimeOnly(5, 6, 7));
    await Assert.That(row.Data.Elapsed).IsEqualTo(TimeSpan.FromMinutes(3));
  }

  /// <summary>
  /// Equality on a date finds its row, which is the case the containment rewrite touches.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the one that could break quietly. The rewrite compiles an equality filter into a
  /// containment test by building the document the stored value would have, and while dates were
  /// stored as text that document had to carry a rendering of the date produced in SQL. A rendering
  /// compared against a number matches nothing, and it matches nothing silently: the query succeeds
  /// and returns no rows.
  /// </para>
  /// <para>
  /// Asserted on rows rather than on the compiled SQL for exactly that reason. A SQL-text assertion
  /// would pass on a statement that is valid and wrong.
  /// </para>
  /// </remarks>
  [Test]
  public async Task EqualityOnADateFindsItsRowAsync() {
    var target = _origin.AddHours(1);

    var labels = await _context!.Set<PerspectiveRow<TemporalModel>>()
      .AsNoTracking()
      .Where(r => r.Data.OccurredAt == target)
      .Select(r => r.Data.Label)
      .ToListAsync();

    await Assert.That(labels).IsEquivalentTo(_theMiddleRow)
      .Because("whatever form the filter is compiled into, it has to describe the value that was "
        + "actually stored; a rendering compared against a number returns nothing and says nothing");
  }

  /// <summary>
  /// An equality filter on a date keeps the extraction form, under both mechanisms.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is a trade taken deliberately, and it is the one place the canonical form gives something
  /// up. Stored as a rendering, a date equality was compiled into a containment test that the
  /// document's GIN index answered, which was free because that index exists on every perspective
  /// table. Stored as a number the value is converted, and neither mechanism reaches containment for
  /// a converted property: the rewrite runs before translation, where its operand is typed by the
  /// model rather than by what the row holds, and the reshape runs after, where the operand arrives
  /// wrapped in a cast rather than as the bare extraction it matches on.
  /// </para>
  /// <para>
  /// What is gained is larger than what is lost. Ranges, ordering and an index of any kind were
  /// impossible for a date before and are ordinary now, and a declared btree answers equality faster
  /// than containment did: one index probe and a heap fetch, against a document-index read that then
  /// rechecks every candidate row. What is lost is the zero-configuration case, an exact date
  /// equality on a field nobody declared an index for.
  /// </para>
  /// <para>
  /// That case is not left silent, which is the condition for the trade being acceptable at all.
  /// WHIZ302 reports it at build time and names the attribute that fixes it, where before it stayed
  /// quiet because containment was serving the filter.
  /// </para>
  /// <para>
  /// If this starts failing because containment appears, a mechanism learned to handle a converted
  /// property. That is worth having, and the advisory above should be revisited with it.
  /// </para>
  /// </remarks>
  [Test]
  [Arguments(ContainmentMode.ExpressionTree)]
  [Arguments(ContainmentMode.TranslatedTree)]
  public async Task ADateEqualityKeepsTheExtractionFormAsync(ContainmentMode mode) {
    JsonbContainmentSwitch.SetMode(mode);
    try {
      var target = _origin.AddHours(1);

      var sql = _context!.Set<PerspectiveRow<TemporalModel>>()
        .Where(r => r.Data.OccurredAt == target)
        .ToQueryString();

      await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal)
        .Because("the value is stored converted, and neither mechanism builds a containment document "
          + "from a converted property; the index that serves this is a declared btree");
      await Assert.That(sql).Contains("OccurredAt", StringComparison.Ordinal);
      await Assert.That(sql).DoesNotContain("to_char", StringComparison.Ordinal)
        .Because("a number needs no rendering, so nothing should still be treating a date as a type "
          + "that does");
    } finally {
      JsonbContainmentSwitch.Reset();
    }
  }

  /// <summary>Equality on every other temporal type finds its row too.</summary>
  [Test]
  public async Task EqualityOnTheOtherTemporalTypesFindsItsRowAsync() {
    var day = new DateOnly(2026, 3, 5);
    var clock = new TimeOnly(6, 6, 7);
    var elapsed = TimeSpan.FromMinutes(1).Add(TimeSpan.FromTicks(1_234_567));
    var recorded = new DateTimeOffset(_origin.AddHours(1), TimeSpan.Zero);

    var set = _context!.Set<PerspectiveRow<TemporalModel>>().AsNoTracking();

    await Assert.That(await set.CountAsync(r => r.Data.Day == day)).IsEqualTo(1);
    await Assert.That(await set.CountAsync(r => r.Data.Clock == clock)).IsEqualTo(1);
    await Assert.That(await set.CountAsync(r => r.Data.Elapsed == elapsed)).IsEqualTo(1);
    await Assert.That(await set.CountAsync(r => r.Data.RecordedAt == recorded)).IsEqualTo(1)
      .Because("the offset normalizes to its instant on both sides of the comparison, so a value "
        + "written at one offset is found by a query written at another");
  }

  /// <summary>Ordering by a date is chronological.</summary>
  [Test]
  public async Task OrderingByADateIsChronologicalAsync() {
    var labels = await _context!.Set<PerspectiveRow<TemporalModel>>()
      .AsNoTracking()
      .OrderByDescending(r => r.Data.OccurredAt)
      .Select(r => r.Data.Label)
      .ToListAsync();

    await Assert.That(labels).IsEquivalentTo(_newestFirst);
  }

  /// <summary>
  /// An index can be built over the extraction, which is the property the whole change was for.
  /// </summary>
  /// <remarks>
  /// This is the assertion that would have failed for every one of these types before. A cast from
  /// text to a timestamp or a date is STABLE, and PostgreSQL refuses to build an index expression
  /// that is not IMMUTABLE, because a key computed from a session setting could go stale. A cast to
  /// bigint is immutable, so the same extraction becomes indexable.
  /// </remarks>
  [Test]
  [Arguments("OccurredAt", "bigint")]
  [Arguments("RecordedAt", "bigint")]
  [Arguments("Day", "integer")]
  [Arguments("Clock", "bigint")]
  [Arguments("Elapsed", "bigint")]
  public async Task AnIndexCanBeBuiltOverTheExtractionAsync(string key, string cast) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var create = new NpgsqlCommand(
      $"CREATE INDEX ix_{key.ToLowerInvariant()} ON {TABLE} (((data ->> '{key}')::{cast}))", db);

    await Assert.That(async () => await create.ExecuteNonQueryAsync()).ThrowsNothing()
      .Because($"a {cast} cast is immutable, so its keys cannot go stale and PostgreSQL will index "
        + "it; the text-to-timestamp cast the rendering needed is stable and was refused outright");
  }
}
