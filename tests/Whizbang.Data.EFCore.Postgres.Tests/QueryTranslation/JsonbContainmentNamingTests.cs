using System.Diagnostics.CodeAnalysis;
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
/// The containment key has to be the name the document actually uses, not the name the property has
/// in C#.
/// </summary>
/// <remarks>
/// <para>
/// A containment test names its key literally, so if the rewrite spelled the CLR property name while
/// the serializer wrote something else, the predicate would compile, run, use the index, and match
/// nothing. That is the worst failure available here: silent, fast and wrong.
/// </para>
/// <para>
/// The framework renames JSON properties itself, so this is not hypothetical. The generated scope
/// mapping declares <c>HasJsonPropertyName("ex")</c> for the extensions collection.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "These locals are captured into an expression tree on purpose. A const local is inlined by the compiler as a literal, which turns the parameterized filter under test into a constant one: in the matrix that collapses every /param row onto its /const twin, and elsewhere it stops exercising the captured-parameter path altogether.")]
public class JsonbContainmentNamingTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=naming;Username=u;Password=p";

  [SuppressIndexAdvisory("compiled, never run")]
  public class RenamedModel {
    /// <summary>Stored under a different key than its CLR name.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Stored under its own name, as a control.</summary>
    public Guid Owner { get; init; }

    /// <summary>A nested object whose own key is renamed, holding a member whose key is renamed too.</summary>
    public InnerModel Inner { get; init; } = new();
  }

  public class InnerModel {
    public string City { get; init; } = string.Empty;
  }

  private sealed class NamingDbContext(DbContextOptions<NamingDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<RenamedModel>>(entity => {
        entity.ToTable("wh_per_naming");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");

        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.Property(p => p.Title).HasJsonPropertyName("ttl");
          d.ComplexProperty(p => p.Inner, i => {
            i.HasJsonPropertyName("inr");
            i.Property(p => p.City).HasJsonPropertyName("cty");
          });
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

  private static readonly DbContextOptions<NamingDbContext> _options =
    new DbContextOptionsBuilder<NamingDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static readonly NamingDbContext _db = new(_options);
  private static IQueryable<PerspectiveRow<RenamedModel>> _rows => _db.Set<PerspectiveRow<RenamedModel>>();

  /// <summary>
  /// The containment document is keyed by the stored name. The CLR name must not appear anywhere in
  /// the statement, because if it did the predicate would match nothing.
  /// </summary>
  [Test]
  public async Task RenamedProperty_UsesTheStoredKeyAsync() {
    var title = "v";
    var sql = _rows.Where(x => x.Data.Title == title).ToQueryString();

    await Assert.That(sql).Contains("jsonb_build_object('ttl'", StringComparison.Ordinal);
    await Assert.That(sql).DoesNotContain("'Title'", StringComparison.Ordinal);
  }

  /// <summary>A property that was not renamed still uses its own name, so the lookup is not blanket.</summary>
  [Test]
  public async Task UnrenamedProperty_UsesItsOwnNameAsync() {
    var owner = Guid.NewGuid();
    var sql = _rows.Where(x => x.Data.Owner == owner).ToQueryString();

    await Assert.That(sql).Contains("jsonb_build_object('Owner'", StringComparison.Ordinal);
  }

  /// <summary>
  /// Every segment of a nested path is the key the document actually uses.
  /// </summary>
  /// <remarks>
  /// Worth reading with the extraction form beside it. Entity Framework applies
  /// <c>HasJsonPropertyName</c> to the scalar here but not to the complex property that holds it, so
  /// the document is keyed <c>{Inner,cty}</c> rather than <c>{inr,cty}</c>. The containment form says
  /// the same thing, because both are built from the provider's own path into the document rather
  /// than from CLR property names. Whether a particular rename takes effect is Entity Framework's
  /// business; agreeing with it is ours.
  /// </remarks>
  [Test]
  public async Task RenamedNestedPath_UsesTheStoredKeyAtEveryLevelAsync() {
    var city = "here";
    var sql = _rows.Where(x => x.Data.Inner.City == city).ToQueryString();

    await Assert.That(sql).Contains("jsonb_build_object('Inner', jsonb_build_object('cty'", StringComparison.Ordinal);
    await Assert.That(sql).DoesNotContain("'City'", StringComparison.Ordinal);
  }

  /// <summary>
  /// The containment key agrees with the key the extraction form would have read, which is the
  /// property that makes the rewrite safe: both name the same place in the document.
  /// </summary>
  [Test]
  public async Task ContainmentKey_AgreesWithTheExtractionKeyAsync() {
    var title = "v";

    JsonbContainmentSwitch.Set(false);
    string extraction;
    try {
      // A different member, so the compiled-query cache cannot serve the earlier shape.
      extraction = _db.Set<PerspectiveRow<RenamedModel>>()
        .Where(x => x.Data.Title == title && x.Version > 0)
        .ToQueryString();
    } finally {
      JsonbContainmentSwitch.Reset();
    }

    await Assert.That(extraction).Contains("'ttl'", StringComparison.Ordinal);
    await Assert.That(extraction).DoesNotContain("'Title'", StringComparison.Ordinal);
  }

  /// <summary>The projected dialect keys the same way, since it reads the same document.</summary>
  [Test]
  public async Task ProjectedModel_UsesTheStoredKeyAsync() {
    var title = "v";
    var sql = _rows.Select(r => r.Data).Where(m => m.Title == title).ToQueryString();

    await Assert.That(sql).Contains("jsonb_build_object('ttl'", StringComparison.Ordinal);
    await Assert.That(sql).DoesNotContain("'Title'", StringComparison.Ordinal);
  }

  /// <summary>
  /// Records what the extraction form reads for the same nested path, which is the authority on what
  /// key the document actually uses.
  /// </summary>
  [Test]
  public async Task NestedPath_ExtractionAndContainmentNameTheSamePlaceAsync() {
    var city = "here";

    JsonbContainmentSwitch.Set(false);
    string extraction;
    try {
      extraction = _db.Set<PerspectiveRow<RenamedModel>>()
        .Where(x => x.Data.Inner.City == city && x.Version > 0)
        .ToQueryString();
    } finally {
      JsonbContainmentSwitch.Reset();
    }

    var containment = _rows.Where(x => x.Data.Inner.City == city).ToQueryString();

    await File.WriteAllTextAsync(
      Environment.GetEnvironmentVariable("WHIZ_NAMING_DUMP") ?? Path.Combine(Path.GetTempPath(), "naming.txt"),
      "EXTRACTION:\n" + extraction + "\n\nCONTAINMENT:\n" + containment + "\n");

    // The invariant that makes the rewrite safe: whatever keys the extraction reads, the containment
    // document names the same ones. Asserted by reading them out of the extraction rather than by
    // restating them, so this keeps holding if Entity Framework changes how a rename is applied.
    var path = extraction[(extraction.IndexOf("#>> '{", StringComparison.Ordinal) + 6)..];
    path = path[..path.IndexOf('}', StringComparison.Ordinal)];
    var keys = path.Split(',');

    await Assert.That(keys).Count().IsEqualTo(2);
    foreach (var key in keys) {
      await Assert.That(containment).Contains($"'{key}'", StringComparison.Ordinal);
    }
  }

  [After(Test)]
  public void RestoreSwitch() => JsonbContainmentSwitch.Reset();
}
