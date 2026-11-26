// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A context built without the Whizbang options extension, as a consumer-registered or hand-built lens context often
/// is, still maps a dictionary member as a value with its stored form, through the generated configuration (#1911).
/// </summary>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/DictionaryMemberConvention.cs</code-under-test>
[Category("Shard2")]
public class DictionaryMemberConventionTests {
  private const string OFFLINE = "Host=localhost;Database=probe;Username=u;Password=p";

  static DictionaryMemberConventionTests() =>
    // A member registered with a type no form answers for: left alone rather than mapped wrong.
    DictionaryMemberForms.RegisterMember(typeof(DictionaryMemberSection), "Ghost", typeof(SortedDictionary<int, int>));

  /// <summary>The perspective mapped as the generated configuration maps it, then the convention, as it calls it.</summary>
  private sealed class PlainDbContext(DbContextOptions<PlainDbContext> options, bool ignoreNestedTotals) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<DictionaryMemberModel>>(entity => {
        entity.ToTable("wh_per_dictionary_plain");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          if (ignoreNestedTotals) {
            d.ComplexProperty(m => m.Section, s => s.Ignore(x => x.Totals));
          }
        });
        // Spread over columns rather than stored as a document: nothing in it is a document member.
        entity.ComplexProperty(e => e.Metadata);
        entity.Ignore(e => e.Scope);
      });

      DictionaryMemberConvention.Apply(modelBuilder);
    }

    // Each variant is its own model.
    public sealed class Cache : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory {
      public object Create(DbContext context, bool designTime) => (context.GetType(), ((PlainDbContext)context)._ignore, designTime);
    }

    private bool _ignore => ignoreNestedTotals;
  }

  private static PlainDbContext _context(bool ignoreNestedTotals = false) => new(
    new DbContextOptionsBuilder<PlainDbContext>()
      .UseNpgsql(OFFLINE)
      .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, PlainDbContext.Cache>()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options,
    ignoreNestedTotals);

  private static IComplexType _data(IModel model) =>
    model.FindEntityType(typeof(PerspectiveRow<DictionaryMemberModel>))!.FindComplexProperty("Data")!.ComplexType;

  [Test]
  public async Task WithoutTheOptionsExtension_EveryDictionaryIsAValueWithItsStoredFormAsync() {
    await using var context = _context();
    var data = _data(context.Model);

    await Assert.That(data.FindProperty("Counts")!.GetTypeMapping()).IsTypeOf<DictionaryMemberTypeMapping>();
    await Assert.That(data.FindProperty("Notes")!.GetTypeMapping()).IsTypeOf<DictionaryMemberTypeMapping>();
    await Assert.That(data.FindComplexProperty("Section")!.ComplexType.FindProperty("Totals")!.GetTypeMapping())
      .IsTypeOf<DictionaryMemberTypeMapping>().Because("a dictionary one level down is mapped too");
    await Assert.That(data.FindComplexProperty("Sections")!.ComplexType.FindProperty("Totals")!.GetTypeMapping())
      .IsTypeOf<DictionaryMemberTypeMapping>().Because("and one inside each element of a collection");
    await Assert.That(data.FindComplexProperty("Section")!.ComplexType.FindProperty("Ghost")).IsNull()
      .Because("a member whose type has no form is not mapped");
  }

  [Test]
  public async Task AMemberTheConfigurationIgnored_StaysIgnoredAsync() {
    await using var context = _context(ignoreNestedTotals: true);
    var section = _data(context.Model).FindComplexProperty("Section")!.ComplexType;

    await Assert.That(section.FindProperty("Totals")).IsNull();
  }

  [Test]
  public async Task AMemberAlreadyMapped_IsLeftAsItWasAsync() {
    var modelBuilder = new ModelBuilder();
    modelBuilder.Entity<PerspectiveRow<DictionaryMemberModel>>(entity => {
      entity.HasKey(e => e.Id);
      entity.ComplexProperty(e => e.Data, d => {
        d.ToJson("data");
        d.Property(m => m.Counts).Metadata.SetTypeMapping(DictionaryMemberTypeMapping.For(typeof(Dictionary<string, int>))!);
      });
      entity.Ignore(e => e.Metadata);
      entity.Ignore(e => e.Scope);
    });
    var counts = modelBuilder.Model.FindEntityType(typeof(PerspectiveRow<DictionaryMemberModel>))!
      .FindComplexProperty("Data")!.ComplexType.FindProperty("Counts")!;

    DictionaryMemberConvention.Apply(modelBuilder);

    await Assert.That(modelBuilder.Model.FindEntityType(typeof(PerspectiveRow<DictionaryMemberModel>))!
      .FindComplexProperty("Data")!.ComplexType.FindProperty("Counts")).IsSameReferenceAs(counts);
    await Assert.That(() => DictionaryMemberConvention.Apply(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheMemberRegistry_RecordsEachMemberOnceAsync() {
    DictionaryMemberForms.RegisterMember(typeof(DictionaryMemberSection), "Totals", typeof(Dictionary<string, decimal>));

    await Assert.That(DictionaryMemberForms.MembersOf(typeof(DictionaryMemberSection)).Count(m => m.PropertyName == "Totals")).IsEqualTo(1);
    await Assert.That(DictionaryMemberForms.MembersOf(typeof(DictionaryMemberConventionTests))).IsEmpty();
    await Assert.That(() => DictionaryMemberForms.RegisterMember(null!, "x", typeof(int))).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryMemberForms.RegisterMember(typeof(int), "", typeof(int))).Throws<ArgumentException>();
    await Assert.That(() => DictionaryMemberForms.RegisterMember(typeof(int), "x", null!)).Throws<ArgumentNullException>();
    await Assert.That(() => DictionaryMemberForms.MembersOf(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// A promoted jsonb column of a dictionary is written in the stored form its mapping gives it, whether the column
  /// names a converter or only its type mapping does, so every writer of the column agrees.
  /// </summary>
  [Test]
  public async Task APromotedDictionaryColumn_IsWrittenInItsMappingsStoredFormAsync() {
    var modelBuilder = new ModelBuilder();
    var entity = modelBuilder.Entity<DictionaryMemberLine>();
    entity.HasKey(e => e.Name);
    var mapped = entity.Property<Dictionary<string, int>>("counts").HasColumnType("jsonb").Metadata;
    mapped.SetTypeMapping(DictionaryMemberTypeMapping.For(typeof(Dictionary<string, int>))!);
    var unmapped = entity.Property<Dictionary<string, int>>("raw").HasColumnType("jsonb").Metadata;
    var counts = new Dictionary<string, int> { ["a"] = 1 };

    var fromMapping = BaseUpsertStrategy.PhysicalColumnValue("counts", mapped, counts);
    var withoutOne = BaseUpsertStrategy.PhysicalColumnValue("raw", unmapped, counts);

    await Assert.That(fromMapping.IsJsonDocument).IsTrue();
    await Assert.That(fromMapping.Value).IsEqualTo("""[{"Key":"a","Value":1}]""");
    await Assert.That(withoutOne.IsJsonDocument).IsFalse()
      .Because("a column with no converter anywhere is bound as the value itself, as before");
  }

  /// <summary>A column-split complex type holding a nested one: the nested type is a document only if it maps to JSON.</summary>
  public sealed class SplitHolder {
    public Guid Id { get; set; }
    public SplitOuter Outer { get; set; } = new();
  }

  /// <summary>Spread over columns; its nested types decide for themselves whether they are documents.</summary>
  public sealed class SplitOuter {
    public string Name { get; set; } = "";
    public SplitDocument Document { get; set; } = new();
    public SplitColumns Columns { get; set; } = new();
  }

  /// <summary>Mapped to JSON inside a column-split parent.</summary>
  public sealed class SplitDocument {
    public Dictionary<string, int> Counts { get; set; } = [];
  }

  /// <summary>Spread over columns inside a column-split parent.</summary>
  public sealed class SplitColumns {
    public int Total { get; set; }
  }

  [Test]
  public async Task ADocumentNestedInAColumnSplitType_GetsItsDictionaries_AndAColumnSplitOneDoesNotAsync() {
    DictionaryMemberForms.RegisterMember(typeof(SplitDocument), nameof(SplitDocument.Counts), typeof(Dictionary<string, int>));
    var modelBuilder = new ModelBuilder();
    modelBuilder.Entity<SplitHolder>(entity => {
      entity.HasKey(e => e.Id);
      entity.ComplexProperty(e => e.Outer, o => {
        o.ComplexProperty(x => x.Document, d => d.ToJson("document"));
        o.ComplexProperty(x => x.Columns);
      });
    });
    var outer = modelBuilder.Model.FindEntityType(typeof(SplitHolder))!.FindComplexProperty("Outer")!.ComplexType;

    DictionaryMemberConvention.Apply(modelBuilder);

    await Assert.That(outer.FindComplexProperty("Document")!.ComplexType.FindProperty("Counts")?.GetTypeMapping())
      .IsTypeOf<DictionaryMemberTypeMapping>()
      .Because("a nested type mapped to JSON is a document even when its parent is spread over columns");
    await Assert.That(outer.FindComplexProperty("Columns")!.ComplexType.FindProperty("Counts")).IsNull()
      .Because("a nested type spread over columns holds no document members");
  }
}
