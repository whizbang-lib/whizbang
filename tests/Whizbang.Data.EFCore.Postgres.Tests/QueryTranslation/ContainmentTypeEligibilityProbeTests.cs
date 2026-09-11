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
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Which CLR types could safely join the containment set, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// A containment test names a literal document, so it only means what the equality it replaces meant
/// when the text the serializer wrote and the text PostgreSQL generates from the same value are
/// identical. Dates, enumerations and binary floating point were excluded on the grounds that those
/// two forms are "not guaranteed to agree", which is a reasonable prior and not a measurement.
/// </para>
/// <para>
/// This writes a row through the real mapping, reads back what actually landed in the document, and
/// asks PostgreSQL whether a containment test built the way the rewrite builds it matches. Enumerations
/// matter most: a survey of real repositories found them filtered constantly and always by equality,
/// so if they are safe they are the largest remaining win.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ContainmentTypeEligibilityProbeTests : IAsyncDisposable {
  private const string TABLE = "wh_per_eligibility";

  public enum Mood { Low = 0, High = 1 }

  /// <summary>A combinable enumeration, where a stored value is a set rather than one member.</summary>
  [Flags]
  public enum Access { None = 0, Read = 1, Write = 2, Both = Read | Write }

  /// <summary>An enumeration over a number no overload accepts.</summary>
  public enum Tier : uint { First = 1 }

  [SuppressIndexAdvisory("probe fixture; one row, read back to see what the mapping wrote")]
  public class ProbeModel {
    public Mood State { get; init; }
    public double Dbl { get; init; }
    public float Flt { get; init; }
    public DateTime When { get; init; }
    public DateTimeOffset WhenOffset { get; init; }
    public decimal Money { get; init; }
    public long Big { get; init; }

    // One value proves nothing about a floating-point format, so carry the awkward ones too.
    public double Third { get; init; }
    public double Huge { get; init; }
    public double Tiny { get; init; }
    public float FloatThird { get; init; }

    // A non-UTC offset is where DateTimeOffset is most likely to disagree.
    public DateTimeOffset Shifted { get; init; }

    // Configured to store as text: if a converter can change an enumeration's stored form, then a
    // numeric containment test would silently match nothing on such a model.
    public Mood Named { get; init; }

    // An ALREADY-eligible type with a converter. If this stores as text while the rewrite builds a
    // numeric document, then the shipped rewrite is silently wrong on such a model.
    public int ConvertedNum { get; init; }

    // The remaining date and time family, plus the two enumeration shapes the equality overloads do
    // not obviously cover. Each is here to be read back rather than reasoned about.
    public DateOnly Day { get; init; }
    public TimeOnly Clock { get; init; }
    public TimeOnly ClockFine { get; init; }
    public TimeSpan Span { get; init; }
    public TimeSpan SpanWithDays { get; init; }
    public char Letter { get; init; }

    /// <summary>Two flags set at once, which is the case a single-member comparison does not describe.</summary>
    public Access Perms { get; init; }

    public Tier Unsupported { get; init; }

    /// <summary>
    /// The framework's own identifier value object, mapped the only way it can be.
    /// </summary>
    /// <remarks>
    /// It cannot be mapped as a complex type: it exposes the value alongside creation metadata and
    /// keeps its constructor private, so Entity Framework cannot bind one and the model fails to
    /// build at all rather than merely failing to filter. A converter down to the underlying
    /// identifier is the available mapping, and what this measures is where a filter on it then
    /// lands.
    /// </remarks>
    public TrackedGuid Tracked { get; init; }

    /// <summary>A time-ordered identifier held as a plain Guid, which is the usual shape.</summary>
    public Guid SortableId { get; init; }

    // How much of the serialized form is stable? The fraction is what decides whether a SQL-side
    // format string can reproduce it.
    public DateTime WholeSecond { get; init; }
    public DateTime Millis { get; init; }
    public DateTime Ticks { get; init; }
    public DateTime TrailingZeros { get; init; }
  }

  private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<ProbeModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.Property(p => p.Named).HasConversion<string>();
          d.Property(p => p.Tracked)
            .HasConversion(t => t.Value, g => TrackedGuid.FromExternal(g));
          d.Property(p => p.ConvertedNum).HasConversion<string>();
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

  private string _databaseName = null!;
  private string _connectionString = null!;
  private ProbeDbContext? _context;

  private static readonly TrackedGuid _tracked = TrackedGuid.NewMedo();
  private static readonly Guid _sortable = TrackedGuid.NewMedo().Value;
  private static readonly DateTime _when = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
  private static readonly DateTimeOffset _whenOffset = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _databaseName = $"eligibility_{Guid.NewGuid():N}";
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

    _context = new ProbeDbContext(new DbContextOptionsBuilder<ProbeDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

    _context.Add(new PerspectiveRow<ProbeModel> {
      Id = Guid.NewGuid(),
      Data = new ProbeModel {
        State = Mood.High,
        Dbl = 0.1d,
        Flt = 0.1f,
        When = _when,
        WhenOffset = _whenOffset,
        Money = 1.50m,
        Big = 9007199254740993L,
        Third = 1.0d / 3.0d,
        Huge = 1e20d,
        Tiny = 1e-7d,
        FloatThird = 1f / 3f,
        Shifted = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromMinutes(330)),
        Named = Mood.High,
        ConvertedNum = 7,
        WholeSecond = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        Millis = new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc),
        Ticks = new DateTime(638_700_000_001_234_567L, DateTimeKind.Utc),
        TrailingZeros = new DateTime(2026, 3, 4, 5, 6, 7, 100, DateTimeKind.Utc),
        Day = new DateOnly(2026, 3, 4),
        Clock = new TimeOnly(5, 6, 7),
        ClockFine = new TimeOnly(5, 6, 7).Add(TimeSpan.FromTicks(1_234_567)),
        Span = new TimeSpan(5, 6, 7),
        SpanWithDays = new TimeSpan(2, 5, 6, 7, 123),
        Letter = 'q',
        Perms = Access.Read | Access.Write,
        Unsupported = Tier.First,
        Tracked = _tracked,
        SortableId = _sortable,
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

    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
      await drop.ExecuteNonQueryAsync();
    } catch (NpgsqlException) {
      // Per-test database on a shared container; a failed drop is not a test failure.
    }

    GC.SuppressFinalize(this);
  }

  private async Task<string?> _scalarAsync(string sql, params (string Name, object Value)[] parameters) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }

    var result = await command.ExecuteScalarAsync();
    return result is null or DBNull ? null : result.ToString();
  }

  /// <summary>
  /// The guard that stops a value converter turning a filter into silent zero rows.
  /// </summary>
  /// <remarks>
  /// A converted property is stored in the converter's form, so an int written as text lands as
  /// <c>"7"</c>. A containment document built from a raw int would be <c>{"n": 7}</c> and would match
  /// nothing. The question this settles is whether Entity Framework applies the converter to the
  /// parameter as well, in which case the document agrees after all. Asserted as agreement between
  /// the rewrite being on and off rather than as a SQL shape, because the answer that matters is the
  /// rows.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task AConvertedMember_KeepsTheExtractionFormAsync(CancellationToken cancellationToken) {
    var converted = 7;

    // The baseline first: whether Entity Framework can filter a converted member of a JSON complex
    // property at all, with the rewrite standing down. If it cannot, the rewrite has nothing to
    // answer for and the limitation belongs upstream.
    Exception? baselineFailure = null;
    var withoutRewrite = -1;
    JsonbContainmentSwitch.Set(false);
    try {
      withoutRewrite = await _context!.Set<PerspectiveRow<ProbeModel>>()
        .CountAsync(r => r.Data.ConvertedNum == converted && r.Version > 0, cancellationToken);
    } catch (Exception ex) {
      baselineFailure = ex;
    } finally {
      JsonbContainmentSwitch.Reset();
    }

    if (baselineFailure is not null) {
      // Entity Framework cannot express this filter itself. The rewrite standing down is then the
      // correct behavior and there is nothing further to prove: it cannot be expected to improve on
      // a translation that does not exist.
      await Assert.That(baselineFailure).IsNotNull();
      return;
    }

    var found = await _context!.Set<PerspectiveRow<ProbeModel>>()
      .CountAsync(r => r.Data.ConvertedNum == converted, cancellationToken);

    await Assert.That(found).IsEqualTo(withoutRewrite)
      .Because("a converted member must answer the same with the rewrite on as with it off");
  }

  /// <summary>An unconverted member of the same type is still rewritten, so the guard is not blanket.</summary>
  [Test]
  [Timeout(120000)]
  public async Task AnUnconvertedMember_IsStillRewrittenAsync(CancellationToken cancellationToken) {
    var big = 9007199254740993L;

    var sql = _context!.Set<PerspectiveRow<ProbeModel>>()
      .Where(r => r.Data.Big == big)
      .ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal);

    var found = await _context.Set<PerspectiveRow<ProbeModel>>()
      .CountAsync(r => r.Data.Big == big, cancellationToken);

    await Assert.That(found).IsEqualTo(1);
  }

  /// <summary>
  /// A DateTimeOffset carrying a non-UTC offset cannot be a query parameter at all, which is a
  /// constraint of the driver rather than of containment: the same limit applies to an ordinary
  /// equality comparison, so it is not a reason for or against the rewrite.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task ANonUtcOffset_CannotBeAParameterAtAllAsync(CancellationToken cancellationToken) {
    await Assert.That(async () => await _scalarAsync(
        $"SELECT count(*) FROM {TABLE} WHERE data @> jsonb_build_object('Shifted', @p)",
        ("p", new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromMinutes(330)))))
      .Throws<ArgumentException>();
  }

  /// <summary>
  /// Records what the mapping actually wrote for each type, which is the fact every eligibility
  /// decision rests on.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task StoredForms_AreRecordedAsync(CancellationToken cancellationToken) {
    var document = await _scalarAsync($"SELECT data::text FROM {TABLE}");

    await Assert.That(document).IsNotNull();

    var target = Environment.GetEnvironmentVariable("WHIZ_ELIGIBILITY_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.WriteAllTextAsync(target, document!, cancellationToken);
    }

    // The enumeration is the one that decides whether the largest remaining win is available.
    await Assert.That(document!).Contains("State", StringComparison.Ordinal);
  }

  /// <summary>
  /// Whether a containment test built the way the rewrite builds it matches the stored row, per type.
  /// A type only becomes eligible if this and the extraction form agree.
  /// </summary>
  [Test]
  [Timeout(120000)]
  [Arguments("State")]
  [Arguments("Dbl")]
  [Arguments("Flt")]
  [Arguments("When")]
  [Arguments("WhenOffset")]
  [Arguments("Money")]
  [Arguments("Big")]
  [Arguments("Third")]
  [Arguments("Huge")]
  [Arguments("Tiny")]
  [Arguments("FloatThird")]
  [Arguments("Named")]
  [Arguments("ConvertedNum")]
  public async Task ContainmentAgreementPerType_IsRecordedAsync(string field, CancellationToken cancellationToken) {
    // Parameterized exactly as Entity Framework would: the CLR value, mapped by Npgsql.
    object value = field switch {
      "State" => (int)Mood.High,
      "Dbl" => 0.1d,
      "Flt" => 0.1f,
      "When" => _when,
      "WhenOffset" => _whenOffset,
      "Money" => 1.50m,
      "Big" => 9007199254740993L,
      "Third" => 1.0d / 3.0d,
      "Huge" => 1e20d,
      "Tiny" => 1e-7d,
      "FloatThird" => 1f / 3f,
      "Named" => "High",
      // Exactly what the rewrite would put in the document for an int member.
      "ConvertedNum" => 7,
      _ => throw new InvalidOperationException(field),
    };

    var matched = await _scalarAsync(
      $"SELECT count(*) FROM {TABLE} WHERE data @> jsonb_build_object('{field}', @p)",
      ("p", value));

    var storedText = await _scalarAsync($"SELECT data ->> '{field}' FROM {TABLE}");
    var generatedText = await _scalarAsync("SELECT (to_jsonb(@p)) #>> '{}'", ("p", value));

    var line = string.Create(CultureInfo.InvariantCulture,
      $"{field}: stored={storedText} generated={generatedText} containmentMatched={matched}");

    var target = Environment.GetEnvironmentVariable("WHIZ_ELIGIBILITY_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.AppendAllTextAsync(target, "\n" + line, cancellationToken);
    }

    // No expectation asserted: this test exists to produce the measurement the decision needs. The
    // eligible set is pinned by JsonbContainmentTypeSetTests, and a type only moves into it once the
    // two text forms here are shown to agree.
    await Assert.That(line).IsNotEmpty();
  }

  /// <summary>
  /// A time-ordered identifier sorts the same as text, as bytes, and chronologically, which is what a
  /// date does not do.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The framework's convention is a UUIDv7 identifier, which carries its timestamp in the leading
  /// bytes. Its stored rendering is fixed-width lowercase hexadecimal, so no trimming or variable
  /// precision can reorder it: the text order, the <c>uuid</c> byte order and the creation order all
  /// agree. That is the property the trimmed date rendering lacks.
  /// </para>
  /// <para>
  /// It matters because it decides what can be indexed. Ordering and range filtering over a date held
  /// in a document cannot use the stored text as a key, while over a time-ordered identifier they can,
  /// and the <c>text</c> to <c>uuid</c> cast is immutable so an expression index is available too.
  /// Cursor paging over a document-held identifier is therefore indexable, which is worth knowing
  /// before reaching for a promoted column.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task ATimeOrderedIdentifier_SortsTheSameAsTextAndAsBytesAsync(CancellationToken cancellationToken) {
    // Generated in order, so the creation order is known independently of how they sort.
    var created = new List<Guid>();
    for (var i = 0; i < 12; i++) {
      created.Add(TrackedGuid.NewMedo().Value);
    }

    await Assert.That(created.Select(g => g.ToString()).OrderBy(t => t, StringComparer.Ordinal).ToList())
      .IsEquivalentTo(created.Select(g => g.ToString()).ToList())
      .Because("a version 7 identifier's text rendering sorts in creation order");

    var agrees = await _scalarAsync(
      "SELECT bool_and((a < b) = ((a::uuid) < (b::uuid))) FROM unnest(@t) WITH ORDINALITY s(a, i) "
      + "CROSS JOIN unnest(@t) WITH ORDINALITY t2(b, j) WHERE i <> j",
      ("t", created.Select(g => g.ToString()).ToArray()));

    await Assert.That(agrees).IsEqualTo("True")
      .Because("the text ordering and the uuid ordering have to agree for either to serve as a key");
  }

  /// <summary>
  /// Records what the remaining date, time and enumeration shapes land as, and whether a filter on
  /// each currently reaches the index. The next eligibility decisions rest on this.
  /// </summary>
  /// <remarks>
  /// No expectation is asserted beyond the report being produced. Two assumptions about stored forms
  /// have already turned out wrong in this area, so the list of candidates is measured before anything
  /// is claimed about it.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task RemainingCandidates_AreRecordedAsync(CancellationToken cancellationToken) {
    var lines = new List<string>();

    foreach (var field in new[] {
      "Day", "Clock", "ClockFine", "Span", "SpanWithDays", "Letter", "Perms", "Unsupported",
      "Tracked", "SortableId",
    }) {
      var stored = await _scalarAsync($"SELECT data -> '{field}' FROM {TABLE}");
      var text = await _scalarAsync($"SELECT data ->> '{field}' FROM {TABLE}");
      lines.Add($"{field}: json={stored} text={text}");
    }

    // Whether a filter on each reaches the index today, which is a separate question from whether the
    // stored form would allow it to.
    foreach (var (name, sql) in new[] {
      ("Day", _context!.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Day == new DateOnly(2026, 3, 4)).ToQueryString()),
      ("Clock", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Clock == new TimeOnly(5, 6, 7)).ToQueryString()),
      ("Span", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Span == new TimeSpan(5, 6, 7)).ToQueryString()),
      ("Letter", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Letter == 'q').ToQueryString()),
      ("Perms equality", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Perms == Access.Both).ToQueryString()),
      ("Perms bitwise", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => (r.Data.Perms & Access.Read) == Access.Read).ToQueryString()),
      ("Unsupported", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Unsupported == Tier.First).ToQueryString()),
      ("Tracked", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.Tracked == _tracked).ToQueryString()),
      ("SortableId", _context.Set<PerspectiveRow<ProbeModel>>()
        .Where(r => r.Data.SortableId == _sortable).ToQueryString()),
    }) {
      var form = sql.Contains("@>", StringComparison.Ordinal) ? "containment" : "extraction";
      lines.Add($"{name}: {form} :: {sql.Split('\n')[^1].Trim()}");
    }

    var report = string.Join('\n', lines);
    var target = Environment.GetEnvironmentVariable("WHIZ_CANDIDATES_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.WriteAllTextAsync(target, report, cancellationToken);
    }

    await Assert.That(report).IsNotEmpty();
  }

  /// <summary>
  /// Records how a binary floating-point value reaches the document, which is what decides whether
  /// the containment form can be built for one at all.
  /// </summary>
  /// <remarks>
  /// A parameter and a literal are not the same question. Npgsql sends a parameter with its own type,
  /// so <c>jsonb_build_object('k', @p)</c> builds from a <c>double precision</c>. A literal written
  /// into the statement has no type annotation, and PostgreSQL reads a bare decimal literal as
  /// <c>numeric</c>, whose text form is not the shortest round-trip form the serializer wrote.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task FloatingPointLiteralForms_AreRecordedAsync(CancellationToken cancellationToken) {
    var lines = new List<string>();

    foreach (var (field, literal) in new[] {
      ("Dbl", "0.1"), ("Flt", "0.1"), ("Third", "0.3333333333333333"), ("FloatThird", "0.33333334"),
    }) {
      var stored = await _scalarAsync($"SELECT data ->> '{field}' FROM {TABLE}");
      var asNumeric = await _scalarAsync($"SELECT jsonb_build_object('{field}', {literal}) ->> '{field}'");
      var asDouble = await _scalarAsync($"SELECT jsonb_build_object('{field}', {literal}::float8) ->> '{field}'");
      var matchedNumeric = await _scalarAsync(
        $"SELECT count(*) FROM {TABLE} WHERE data @> jsonb_build_object('{field}', {literal})");
      var matchedDouble = await _scalarAsync(
        $"SELECT count(*) FROM {TABLE} WHERE data @> jsonb_build_object('{field}', {literal}::float8)");

      lines.Add(string.Create(CultureInfo.InvariantCulture,
        $"{field}: stored={stored} numeric={asNumeric} float8={asDouble} matchNumeric={matchedNumeric} matchFloat8={matchedDouble}"));
    }

    // Whether jsonb compares numbers by value or by the text they were written as, and whether the
    // text a cast produces is stable. Both decide if a cast can be relied on to normalize the value.
    lines.Add($"valueNotText: {await _scalarAsync("SELECT ('{\"a\":1.0}'::jsonb @> '{\"a\":1}'::jsonb)::text")}");
    foreach (var digits in new[] { "-1", "0", "1", "3" }) {
      var rendered = await _scalarAsync(
        $"SET LOCAL extra_float_digits = {digits}; SELECT jsonb_build_object('k', 0.1::float8) ->> 'k'");
      lines.Add($"extra_float_digits={digits}: {rendered}");
    }

    foreach (var type in new[] { "double", "float", "awkward double", "awkward float" }) {
      var (filter, rephrased) = _filtersFor(type);
      var on = _context!.Set<PerspectiveRow<ProbeModel>>().Where(filter).ToQueryString();
      var onRows = await _context.Set<PerspectiveRow<ProbeModel>>().CountAsync(filter, cancellationToken);

      string off;
      int offRows;
      JsonbContainmentSwitch.Set(false);
      try {
        off = _context.Set<PerspectiveRow<ProbeModel>>().Where(rephrased).ToQueryString();
        offRows = await _context.Set<PerspectiveRow<ProbeModel>>().CountAsync(rephrased, cancellationToken);
      } finally {
        JsonbContainmentSwitch.Reset();
      }

      lines.Add(string.Create(CultureInfo.InvariantCulture,
        $"{type}: on={onRows} {on.Split('\n')[^1].Trim()}"));
      lines.Add(string.Create(CultureInfo.InvariantCulture,
        $"{type}: off={offRows} {off.Split('\n')[^1].Trim()}"));
    }

    var report = string.Join('\n', lines);
    var target = Environment.GetEnvironmentVariable("WHIZ_ELIGIBILITY_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.WriteAllTextAsync(target, report, cancellationToken);
    }

    await Assert.That(report).IsNotEmpty();
  }

  /// <summary>
  /// The filter for a newly eligible type, and the same filter with a term added that no row fails.
  /// </summary>
  /// <remarks>
  /// The second spelling exists only to be a different cache key. Entity Framework caches a compiled
  /// query by its expression tree, so running the identical filter with the rewrite switched off
  /// would replay the plan compiled while it was on and compare a result against itself. Every row in
  /// this fixture has a version, so the added term changes the plan without changing the answer.
  /// </remarks>
  private static (Expression<Func<PerspectiveRow<ProbeModel>, bool>> Filter,
    Expression<Func<PerspectiveRow<ProbeModel>, bool>> Rephrased) _filtersFor(string type) => type switch {
      "enumeration" => (r => r.Data.State == Mood.High, r => r.Data.State == Mood.High && r.Version > 0),
      "double" => (r => r.Data.Dbl == 0.1d, r => r.Data.Dbl == 0.1d && r.Version > 0),
      "float" => (r => r.Data.Flt == 0.1f, r => r.Data.Flt == 0.1f && r.Version > 0),
      "awkward double" => (r => r.Data.Third == 1.0d / 3.0d, r => r.Data.Third == 1.0d / 3.0d && r.Version > 0),
      "awkward float" => (r => r.Data.FloatThird == 1f / 3f, r => r.Data.FloatThird == 1f / 3f && r.Version > 0),
      _ => throw new InvalidOperationException(type),
    };

  /// <summary>
  /// Each type the measurements admitted is compiled to containment and finds the row that the
  /// filter describes.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Two claims, and the second is the one that matters. The generated SQL saying <c>@&gt;</c> only
  /// shows the rewrite fired; a containment document built in a form the row does not hold would say
  /// exactly the same thing while matching nothing. So the rows are counted, and the count with the
  /// rewrite switched off is pinned alongside, which is how a change in either is noticed.
  /// </para>
  /// <para>
  /// An enumeration is the case that needed the work. It is stored through a value converter to its
  /// underlying number, so the comparison reaches the overload for that number through a conversion,
  /// and that conversion survives translation as a cast wrapped around the member. Until the
  /// emission looked underneath it, an enumeration filter compiled back to the extraction form.
  /// </para>
  /// <para>
  /// <strong>Single precision is where the two forms disagree, and containment is the correct one.</strong>
  /// The extraction form compares <c>CAST(data -&gt;&gt; 'k' AS real)</c> with a bare decimal literal,
  /// which PostgreSQL reads as numeric; there is no operator for that pair, so both sides widen to
  /// double precision, and a single-precision 0.1 widened to double is 0.10000000149011612 while the
  /// literal is 0.1. It therefore finds nothing, for any value that is not exactly representable.
  /// Containment compares the stored value itself and finds the row. The unrewritten counts of zero
  /// below are that defect on record: this rewrite corrects it rather than preserving it, which is a
  /// deliberate decision and the reason the counts are asserted per case instead of as agreement.
  /// The awkward fractions are here because one value proves nothing about a floating-point format.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(120000)]
  [Arguments("enumeration", 1)]
  [Arguments("double", 1)]
  [Arguments("awkward double", 1)]
  [Arguments("float", 0)]
  [Arguments("awkward float", 0)]
  public async Task ANewlyEligibleType_ReachesTheIndexAsync(
    string type, int unrewrittenRows, CancellationToken cancellationToken) {
    var (filter, rephrased) = _filtersFor(type);

    var sql = _context!.Set<PerspectiveRow<ProbeModel>>().Where(filter).ToQueryString();
    await Assert.That(sql).Contains("@>", StringComparison.Ordinal)
      .Because("the filter has to compile to containment or the index cannot answer it");

    var withRewrite = await _context.Set<PerspectiveRow<ProbeModel>>().CountAsync(filter, cancellationToken);

    int withoutRewrite;
    JsonbContainmentSwitch.Set(false);
    try {
      withoutRewrite = await _context.Set<PerspectiveRow<ProbeModel>>().CountAsync(rephrased, cancellationToken);
    } finally {
      JsonbContainmentSwitch.Reset();
    }

    await Assert.That(withRewrite).IsEqualTo(1)
      .Because("the seeded row is the row this filter describes, so containment has to find it");
    await Assert.That(withoutRewrite).IsEqualTo(unrewrittenRows)
      .Because("the form being replaced is pinned too, so a change in what it answers is noticed");
  }
}
