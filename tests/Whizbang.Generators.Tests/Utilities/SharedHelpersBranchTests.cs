// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Text;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using AttributeUtilities = shared::Whizbang.Generators.Shared.Utilities.AttributeUtilities;
using ColumnStorageSql = shared::Whizbang.Generators.Shared.Models.ColumnStorageSql;
using CompositeIndexSql = shared::Whizbang.Generators.Shared.Models.CompositeIndexSql;
using JsonIndexCast = shared::Whizbang.Generators.Shared.Models.JsonIndexCast;
using JsonIndexInfo = shared::Whizbang.Generators.Shared.Models.JsonIndexInfo;
using JsonIndexSql = shared::Whizbang.Generators.Shared.Models.JsonIndexSql;
using MigrationBootstrapRegions = shared::Whizbang.Generators.Shared.Models.MigrationBootstrapRegions;
using PolymorphicModelDiscovery = shared::Whizbang.Generators.Shared.Models.PolymorphicModelDiscovery;
using SortableExposureDiscovery = shared::Whizbang.Generators.Shared.Models.SortableExposureDiscovery;
using TableStorageInfo = shared::Whizbang.Generators.Shared.Models.TableStorageInfo;
using TypeNameUtilities = shared::Whizbang.Generators.Shared.Utilities.TypeNameUtilities;
using TypeSymbolExtensions = shared::Whizbang.Generators.Shared.Utilities.TypeSymbolExtensions;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// The edges of the shared generator helpers that no generator input reaches: missing arguments,
/// values outside a mapped set, and the options a generator never passes.
/// </summary>
/// <remarks>
/// Each test takes both outcomes of the decision it targets through the same <c>shared::</c> copy.
/// The shared code is also merged into every generator assembly, and coverage is kept per copy, so
/// an edge asserted here and its ordinary case taken only by a generator would still read as half
/// covered.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators.Shared</code-under-test>
public class SharedHelpersBranchTests {
  private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation _compile(string source) => GeneratorTestHelper.CreateCompilation(source);

  [Test]
  public async Task GetStringArrayValue_DropsNullElements_FromNamedAndPositionalArgumentsAsync() {
    var compilation = _compile("""
      namespace Sample;
      public sealed class TagsAttribute : System.Attribute {
        public TagsAttribute() { }
        public TagsAttribute(string[] names) { Names = names; }
        public string[] Names { get; set; } = [];
      }
      [Tags(Names = new[] { "a", null, "b" })] public class Named { }
      [Tags(new[] { "c", null })] public class Positional { }
      """);
    var named = compilation.GetTypeByMetadataName("Sample.Named")!.GetAttributes().Single();
    var positional = compilation.GetTypeByMetadataName("Sample.Positional")!.GetAttributes().Single();

    await Assert.That(AttributeUtilities.GetStringArrayValue(named, "Names")).IsEquivalentTo(_namedValues);
    await Assert.That(AttributeUtilities.GetStringArrayValue(positional, "names")).IsEquivalentTo(_positionalValues);
  }

  private static readonly string[] _namedValues = ["a", "b"];
  private static readonly string[] _positionalValues = ["c"];

  [Test]
  public async Task GetAllMethods_IncludesNonPublicAndStatic_OnlyWhenAskedAsync() {
    var type = _compile("""
      namespace Sample;
      public class Service {
        public void Open() { }
        private void Hidden() { }
        public static void Shared() { }
      }
      """).GetTypeByMetadataName("Sample.Service")!;

    var byDefault = TypeSymbolExtensions.GetAllMethods(type).Select(m => m.Name).ToList();
    var everything = TypeSymbolExtensions.GetAllMethods(type, includeNonPublic: true, includeStatic: true).Select(m => m.Name).ToList();

    await Assert.That(byDefault).IsEquivalentTo(_openOnly);
    await Assert.That(everything).IsEquivalentTo(_allMethods);
  }

  private static readonly string[] _openOnly = ["Open"];
  private static readonly string[] _allMethods = ["Open", "Hidden", "Shared"];

