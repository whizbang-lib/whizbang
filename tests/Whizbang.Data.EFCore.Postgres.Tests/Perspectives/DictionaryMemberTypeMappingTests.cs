// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>A model whose dictionary values a key lookup cannot be cast to: a temporal and a structured value.</summary>
[SuppressIndexAdvisory("translation fixture; nothing is run against a database")]
public class DictionaryMemberRefusalModel {
  [StreamId]
  public Guid Id { get; set; }

  public Dictionary<string, DateTime> Times { get; set; } = [];
  public Dictionary<string, DictionaryMemberLine> Lines { get; set; } = [];
  public Dictionary<string, List<int>> Lists { get; set; } = [];
  public Dictionary<string, int> Counts { get; set; } = [];
  public string Label { get; set; } = string.Empty;
}

public record DictionaryMemberRefusalNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("dictionary-members")]
public class DictionaryMemberRefusalProjection : IPerspectiveFor<DictionaryMemberRefusalModel, DictionaryMemberRefusalNoted> {
  public DictionaryMemberRefusalModel Apply(DictionaryMemberRefusalModel currentData, DictionaryMemberRefusalNoted eventData) => currentData;
}

/// <summary>
/// The pieces of a dictionary member's Entity Framework mapping, without a database: the mapping per declared shape,
/// the comparer that decides a change, the JSON reader over Entity Framework's buffered reader, and the queries the
/// translator leaves for Entity Framework to refuse (#1911).
/// </summary>
/// <docs>fundamentals/perspectives/dictionary-members</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/DictionaryMemberTypeMapping.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Functions/DictionaryMemberTranslator.cs</code-under-test>
[Category("Shard2")]
public class DictionaryMemberTypeMappingTests {
  private const string OFFLINE = "Host=localhost;Database=dictionary_probe;Username=u;Password=p";

  static DictionaryMemberTypeMappingTests() => DictionaryMemberForms.Register<string, bool?>();

  private sealed class RefusalDbContext(DbContextOptions<RefusalDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<DictionaryMemberRefusalModel>>(entity => {
        entity.ToTable("wh_per_dictionary_refusal");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
      });
  }

  private static RefusalDbContext _context() => new(new DbContextOptionsBuilder<RefusalDbContext>()
    .UseNpgsql(OFFLINE, npgsql => npgsql.UseWhizbangFunctions())
    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
    .Options);

  private static DictionaryMemberTypeMapping _mapping<TMember>() => DictionaryMemberTypeMapping.For(typeof(TMember))!;

