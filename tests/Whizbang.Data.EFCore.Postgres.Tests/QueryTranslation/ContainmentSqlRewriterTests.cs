using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Reshaping what Entity Framework translated, rather than rewriting the tree beforehand.
/// </summary>
/// <remarks>
/// <para>
/// The two mechanisms have to agree about which filters become containment tests, and the matrix
/// covers that across a thousand shapes. What is asserted here is what is specific to this one: that
/// it acts only when selected, that it sees a value converter's effect where the other mechanism
/// cannot, and that it leaves a date alone because its stored rendering is not PostgreSQL's.
/// </para>
/// <para>
/// A value converter is the case worth reading. On the tree, the compared value has to be converted
/// by hand to reach the stored type, and the converted constant arrives at the SQL tree without a
/// type mapping, so Entity Framework refuses the query. After translation the converter has already
/// been applied to both sides, so the document is built in the stored form with nothing to convert.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
[Category("Shard1")]
[NotInParallel("ContainmentMode")]
public class ContainmentSqlRewriterTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=reshape;Username=u;Password=p";

  public enum Grade { Low = 0, High = 1 }

  [SuppressIndexAdvisory("compile-only fixture, never run")]
  public class ReshapeModel {
    public string Title { get; init; } = string.Empty;
    public int Rank { get; init; }
    public Guid Reference { get; init; }
    public Grade Level { get; init; }
    public DateTime OccurredAt { get; init; }
    public string? Maybe { get; init; }

    /// <summary>Stored through a converter, which is what the other mechanism cannot handle.</summary>
    public int Converted { get; init; }

    public NestedReshape Inner { get; init; } = new();
  }

  public class NestedReshape {
    public string City { get; init; } = string.Empty;
  }

  private sealed class ReshapeDbContext(DbContextOptions<ReshapeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<ReshapeModel>>(entity => {
        entity.ToTable("wh_per_reshape");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.ComplexProperty(p => p.Inner);
          d.Property(p => p.Converted).HasConversion<string>();
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
    }
  }

  private static readonly DbContextOptions<ReshapeDbContext> _options =
    new DbContextOptionsBuilder<ReshapeDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangContainmentReshape()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static ReshapeDbContext _newContext() => new(_options);

  [Before(Test)]
  public void SelectTheReshape() => JsonbContainmentSwitch.SetMode(ContainmentMode.TranslatedTree);

  [After(Test)]
  public void RestoreDefault() => JsonbContainmentSwitch.Reset();

  private static string _sql(Func<IQueryable<PerspectiveRow<ReshapeModel>>,
    IQueryable<PerspectiveRow<ReshapeModel>>> shape) {
    using var db = _newContext();
    return shape(db.Set<PerspectiveRow<ReshapeModel>>()).ToQueryString();
  }

  /// <summary>An equality on a JSON member becomes a containment test.</summary>
  [Test]
  [Arguments("string")]
  [Arguments("int")]
  [Arguments("guid")]
  [Arguments("enum")]
  [Arguments("nested")]
  public async Task AnEqualityBecomesContainmentAsync(string kind) {
    var reference = Guid.NewGuid();

    var sql = kind switch {
      "string" => _sql(rows => rows.Where(r => r.Data.Title == "v")),
      "int" => _sql(rows => rows.Where(r => r.Data.Rank == 7)),
      "guid" => _sql(rows => rows.Where(r => r.Data.Reference == reference)),
      "enum" => _sql(rows => rows.Where(r => r.Data.Level == Grade.High)),
      "nested" => _sql(rows => rows.Where(r => r.Data.Inner.City == "here")),
      _ => throw new InvalidOperationException(kind),
    };

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal);
    await Assert.That(sql).Contains("jsonb_build_object", StringComparison.Ordinal);
  }

  /// <summary>
  /// A converted member is reshaped correctly, which is the whole reason for this mechanism.
  /// </summary>
  /// <remarks>
  /// The converter stores an int as text, so the document must be built from the text. On the tree
  /// this case had to stand down to avoid building a numeric document that matches nothing; here the
  /// value has already been converted, so there is nothing to get wrong.
  /// </remarks>
  [Test]
  public async Task AConvertedMemberIsReshapedAsync() {
    var sql = _sql(rows => rows.Where(r => r.Data.Converted == 7));

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal)
      .Because("after translation the converter has been applied to both sides, so the document is "
        + "built in the stored form without anything having to be converted by hand");
    await Assert.That(sql).Contains("jsonb_build_object", StringComparison.Ordinal);
  }

  /// <summary>
  /// A date is left alone, because its stored rendering is the serializer's and not PostgreSQL's.
  /// </summary>
  /// <remarks>
  /// A deliberate difference from the other mechanism, which renders the instant in SQL to match.
  /// Writing that rendering here would mean building exactly what the canonical stored format is
  /// going to delete, so the filter keeps the extraction form: correct rows, no index.
  /// THIS ASSERTION IS EXPECTED TO FAIL when dates are stored as a number, at which point a date
  /// needs nothing special from either mechanism and this case should be deleted rather than inverted.
  /// </remarks>
  [Test]
  public async Task ADateIsLeftAloneAsync() {
    var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    var sql = _sql(rows => rows.Where(r => r.Data.OccurredAt == when));

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal);
  }

  /// <summary>Each place the two forms disagree is left alone here too.</summary>
  [Test]
  [Arguments("null comparison")]
  [Arguments("negation")]
  [Arguments("inequality")]
  [Arguments("range")]
  public async Task WhereTheFormsDisagreeItStandsDownAsync(string shape) {
    var sql = shape switch {
      "null comparison" => _sql(rows => rows.Where(r => r.Data.Maybe == null)),
      "negation" => _sql(rows => rows.Where(r => !(r.Data.Rank == 7))),
      "inequality" => _sql(rows => rows.Where(r => r.Data.Rank != 7)),
      "range" => _sql(rows => rows.Where(r => r.Data.Rank > 7)),
      _ => throw new InvalidOperationException(shape),
    };

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal);
  }

  /// <summary>A comparison in a projection is not a filter and is not reshaped.</summary>
  [Test]
  public async Task AProjectionIsNotReshapedAsync() {
    using var db = _newContext();

    var sql = db.Set<PerspectiveRow<ReshapeModel>>()
      .Select(r => new { Matches = r.Data.Rank == 7 })
      .ToQueryString();

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal)
      .Because("a projection surfaces the comparison's own value, where an absent key reading as "
        + "null rather than as false becomes visible to the caller");
  }

  /// <summary>Both operand orders reshape, since which side holds the member is incidental.</summary>
  [Test]
  public async Task EitherOperandOrderReshapesAsync() {
    await Assert.That(_sql(rows => rows.Where(r => 7 == r.Data.Rank)))
      .Contains("@>", StringComparison.Ordinal);
  }

  /// <summary>
  /// It does nothing unless its mode is selected, so the two mechanisms never both act.
  /// </summary>
  /// <remarks>
  /// Each case filters on a different value, and that is not incidental. Entity Framework caches a
  /// compiled query by its shape, so reusing one another case already compiled would return the plan
  /// built under whatever mode was in force then and report the mode being tested as having no
  /// effect. The switch documents the same thing for a deployment: flipping it changes queries
  /// compiled afterwards, not ones already cached.
  /// </remarks>
  [Test]
  [Arguments(ContainmentMode.Off, 8001)]
  [Arguments(ContainmentMode.ExpressionTree, 8002)]
  public async Task ItActsOnlyWhenSelectedAsync(ContainmentMode mode, int distinctValue) {
    JsonbContainmentSwitch.SetMode(mode);

    // The tree rewriter is not registered on this context, so nothing should compile to containment
    // under either of these modes: under Off because nothing is in force, and under ExpressionTree
    // because the mechanism that mode selects is not present here.
    await Assert.That(_sql(rows => rows.Where(r => r.Data.Rank == distinctValue)))
      .DoesNotContain("@>", StringComparison.Ordinal)
      .Because("both mechanisms acting would compile one filter twice");
  }
}