  [Test]
  public async Task CompositeCreateStatement_NoDeclaration_IsEmptyAsync() {
    var declared = new shared::Whizbang.Generators.Shared.Models.CompositeIndexInfo(
      [new shared::Whizbang.Generators.Shared.Models.CompositeIndexElement("Tenant", "(data ->> 'Tenant')")]);

    await Assert.That(CompositeIndexSql.CreateStatement(null!, "app.wh_per_order", "order")).IsEmpty();
    await Assert.That(CompositeIndexSql.CreateStatement(declared, "app.wh_per_order", "order")).StartsWith("CREATE INDEX")
      .Because("the control: a declaration with an element is created");
  }

  /// <summary>Each cast names its SQL type; a value outside the enumeration names none.</summary>
  /// <remarks>
  /// The cast is passed as its number: the test framework's generated argument code cannot name a type
  /// reached only through the <c>shared</c> alias.
  /// </remarks>
  [Test]
  [Arguments(0, null)]
  [Arguments(1, "smallint")]
  [Arguments(2, "integer")]
  [Arguments(3, "bigint")]
  [Arguments(4, "numeric")]
  [Arguments(5, "real")]
  [Arguments(6, "double precision")]
  [Arguments(7, "boolean")]
  [Arguments(8, "uuid")]
  [Arguments(99, null)]
  public async Task StoreType_MapsEachCast_AndNothingElseAsync(int cast, string? expected) {
    await Assert.That(JsonIndexSql.StoreType((JsonIndexCast)cast)).IsEqualTo(expected);
  }

  [Test]
  public async Task ColumnCreateStatements_NoIndex_IsNothingAsync() {
    var index = new JsonIndexInfo("Status", "status", JsonIndexCast.None, Ordered: true, Substring: false, CaseInsensitive: false);

    await Assert.That(JsonIndexSql.ColumnCreateStatements(null!, "status", "app.wh_per_order", "order")).IsEmpty();
    await Assert.That(JsonIndexSql.ColumnCreateStatements(index, "status", "app.wh_per_order", "order")).IsNotEmpty()
      .Because("the control: an ordered index on the column is created");
  }

  /// <summary>The fold function is schema-qualified when the table is, and bare when it is not.</summary>
  [Test]
  public async Task ColumnSearchStatement_QualifiesTheFoldByTheTablesSchemaAsync() {
    var qualified = JsonIndexSql.ColumnSearchStatement("status", "app.wh_per_order", "order");
    var bare = JsonIndexSql.ColumnSearchStatement("status", "wh_per_order", "order");

    await Assert.That(qualified).Contains("app.wh_fold(status)");
    await Assert.That(bare).Contains("(wh_fold(status)");
  }

  /// <summary>Without a rewrite for plain statements, each is appended as written.</summary>
  [Test]
  public async Task AppendScript_WithoutAPlainRewrite_AppendsStatementsAsWrittenAsync() {
    var index = new JsonIndexInfo("Status", "status", JsonIndexCast.None, Ordered: true, Substring: false, CaseInsensitive: false);
    var rewritten = new StringBuilder();
    var asWritten = new StringBuilder();

    JsonIndexSql.AppendScript(rewritten, [index], "app.wh_per_order", "order", plain: s => "-- " + s);
    JsonIndexSql.AppendScript(asWritten, [index], "app.wh_per_order", "order");

    await Assert.That(rewritten.ToString()).StartsWith("-- CREATE INDEX");
    await Assert.That(asWritten.ToString()).StartsWith("CREATE INDEX");
  }