  [Test]
  public async Task EachDeclaredShape_HasOneJsonbMappingAsync() {
    var concrete = _mapping<Dictionary<string, int>>();

    await Assert.That(concrete.StoreType).IsEqualTo("jsonb");
    await Assert.That(concrete.ClrType).IsEqualTo(typeof(Dictionary<string, int>));
    await Assert.That(_mapping<IReadOnlyDictionary<string, int>>().ClrType).IsEqualTo(typeof(IReadOnlyDictionary<string, int>));
    await Assert.That(DictionaryMemberTypeMapping.For(typeof(Dictionary<string, int>))).IsSameReferenceAs(concrete)
      .Because("a mapping is immutable and built once per type");
    await Assert.That(concrete.WithStoreTypeAndSize("jsonb", null)).IsTypeOf<DictionaryMemberTypeMapping>()
      .Because("a mapping Entity Framework clones keeps its form");
    await Assert.That(DictionaryMemberTypeMapping.For(typeof(SortedDictionary<string, int>))).IsNull();
    await Assert.That(() => DictionaryMemberTypeMapping.For(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheComparer_SeesOrderAndChangesInPlaceAsync() {
    var original = new Dictionary<string, DictionaryMemberLine> { ["a"] = new() { Name = "x", Quantity = 1 } };
    var snapshot = DictionaryMemberValues<Dictionary<string, DictionaryMemberLine>, string, DictionaryMemberLine>.Snapshot(original);

    await Assert.That(_equal(original, snapshot)).IsTrue();
    await Assert.That(DictionaryMemberValues<Dictionary<string, DictionaryMemberLine>, string, DictionaryMemberLine>.HashOf(original))
      .IsEqualTo(DictionaryMemberValues<Dictionary<string, DictionaryMemberLine>, string, DictionaryMemberLine>.HashOf(snapshot));

    original["a"].Quantity = 2;

    await Assert.That(_equal(original, snapshot)).IsFalse()
      .Because("the snapshot is a deep copy, so a value changed in place is a change to save");
    await Assert.That(_equal(original, original)).IsTrue();
    await Assert.That(_equal(null, null)).IsTrue();
    await Assert.That(_equal(original, null)).IsFalse();
    await Assert.That(_equal(null, original)).IsFalse();

    var forward = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
    var backward = new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 };
    await Assert.That(DictionaryMemberValues<Dictionary<string, int>, string, int>.AreEqual(forward, backward)).IsFalse()
      .Because("the order is stored, so a reordering is a change");
  }

  private static bool _equal(Dictionary<string, DictionaryMemberLine>? left, Dictionary<string, DictionaryMemberLine>? right) =>
    DictionaryMemberValues<Dictionary<string, DictionaryMemberLine>, string, DictionaryMemberLine>.AreEqual(left, right);

  [Test]
  public async Task TheConverter_RoundTripsTheStoredTextAsync() {
    var mapping = _mapping<Dictionary<string, int>>();
    var converter = mapping.Converter!;

    var text = (string)converter.ConvertToProvider(new Dictionary<string, int> { ["a"] = 1 })!;
    var back = (Dictionary<string, int>)converter.ConvertFromProvider(text)!;

    await Assert.That(text).IsEqualTo("""[{"Key":"a","Value":1}]""");
    await Assert.That(back["a"]).IsEqualTo(1);
    await Assert.That((string)mapping.KeyToJson.ConvertToProvider("a")!).IsEqualTo("\"a\"");
    await Assert.That((string)mapping.KeyToJson.ConvertFromProvider("\"a\"")!).IsEqualTo("a");
  }

  [Test]
  public async Task TheReader_CopiesEveryTokenKindFromEntityFrameworksReaderAsync() {
    var readerWriter = _mapping<Dictionary<string, bool?>>().JsonValueReaderWriter!;

    var read = (Dictionary<string, bool?>)readerWriter.FromJsonString("""[{"Key":"t","Value":true},{"Key":"f","Value":false},{"Key":"n","Value":null}]""");
    var written = readerWriter.ToJsonString(read);

    await Assert.That(read["t"]).IsTrue();
    await Assert.That(read["f"]).IsFalse();
    await Assert.That(read["n"]).IsNull();
    await Assert.That(written).IsEqualTo("""[{"Key":"t","Value":true},{"Key":"f","Value":false},{"Key":"n","Value":null}]""");
    await Assert.That(() => readerWriter.FromJsonString("42")).Throws<JsonException>()
      .Because("a scalar is neither stored form");
#pragma warning disable EF9100 // Entity Framework reads it to build a compiled model; asserted here so it names the instance.
    await Assert.That(readerWriter.ConstructorExpression.ToString()).Contains("Instance");
#pragma warning restore EF9100
  }

  [Test]
  public async Task TheTypeMappingSourcePlugin_AnswersOnlyForARegisteredFormAsync() {
    var plugin = new DictionaryMemberTypeMappingSourcePlugin();

    await Assert.That(plugin.FindMapping(new RelationalTypeMappingInfo(typeof(Dictionary<string, int>)))).IsTypeOf<DictionaryMemberTypeMapping>();
    await Assert.That(plugin.FindMapping(new RelationalTypeMappingInfo(typeof(List<int>)))).IsNull();
    await Assert.That(plugin.FindMapping(default)).IsNull();
  }

  [Test]
  [Arguments("temporal")]
  [Arguments("structured")]
  [Arguments("collection")]
  [Arguments("column key")]
  [Arguments("other method")]
  public async Task ALookupTheTranslatorCannotAnswer_IsRefused_NeverEvaluatedOnTheClientAsync(string shape) {
    await using var context = _context();
    var rows = context.Set<PerspectiveRow<DictionaryMemberRefusalModel>>();
    IQueryable<PerspectiveRow<DictionaryMemberRefusalModel>> query = shape switch {
      "temporal" => rows.Where(r => r.Data.Times["a"] > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
      "structured" => rows.Where(r => r.Data.Lines["a"].Quantity == 1),
      "collection" => rows.Where(r => r.Data.Lists["a"].Count == 1),
      "column key" => rows.Where(r => r.Data.Counts.ContainsKey(r.Data.Label)),
      _ => rows.Where(r => r.Data.Counts.ContainsValue(1)),
    };

    await Assert.That(() => query.ToQueryString()).Throws<InvalidOperationException>()
      .Because("Entity Framework refuses what no translator answers; the rows are never read to filter on the client");
  }

  [Test]
  public async Task TheTranslator_LeavesEverythingButAKeyLookupOnADictionaryAsync() {
    await using var context = _context();
    var factory = context.GetService<ISqlExpressionFactory>();
    var mappings = context.GetService<IRelationalTypeMappingSource>();
    var translator = new DictionaryMemberTranslatorPlugin(factory, mappings).Translators.Single();
    var logger = context.GetService<IDiagnosticsLogger<DbLoggerCategory.Query>>();
    var dictionary = new SqlConstantExpression(new Dictionary<string, int>(), typeof(Dictionary<string, int>), _mapping<Dictionary<string, int>>());
    var key = new SqlConstantExpression("a", typeof(string), mappings.FindMapping(typeof(string)));
    var containsKey = typeof(Dictionary<string, int>).GetMethod(nameof(Dictionary<,>.ContainsKey))!;
    var toString = typeof(object).GetMethod(nameof(ToString))!;

    await Assert.That(translator.Translate(null, containsKey, [key], logger)).IsNull();
    await Assert.That(translator.Translate(key, containsKey, [key], logger)).IsNull()
      .Because("an instance that is not a dictionary member is not this translator's");
    await Assert.That(translator.Translate(dictionary, toString, [], logger)).IsNull();
    await Assert.That(translator.Translate(dictionary, containsKey, [key], logger)).IsNotNull();
  }

  [Test]
  public async Task ThePlugin_RefusesAFactoryThatIsNotNpgsqlsAsync() {
    await using var context = _context();
    var foreign = System.Reflection.DispatchProxy.Create<ISqlExpressionFactory, Functions.WhizbangDbContextOptionsExtensionsCoverageTests.UnusedFactoryProxy>();

    await Assert.That(() => new DictionaryMemberTranslatorPlugin(foreign, context.GetService<IRelationalTypeMappingSource>()))
      .Throws<InvalidOperationException>();
  }
}
