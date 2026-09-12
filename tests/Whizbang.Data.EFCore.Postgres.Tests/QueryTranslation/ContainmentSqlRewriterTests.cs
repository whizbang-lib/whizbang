using System.Diagnostics.CodeAnalysis;
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
// Serialized against every other class that touches the containment switch, which is process
// static. The matrix selects a mechanism per case, so a class running beside it would have the
// mode changed underneath it and would assert the other mechanism's output. One key for all of
// them rather than a key per concern, because a second key is exactly how this went wrong.
[NotInParallel("EFCorePostgresTests")]
public class ContainmentSqlRewriterTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=reshape;Username=u;Password=p";

  public enum Grade { Low = 0, High = 1 }

  [SuppressIndexAdvisory("compile-only fixture, never run")]
  public class ReshapeModel {
    public string Title { get; init; } = string.Empty;
    public int Rank { get; init; }

    /// <summary>
    /// Carries a btree of its own, which is what the reshape has to stand down for.
    /// </summary>
    /// <remarks>
    /// A field of its own rather than reusing one above, because the registry is process static and
    /// additive with no reset: registering a table index against a key another case filters on would
    /// change that case's expected destination for the rest of the run.
    /// </remarks>
    [Indexed]
    public int IndexedRank { get; init; }
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

  /// <summary>
  /// Takes the untyped options so both registration overloads can be driven through one mapping.
  /// </summary>
  private sealed class ReshapeDbContext(DbContextOptions options) : DbContext(options) {
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
  [SuppressMessage("Readability", "RCS1068:Simplify logical negation",
    Justification = "The negated spelling is the subject. !(a == b) builds Not(Equal) and a != b "
      + "builds NotEqual, which take different paths through the rewriter, so simplifying it would "
      + "delete the case rather than tidy it.")]
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
    await using var db = _newContext();

    var sql = db.Set<PerspectiveRow<ReshapeModel>>()
      .Select(r => new { Matches = r.Data.Rank == 7 })
      .ToQueryString();

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal)
      .Because("a projection surfaces the comparison's own value, where an absent key reading as "
        + "null rather than as false becomes visible to the caller");
  }

  /// <summary>Both operand orders reshape, since which side holds the member is incidental.</summary>
  [Test]
  [SuppressMessage("Readability", "RCS1098:Constant values should be placed on right side of comparisons",
    Justification = "Reading the member from either operand is what this asserts; moving the constant "
      + "to the right removes the case.")]
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

  /// <summary>
  /// A field with a btree of its own keeps the extraction form here too.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The same decision as under the tree rewrite, reached from different information. That mechanism
  /// knows the model being queried; by the time this one runs the tree is gone and what it has is the
  /// column's table, so the registry is asked by table name instead. Both have to answer the same way
  /// or switching mechanism would silently change which index a query uses.
  /// </para>
  /// <para>
  /// Registered here rather than in a fixture because the registry is what the generated startup code
  /// writes to, and registering exactly that is what makes this the real path.
  /// </para>
  /// </remarks>
  [Test]
  public async Task ABtreeIndexedFieldIsNotReshapedAsync() {
    JsonIndexRegistry.RegisterForTable("wh_per_reshape", nameof(ReshapeModel.IndexedRank), IndexKinds.Btree);

    var sql = _sql(rows => rows.Where(r => r.Data.IndexedRank == 11));

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal)
      .Because("reshaping this would send the planner to the document index and leave the field's own "
        + "index unused, which is the situation the reshape exists to fix rather than to cause");
    await Assert.That(sql).Contains("data ->> 'IndexedRank'", StringComparison.Ordinal);
  }

  /// <summary>
  /// The reshape can be registered through the untyped options builder, which is how an application
  /// wires it.
  /// </summary>
  /// <remarks>
  /// The rest of this class configures a typed builder, because a test owns its context type. An
  /// application calls <c>AddDbContext(options =&gt; ...)</c> and the lambda is handed the untyped one,
  /// so that overload is the one real callers use and it has to put the reshape in force just the same.
  /// </remarks>
  [Test]
  public async Task TheUntypedOverloadRegistersTheReshapeAsync() {
    var builder = new DbContextOptionsBuilder();

    var returned = builder
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangContainmentReshape()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    await Assert.That(returned).IsSameReferenceAs(builder)
      .Because("it returns the builder so it can sit in the middle of a configuration chain");

    await using var db = new ReshapeDbContext(returned.Options);

    var sql = db.Set<PerspectiveRow<ReshapeModel>>()
      .Where(r => r.Data.Rank == 9001)
      .ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal)
      .Because("registering through the untyped builder has to put the reshape in force, or the "
        + "overload every application actually calls does nothing");
  }
}
