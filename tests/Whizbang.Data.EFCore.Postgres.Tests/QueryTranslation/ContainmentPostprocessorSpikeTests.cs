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
/// The two things reshaping translated SQL depends on, asserted before anything is built on them.
/// </summary>
/// <remarks>
/// <para>
/// Reshaping a query after Entity Framework has translated it is a better place to compile a
/// containment test from than the expression tree is, because the value converter has already been
/// applied to both sides of the comparison and every node carries a type mapping. Both of those are
/// claims about when a translation postprocessor runs, and neither is worth building on unverified.
/// </para>
/// <para>
/// The second claim is the one that can end the idea rather than merely complicate it. A
/// <c>SelectExpression</c> is close to immutable by design, and if its predicate cannot be replaced
/// from this position then there is nothing to reshape and the approach is dead. So it is asserted
/// first and on its own, with a reshape deliberately crude enough that only the mechanism is under
/// test.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
[Category("Shard1")]
public class ContainmentPostprocessorSpikeTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=spike;Username=u;Password=p";

  [SuppressIndexAdvisory("compile-only spike fixture, never run")]
  public class SpikeModel {
    public string Title { get; init; } = string.Empty;
    public int Rank { get; init; }
  }

  private sealed class SpikeDbContext(DbContextOptions<SpikeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<SpikeModel>>(entity => {
        entity.ToTable("wh_per_spike");
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

  private static SpikeDbContext _newContext() =>
    new(new DbContextOptionsBuilder<SpikeDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangTranslatedTreeContainmentSpike()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  /// <summary>
  /// Gate one: a predicate reached after translation can be replaced, and the replacement is what is
  /// sent.
  /// </summary>
  /// <remarks>
  /// The spike marks every predicate it rewrites rather than producing a containment test, so that a
  /// failure here means the mechanism does not work rather than that the containment shape is wrong.
  /// Those are different problems and only the first one is fatal.
  /// </remarks>
  [Test]
  public async Task APredicateCanBeReplacedAfterTranslationAsync() {
    using var db = _newContext();

    var sql = db.Set<PerspectiveRow<SpikeModel>>()
      .Where(r => r.Data.Rank == 7)
      .ToQueryString();

    // Ruled out first: the two observations below both hold trivially if the postprocessor never
    // ran, so its having run is a separate claim and the one to check before the others mean
    // anything.
    await Assert.That(ContainmentPostprocessorSpike.PredicatesReshaped).IsGreaterThan(0)
      .Because("a replaced service that is never asked for would make every other assertion here "
        + "pass while proving nothing");

    await Assert.That(sql).Contains(ContainmentPostprocessorSpike.MARKER, StringComparison.Ordinal)
      .Because("if a SelectExpression predicate cannot be replaced from a translation "
        + "postprocessor, there is nothing to reshape and the approach is dead rather than harder");
  }

  /// <summary>
  /// Gate two: every node the reshape touches already carries a type mapping.
  /// </summary>
  /// <remarks>
  /// This is the claim the value-object case rests on. The earlier attempt on the expression tree
  /// failed precisely because a converted value reached the SQL tree without a mapping and Entity
  /// Framework refused the query; running after the base pass is supposed to be the fix. Asserted by
  /// reshaping and then requiring the query to compile at all, since a missing mapping is exactly
  /// what makes it throw.
  /// </remarks>
  [Test]
  public async Task TypeMappingsAreAssignedBeforeTheReshapeRunsAsync() {
    using var db = _newContext();

    await Assert.That(() => db.Set<PerspectiveRow<SpikeModel>>()
        .Where(r => r.Data.Title == "v")
        .ToQueryString())
      .ThrowsNothing()
      .Because("a node without a type mapping is what made the earlier attempt fail, so the whole "
        + "point of running after the base pass is that there are none left");

    await Assert.That(ContainmentPostprocessorSpike.PredicatesReshaped).IsGreaterThan(0)
      .Because("the flag below starts true, so it says nothing unless the reshape actually ran");

    await Assert.That(ContainmentPostprocessorSpike.EveryVisitedNodeHadAMapping).IsTrue()
      .Because("compiling is necessary but not sufficient: the reshape has to have actually seen "
        + "mapped nodes rather than skipped them");
  }
}
