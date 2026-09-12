using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Compatibility;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Turning a comparison Entity Framework refuses to translate into one it accepts.
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework will not translate <c>Equals</c> with a <see cref="StringComparison"/> argument
/// at all, and its refusal is principled: the comparison would have to compile to <c>=</c> on text,
/// whose meaning follows the column's collation, and that is not what any particular
/// <c>StringComparison</c> asks for. So it declines rather than guess.
/// </para>
/// <para>
/// For <see cref="StringComparison.Ordinal"/> specifically the answer is not a guess. Ordinal
/// comparison is byte equality, which is what <c>=</c> means under a deterministic collation, so the
/// call can be replaced with <c>==</c> and translated. That is the one thing this framework does
/// beyond Entity Framework, and it only works before translation, because afterwards the refusal has
/// already been raised.
/// </para>
/// <para>
/// Which is why it lives on its own rather than inside the containment rewrite. It normalizes an
/// untranslatable shape into a translatable one and stops; whatever compiles the result into a
/// containment test, if anything, then sees an ordinary equality and needs no special case. That
/// keeps the capability working under either mechanism and under neither.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
[Category("Shard1")]
// Serialized against every other class that touches the containment switch, which is process
// static. The matrix selects a mechanism per case, so a class running beside it would have the
// mode changed underneath it and would assert the other mechanism's output. One key for all of
// them rather than a key per concern, because a second key is exactly how this went wrong.
[NotInParallel("EFCorePostgresTests")]
public class OrdinalEqualsRewriterTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=ordinal;Username=u;Password=p";

  [SuppressIndexAdvisory("compile-only fixture, never run")]
  public class OrdinalModel {
    public string Code { get; init; } = string.Empty;
  }

  private sealed class OrdinalDbContext(DbContextOptions<OrdinalDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<OrdinalModel>>(entity => {
        entity.ToTable("wh_per_ordinal");
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
    }
  }

  private static readonly DbContextOptions<OrdinalDbContext> _options =
    new DbContextOptionsBuilder<OrdinalDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      // The normalizing pass rides on the perspective query interceptor, which this registers. A
      // real application always has it, because the generated configuration emits it, but it is
      // worth being explicit here: without it the query is refused by Entity Framework and the
      // capability is simply absent.
      .UseWhizbangPhysicalFields()
      .UseWhizbangContainmentReshape()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  [Before(Test)]
  public void SelectTheReshape() => JsonbContainmentSwitch.SetMode(ContainmentMode.TranslatedTree);

  [After(Test)]
  public void RestoreDefault() => JsonbContainmentSwitch.Reset();

  /// <summary>
  /// An ordinal comparison translates, which Entity Framework on its own will not do.
  /// </summary>
  /// <remarks>
  /// Asserted through the reshape mechanism deliberately. The tree rewrite already handled this case
  /// as part of building containment, so proving it under the other mechanism is what shows the
  /// capability now belongs to a pass of its own and survives a change of mechanism.
  /// </remarks>
  [Test]
  [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison",
    Justification = "The spelling is the subject of the test.")]
  public async Task AnOrdinalComparisonTranslatesAsync() {
    await using var db = new OrdinalDbContext(_options);

    var sql = db.Set<PerspectiveRow<OrdinalModel>>()
      .Where(r => r.Data.Code.Equals("v", StringComparison.Ordinal))
      .ToQueryString();

    await Assert.That(sql).Contains("data", StringComparison.Ordinal)
      .Because("Entity Framework refuses this overload outright, so translating at all is the point");
  }

  /// <summary>
  /// Having been normalized, it is compiled into containment like any other equality.
  /// </summary>
  /// <remarks>
  /// This is what the split is for. The normalizing pass knows nothing about containment, and the
  /// containment pass knows nothing about <c>StringComparison</c>, yet the two compose.
  /// </remarks>
  [Test]
  [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison",
    Justification = "The spelling is the subject of the test.")]
  public async Task AndIsThenIndexedLikeAnyOtherEqualityAsync() {
    await using var db = new OrdinalDbContext(_options);

    var sql = db.Set<PerspectiveRow<OrdinalModel>>()
      .Where(r => r.Data.Code.Equals("indexed", StringComparison.Ordinal))
      .ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal)
      .Because("the normalizing pass leaves an ordinary equality, which the reshape indexes without "
        + "knowing a StringComparison was ever involved");
  }

  /// <summary>
  /// A comparison that is not ordinal is still refused, because its meaning is not byte equality.
  /// </summary>
  /// <remarks>
  /// The boundary of the capability, and the reason it is a capability rather than a workaround:
  /// translating a case-insensitive comparison to <c>=</c> would claim something untrue.
  /// </remarks>
  [Test]
  [Arguments(nameof(StringComparison.OrdinalIgnoreCase))]
  [Arguments(nameof(StringComparison.CurrentCulture))]
  [Arguments(nameof(StringComparison.InvariantCultureIgnoreCase))]
  public async Task ANonOrdinalComparisonIsStillRefusedAsync(string comparison) {
    await using var db = new OrdinalDbContext(_options);

    var comparisonType = Enum.Parse<StringComparison>(comparison);

    await Assert.That(() => db.Set<PerspectiveRow<OrdinalModel>>()
        .Where(r => r.Data.Code.Equals("v", comparisonType))
        .ToQueryString())
      .Throws<InvalidOperationException>()
      .Because("only ordinal comparison is byte equality; anything else would be a claim the SQL "
        + "does not support");
  }

  /// <summary>
  /// It works with no containment mechanism in force, because it is not part of either.
  /// </summary>
  [Test]
  [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison",
    Justification = "The spelling is the subject of the test.")]
  public async Task ItWorksWithContainmentTurnedOffAsync() {
    JsonbContainmentSwitch.SetMode(ContainmentMode.Off);

    await using var db = new OrdinalDbContext(_options);

    var sql = db.Set<PerspectiveRow<OrdinalModel>>()
      .Where(r => r.Data.Code.Equals("unindexed", StringComparison.Ordinal))
      .ToQueryString();

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal);
    await Assert.That(sql).Contains("data ->> 'Code'", StringComparison.Ordinal)
      .Because("normalizing a shape Entity Framework refuses is a separate service from indexing it, "
        + "so turning the indexing off must not take the translation with it");
  }
}
