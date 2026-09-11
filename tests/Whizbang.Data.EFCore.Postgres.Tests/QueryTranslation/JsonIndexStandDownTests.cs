using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// A field that carries its own index is not compiled into a containment test.
/// </summary>
/// <remarks>
/// <para>
/// This is the rule that keeps the two index kinds from cancelling each other out. Containment is
/// answered from the GIN index over the whole document; a declared index is a btree over one
/// extraction. Both are real indexes, and the planner will happily use the document one for a
/// rewritten equality filter, leaving the field's own index untouched. A consumer who declares an
/// index and sees it never used is back to the situation this whole area exists to fix, except now
/// they have paid for the index as well.
/// </para>
/// <para>
/// Standing down is also the faster choice rather than merely the safer one. A single-column btree
/// equality probe reads one index and goes to the heap; containment reads the document index and then
/// rechecks every candidate row, because the default operator class stores keys and values as
/// separate tokens and cannot confirm on its own that they belong together.
/// </para>
/// <para>
/// Nothing about the answer changes either way, which is exactly why this needs a test. Both forms
/// return the same rows, so a regression here is invisible except in a plan.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class JsonIndexStandDownTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=standdown;Username=u;Password=p";

  [SuppressIndexAdvisory("compile-only fixture, never run")]
  public class StandDownModel {
    /// <summary>Declared as carrying its own index, so equality must not be rewritten.</summary>
    [JsonIndexed]
    public int Rank { get; init; }

    /// <summary>Declared for substring matching only, which leaves equality to containment.</summary>
    [JsonIndexed(JsonIndexKind.Trigram)]
    public string Title { get; init; } = string.Empty;

    /// <summary>Undeclared, so equality reaches the document index as before.</summary>
    public string Plain { get; init; } = string.Empty;
  }

  private sealed class StandDownDbContext(DbContextOptions<StandDownDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<StandDownModel>>(entity => {
        entity.ToTable("wh_per_standdown");
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

  private static readonly DbContextOptions<StandDownDbContext> _options =
    new DbContextOptionsBuilder<StandDownDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static StandDownDbContext _newContext() {
    // What the generator emits at startup for a model carrying [JsonIndexed].
    JsonIndexRegistry.Register<StandDownModel>("Rank", JsonIndexKind.Btree);
    JsonIndexRegistry.Register<StandDownModel>("Title", JsonIndexKind.Trigram);

    return new StandDownDbContext(_options);
  }

  /// <summary>
  /// A field with a btree of its own keeps the extraction form, so its index is the one used.
  /// </summary>
  [Test]
  public async Task ABtreeIndexedField_IsNotCompiledToContainmentAsync() {
    using var db = _newContext();

    var sql = db.Set<PerspectiveRow<StandDownModel>>()
      .Where(r => r.Data.Rank == 7)
      .ToQueryString();

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal)
      .Because("rewriting this would send the planner to the document index and leave the field's own "
        + "index unused, which is the situation the rewrite exists to fix rather than to cause");
    await Assert.That(sql).Contains("data ->> 'Rank'", StringComparison.Ordinal);
  }

  /// <summary>
  /// A trigram declaration does not stand equality down, because a trigram index cannot answer an
  /// equality any better than containment can.
  /// </summary>
  /// <remarks>
  /// The distinction matters: standing down is justified only where the field has an index that
  /// serves the filter being written. A trigram index serves substring matching, so an equality
  /// filter on such a field is still better off reaching the document index than scanning.
  /// </remarks>
  [Test]
  public async Task ATrigramOnlyField_StillReachesContainmentForEqualityAsync() {
    using var db = _newContext();

    var sql = db.Set<PerspectiveRow<StandDownModel>>()
      .Where(r => r.Data.Title == "v")
      .ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal)
      .Because("a trigram index does not answer an equality, so there is nothing to stand down for");
  }

  /// <summary>An undeclared field behaves exactly as it did before any of this existed.</summary>
  [Test]
  public async Task AnUndeclaredField_IsUnaffectedAsync() {
    using var db = _newContext();

    var sql = db.Set<PerspectiveRow<StandDownModel>>()
      .Where(r => r.Data.Plain == "v")
      .ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal);
  }

  /// <summary>
  /// The registry answers about the property it was asked about, and not about a same-named property
  /// on another model.
  /// </summary>
  [Test]
  public async Task TheRegistryIsPerModelAsync() {
    JsonIndexRegistry.Register<StandDownModel>("Rank", JsonIndexKind.Btree);

    await Assert.That(JsonIndexRegistry.HasBtree(typeof(StandDownModel), "Rank")).IsTrue();
    await Assert.That(JsonIndexRegistry.HasBtree(typeof(StandDownModel), "Plain")).IsFalse();
    await Assert.That(JsonIndexRegistry.HasBtree(typeof(JsonIndexStandDownTests), "Rank")).IsFalse()
      .Because("two models can carry the same property name and mean different things by it");
  }

  /// <summary>
  /// A repeated declaration adds to the kinds rather than replacing them, which is what makes the
  /// attribute repeatable rather than merely tolerated.
  /// </summary>
  [Test]
  public async Task RepeatedRegistrationsCombineAsync() {
    JsonIndexRegistry.Register<StandDownModel>("Combined", JsonIndexKind.Btree);
    JsonIndexRegistry.Register<StandDownModel>("Combined", JsonIndexKind.Trigram);

    await Assert.That(JsonIndexRegistry.HasBtree(typeof(StandDownModel), "Combined")).IsTrue();
    await Assert.That(JsonIndexRegistry.Kinds(typeof(StandDownModel), "Combined"))
      .IsEqualTo(JsonIndexKind.Btree | JsonIndexKind.Trigram);
  }
}
