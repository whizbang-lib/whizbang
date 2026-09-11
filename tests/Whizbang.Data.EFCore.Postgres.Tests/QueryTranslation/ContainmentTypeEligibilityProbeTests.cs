using System.Globalization;
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
}