  [Test]
  public async Task TypeNameGuards_RejectANullSymbolAsync() {
    var compilation = _compile("namespace Sample.Inner; public class Named { } ");
    var named = compilation.GetTypeByMetadataName("Sample.Inner.Named")!;

    await Assert.That(() => TypeNameUtilities.FullyQualified(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.FullyQualifiedWithNullability(null!)).Throws<ArgumentNullException>();
    await Assert.That(TypeNameUtilities.FullyQualified(named)).IsEqualTo("global::Sample.Inner.Named");
    await Assert.That(() => TypeNameUtilities.MinimallyQualified(null!)).Throws<ArgumentNullException>();
    await Assert.That(TypeNameUtilities.FullyQualifiedWithNullability(named)).IsEqualTo("global::Sample.Inner.Named");
    await Assert.That(TypeNameUtilities.MinimallyQualified(named)).IsEqualTo("Named");
  }

  [Test]
  public async Task NamespaceName_IsEmptyForNoNamespaceAndTheGlobalOneAsync() {
    var compilation = _compile("namespace Sample.Inner; public class Named { } ");

    await Assert.That(TypeNameUtilities.NamespaceName(null)).IsEmpty();
    await Assert.That(TypeNameUtilities.NamespaceName(compilation.GlobalNamespace)).IsEmpty();
    await Assert.That(TypeNameUtilities.NamespaceName(compilation.GetTypeByMetadataName("Sample.Inner.Named")!.ContainingNamespace))
      .IsEqualTo("Sample.Inner");
  }

  [Test]
  public async Task IsPolymorphicType_NoType_IsNotPolymorphicAsync() {
    var compilation = _compile("namespace Sample; public abstract class Shape { } public sealed class Plain { }");

    await Assert.That(PolymorphicModelDiscovery.IsPolymorphicType(null!)).IsFalse();
    await Assert.That(PolymorphicModelDiscovery.IsPolymorphicType(compilation.GetTypeByMetadataName("Sample.Shape")!)).IsTrue();
    await Assert.That(PolymorphicModelDiscovery.IsPolymorphicType(compilation.GetTypeByMetadataName("Sample.Plain")!)).IsFalse();
  }

  /// <summary>A file whose marked region holds only whitespace has no bootstrap SQL to run.</summary>
  [Test]
  public async Task Extract_RegionOfOnlyWhitespace_IsNoBootstrapAsync() {
    var blank = MigrationBootstrapRegions.BEGIN + "\n   \n" + MigrationBootstrapRegions.END + "\nSELECT 1;\n";
    var real = MigrationBootstrapRegions.BEGIN + "\nCREATE SCHEMA x;\n" + MigrationBootstrapRegions.END + "\n";

    await Assert.That(MigrationBootstrapRegions.Extract(blank)).IsNull();
    await Assert.That(MigrationBootstrapRegions.Extract(real)).IsEqualTo("CREATE SCHEMA x;\n");
  }

  [Test]
  public async Task TableStorage_NoModel_DeclaresNothingAsync() {
    var plain = _compile("namespace Sample; public class Plain { }").GetTypeByMetadataName("Sample.Plain");

    await Assert.That(TableStorageInfo.From(null)).IsNull();
    await Assert.That(TableStorageInfo.From(plain)).IsNull();
  }

  [Test]
  [Arguments(1, "pglz")]
  [Arguments(2, "lz4")]
  [Arguments(0, null)]
  [Arguments(7, null)]
  public async Task CompressionName_MapsOnlyTheNamedMethodsAsync(int value, string? expected) {
    await Assert.That(ColumnStorageSql.CompressionName(value)).IsEqualTo(expected);
  }

  [Test]
  public async Task CompressionName_NonNumericValue_IsNoMethodAsync() {
    await Assert.That(ColumnStorageSql.CompressionName("lz4")).IsNull();
  }

  /// <summary>A type that is not generic has no parameter list to cut, and is not a queryable.</summary>
  [Test]
  public async Task ModelOfQueryable_NonGenericType_IsNoModelAsync() {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
      namespace Sample;
      public class Order { }
      public static class Source { public static System.Linq.IQueryable<Order> Orders() => null!; }
      """);
    var stringType = compilation.GetSpecialType(SpecialType.System_String);
    var queryable = compilation.GetTypeByMetadataName("Sample.Source")!.GetMembers("Orders").OfType<IMethodSymbol>().Single().ReturnType;

    await Assert.That(SortableExposureDiscovery.ModelOfQueryable(stringType)).IsNull();
    await Assert.That(SortableExposureDiscovery.ModelOfQueryable(queryable)?.Name).IsEqualTo("Order")
      .Because("the control: a generic queryable's definition name is cut at its bracket and recognized");
  }
}
