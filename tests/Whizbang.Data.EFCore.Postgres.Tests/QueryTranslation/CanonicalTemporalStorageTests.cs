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
