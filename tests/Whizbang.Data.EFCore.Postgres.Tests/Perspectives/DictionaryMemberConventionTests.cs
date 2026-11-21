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
}
