extern alias core_generators;
extern alias fastendpoints_generators;
extern alias hotchocolate_generators;
extern alias postgres_generators;
extern alias shared;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Verifies the copy of Whizbang.Generators.Shared that ILRepack merges into each generator
/// assembly is present and behaves correctly.
/// </summary>
/// <remarks>
/// <para>
/// Shared is merged into four generator assemblies so each analyzer ships self-contained, which
/// leaves four independent copies of the same code. Nothing verified any of them: the existing
/// tests exercise Shared in its own assembly, and that is the one copy which never ships inside
/// an analyzer. If ILRepack dropped a type, merged a stale build, or produced copies that
/// disagree, the affected generator would misbehave in a consumer's build while this suite
/// stayed green.
/// </para>
/// <para>
/// Reflection rather than direct calls, because the merge internalizes the shared types in
/// three of the four hosts (they remain public only in Whizbang.Generators). Reflecting keeps
/// one uniform test for all four and avoids widening production visibility purely for tests.
/// The lines still execute in the host assembly, which is the point.
/// </para>
/// </remarks>
/// <tests>Whizbang.Generators.Shared/Utilities/NamingConventionUtilities.cs:*</tests>
public class MergedSharedCopyTests {

  private const string NAMING_TYPE = "Whizbang.Generators.Shared.Utilities.NamingConventionUtilities";

  private static readonly string[] _indexColumns = ["id"];
  private static readonly string[] _strippedSuffixes = ["Model", "Dto"];
  private static readonly string[] _tableSuffixes = ["Projection", "Model"];
  private static readonly string[] _noSuffixes = [];

  /// <summary>A generator assembly carrying its own merged copy of the shared code.</summary>
  public sealed record MergedHost(string Host, Assembly Assembly);

  /// <summary>One anchor type per host, used only to reach that host's assembly.</summary>
  private static readonly MergedHost[] _hosts = [
    new MergedHost("Whizbang.Generators",
      typeof(core_generators::Whizbang.Generators.MessageJsonContextGenerator).Assembly),
    new MergedHost("Whizbang.Data.EFCore.Postgres.Generators",
      typeof(postgres_generators::Whizbang.Data.EFCore.Postgres.Generators.PerspectiveModelDictionaryAnalyzer).Assembly),
    new MergedHost("Whizbang.Transports.HotChocolate.Generators",
      typeof(hotchocolate_generators::Whizbang.Transports.HotChocolate.Generators.GraphQLLensTypeGenerator).Assembly),
    new MergedHost("Whizbang.Transports.FastEndpoints.Generators",
      typeof(fastendpoints_generators::Whizbang.Transports.FastEndpoints.Generators.RestLensEndpointGenerator).Assembly),
    // The source assembly itself. It is the reference the four copies are supposed to match, so
    // running the same checks against it is what makes "identical" mean anything -- and without
    // it the self-test below is dead code in the one assembly that actually ships it as source.
    new MergedHost("Whizbang.Generators.Shared",
      typeof(shared::Whizbang.Generators.Shared.Diagnostics.SharedSelfTest).Assembly),
  ];

  private static MethodInfo _method(Assembly assembly, string name) {
    var type = assembly.GetType(NAMING_TYPE)
      ?? throw new InvalidOperationException(
        $"{NAMING_TYPE} is missing from {assembly.GetName().Name}: the ILRepack merge did not include it.");
    return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException(
        $"{NAMING_TYPE}.{name} is missing from {assembly.GetName().Name}.");
  }

  private static string _invoke(Assembly assembly, string method, string argument)
    => (string)_method(assembly, method).Invoke(null, [argument])!;

  public static IEnumerable<Func<MergedHost>> Hosts()
    => _hosts.Select<MergedHost, Func<MergedHost>>(h => () => h);

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_DerivesNamesIdenticallyToTheSourceAsync(MergedHost host) {
    // These conversions decide table names, REST routes and GraphQL field names. A copy that
    // merged incorrectly would send a consumer's endpoint to a different path, or map a
    // perspective to a different table, with nothing in this repo to notice.
    await Assert.That(_invoke(host.Assembly, "ToSnakeCase", "OrderReadModel")).IsEqualTo("order_read_model")
      .Because($"{host.Host} derives table names from this");
    await Assert.That(_invoke(host.Assembly, "Pluralize", "Order")).IsEqualTo("Orders");
    await Assert.That(_invoke(host.Assembly, "Pluralize", "Orders")).IsEqualTo("Orders")
      .Because("an already-plural name must not gain a second s");
    await Assert.That(_invoke(host.Assembly, "StripCommonSuffixes", "OrderReadModel")).IsEqualTo("Order");
    await Assert.That(_invoke(host.Assembly, "StripCommonSuffixes", "ProductDto")).IsEqualTo("Product");
    await Assert.That(_invoke(host.Assembly, "ToDefaultRouteName", "OrderReadModel")).IsEqualTo("/api/orders")
      .Because($"{host.Host} derives REST routes from this");
    await Assert.That(_invoke(host.Assembly, "ToDefaultQueryName", "OrderReadModel")).IsEqualTo("orders")
      .Because($"{host.Host} derives GraphQL field names from this");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ContainsTheSharedUtilityTypeAsync(MergedHost host) {
    // The bluntest failure the merge can produce: a type that simply is not there. It surfaces
    // as a MissingMethodException inside a consumer's build, long after this repo shipped.
    await Assert.That(host.Assembly.GetType(NAMING_TYPE)).IsNotNull()
      .Because($"{host.Host} ships self-contained, so the merged copy has to be in it");
  }

  [Test]
  public async Task EveryHostCopy_AgreesWithTheOthersAsync() {
    // Four copies built from one source must not diverge. If they did, the same model type
    // would produce a different route depending on which generator emitted it -- an
    // inconsistency almost impossible to trace back to packaging.
    const string model = "InvoiceReadModel";

    var routes = _hosts.Select(h => _invoke(h.Assembly, "ToDefaultRouteName", model)).Distinct().ToList();

    await Assert.That(routes.Count).IsEqualTo(1)
      .Because("all four merged copies come from one source and must behave identically");
    await Assert.That(routes[0]).IsEqualTo("/api/invoices");
  }

  // ── The rest of the shared surface, per host ──────────────────────────────

  private static Type _type(Assembly assembly, string fullName)
    => assembly.GetType(fullName)
      ?? throw new InvalidOperationException(
        $"{fullName} is missing from {assembly.GetName().Name}: the ILRepack merge did not include it.");

  private static object? _call(Assembly assembly, string typeName, string method, params object?[] args) {
    var type = _type(assembly, typeName);
    var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
      .Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToList();
    var chosen = candidates.FirstOrDefault(m =>
        m.GetParameters().Zip(args).All(p => p.Second is null || p.First.ParameterType.IsInstanceOfType(p.Second)))
      ?? candidates.FirstOrDefault()
      ?? throw new InvalidOperationException($"{typeName}.{method} is missing from {assembly.GetName().Name}.");
    return chosen.Invoke(null, args);
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_HashesSchemasIdenticallyAsync(MergedHost host) {
    // The schema hash decides whether a perspective's table is considered changed. If a host's
    // merged copy hashed differently, that generator would see drift where none exists and
    // reissue migrations against an unchanged table.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var hash = (string)_call(host.Assembly, ns + "SchemaHashUtilities", "ComputeHash", "order_table")!;

    await Assert.That(hash).IsNotEmpty();
    await Assert.That((string)_call(host.Assembly, ns + "SchemaHashUtilities", "ComputeHash", "order_table")!)
      .IsEqualTo(hash)
      .Because("the hash has to be stable, or every build would look like a schema change");
    await Assert.That((string)_call(host.Assembly, ns + "SchemaHashUtilities", "ComputeHash", "other_table")!)
      .IsNotEqualTo(hash)
      .Because("different schemas must not collide, or a real change would go unnoticed");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_CanonicalisesAndHashesATableSchemaAsync(MergedHost host) {
    // Exercises the record types the hash is built from -- ColumnSchema, IndexSchema and
    // PerspectiveTableSchema -- inside this host's merged copy.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var columnType = _type(host.Assembly, ns + "ColumnSchema");
    var indexType = _type(host.Assembly, ns + "IndexSchema");
    var schemaType = _type(host.Assembly, ns + "PerspectiveTableSchema");

    var column = Activator.CreateInstance(columnType, "id", "uuid", false, true, false, (int?)null)!;
    var index = Activator.CreateInstance(indexType, "idx_id", (IReadOnlyList<string>)_indexColumns, "btree", true)!;

    var columns = Array.CreateInstance(columnType, 1);
    columns.SetValue(column, 0);
    var indexes = Array.CreateInstance(indexType, 1);
    indexes.SetValue(index, 0);

    var schema = Activator.CreateInstance(schemaType, columns, indexes)!;

    var json = (string)_call(host.Assembly, ns + "SchemaHashUtilities", "ToCanonicalJson", schema)!;
    var schemaHash = (string)_call(host.Assembly, ns + "SchemaHashUtilities", "ComputeSchemaHash", schema)!;

    await Assert.That(json).Contains("id");
    await Assert.That(schemaHash).IsNotEmpty();
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ParsesSuffixListsAndMeasuresIdentifiersAsync(MergedHost host) {
    // Suffix lists come from .editorconfig and decide how a model name becomes a table name;
    // the byte count is what identifier-length limits are checked against, and getting it wrong
    // means either a rejected migration or a silently truncated name.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var suffixes = (string[])_call(host.Assembly, ns + "ConfigurationUtilities", "ParseSuffixList", "Model,Dto, View")!;

    await Assert.That(suffixes).Contains("Model");
    await Assert.That(suffixes).Contains("Dto");
    await Assert.That(suffixes).Contains("View")
      .Because("entries are trimmed, so a space after a comma must not produce \" View\"");

    var bytes = (int)_call(host.Assembly, ns + "IdentifierValidation", "GetByteCount", "order_table")!;
    await Assert.That(bytes).IsEqualTo(11);
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ShapesGeneratedTextIdenticallyAsync(MergedHost host) {
    // Template handling produces the source these generators emit. A divergence here shows up as
    // malformed generated code in whichever package carried the bad copy.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var indented = (string)_call(host.Assembly, ns + "TemplateUtilities", "IndentCode", "a\nb", "  ")!;

    await Assert.That(indented).Contains("  a");
    await Assert.That(indented).Contains("  b");

    var simple = (string)_call(host.Assembly, ns + "TypeNameUtilities", "GetSimpleName", "App.Models.OrderReadModel")!;
    await Assert.That(simple).IsEqualTo("OrderReadModel")
      .Because("the simple name is what table and endpoint names are derived from");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_CarriesTheSharedModelRecordsAsync(MergedHost host) {
    // The model records travel between the shared utilities and each generator. A host missing
    // one, or carrying a differently-shaped copy, fails at the boundary rather than at the call.
    const string models = "Whizbang.Generators.Shared.Models.";
    var configType = _type(host.Assembly, models + "TableNameConfig");

    var config = Activator.CreateInstance(configType, true, _strippedSuffixes)!;
    var stripSuffixes = (bool)configType.GetProperty("StripSuffixes")!.GetValue(config)!;
    var suffixes = (string[])configType.GetProperty("SuffixesToStrip")!.GetValue(config)!;

    await Assert.That(stripSuffixes).IsTrue();
    await Assert.That(suffixes).Contains("Model");

    var defaults = configType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    await Assert.That((bool)configType.GetProperty("StripSuffixes")!.GetValue(defaults)!).IsTrue()
      .Because("the default configuration strips suffixes; a host disagreeing would name tables differently");

    foreach (var name in new[] { "DbContextInfo", "PerspectiveInfo", "PhysicalFieldInfo" }) {
      await Assert.That(host.Assembly.GetType(models + name)).IsNotNull()
        .Because($"{name} crosses the boundary between the shared code and {host.Host}");
    }
  }


  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_PhysicalFieldInfoStillComparesByValueAsync(MergedHost host) {
    // Incremental generators cache on these records, and the caching is only correct because the
    // record compares by value. A merged copy that compared by reference would report every
    // rebuild as a change and re-run the whole generator on each keystroke; one that compared
    // everything equal would never re-run it and emit stale code. Neither failure announces
    // itself -- the generator still produces output, just at the wrong time.
    //
    // Two of the four hosts never construct one of these in their own generator, so their copy's
    // synthesized members had never executed. Reflection is the only way in: the record type is
    // internal to each host and a distinct type identity per copy.
    const string physicalField = "Whizbang.Generators.Shared.Models.PhysicalFieldInfo";
    var fieldType = _type(host.Assembly, physicalField);

    object?[] arguments = [
      "Embedding", "embedding", "float[]", true, false, null, true, null, null, null, null,
    ];
    var one = Activator.CreateInstance(fieldType, arguments)!;
    var same = Activator.CreateInstance(fieldType, arguments)!;

    object?[] differing = [.. arguments];
    differing[1] = "a_different_column";
    var other = Activator.CreateInstance(fieldType, differing)!;

    await Assert.That(ReferenceEquals(one, same)).IsFalse()
      .Because("these are two separate instances; comparing them is the point of the test");
    await Assert.That(one.Equals(same)).IsTrue()
      .Because("two fields describing the same column are the same value, and the generator's "
             + "incremental cache depends on that being true across the merge boundary");
    await Assert.That(one.GetHashCode()).IsEqualTo(same.GetHashCode())
      .Because("equal values must hash alike or the cache lookup misses even when equality holds");
    await Assert.That(one.Equals(other)).IsFalse()
      .Because("a copy that found every field equal would cache a stale result and emit code for "
             + "a column that had been renamed");
    await Assert.That(one.ToString()).Contains("Embedding")
      .Because("the record's generated ToString is what a generator diagnostic prints when it "
             + "reports which field it choked on");
  }

  // ── The symbol-dependent surface, per host ────────────────────────────────

  private const string SYMBOL_SOURCE = """
    namespace App {
      [System.AttributeUsage(System.AttributeTargets.Class)]
      public sealed class RouteAttribute : System.Attribute {
        public RouteAttribute(string path) { Path = path; }
        public string Path { get; }
        public string? Name { get; set; }
        public bool Cached { get; set; }
        public int Limit { get; set; }
      }

      [Route("/api/orders", Name = "orders", Cached = true, Limit = 25)]
      public class OrderReadModel {
        public string Id { get; set; } = "";
        public int Total { get; set; }
      }
    }
    """;

  /// <summary>Builds the symbol inputs the shared utilities take. Roslyn types are not merged,
  /// so one compilation's symbols are accepted by every host's copy.</summary>
  private static (INamedTypeSymbol Type, AttributeData Attribute) _symbols() {
    var compilation = GeneratorTestHelper.CreateCompilation(SYMBOL_SOURCE);
    var type = compilation.GetTypeByMetadataName("App.OrderReadModel")
      ?? throw new InvalidOperationException("test compilation did not produce App.OrderReadModel");
    var attribute = type.GetAttributes().First(a => a.AttributeClass?.Name == "RouteAttribute");
    return (type, attribute);
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReadsAttributeArgumentsIdenticallyAsync(MergedHost host) {
    // Every generator decides what to emit by reading attribute arguments. A host whose merged
    // copy read them differently would emit an endpoint on the wrong route, or silently fall
    // back to a default the author never chose -- and it would do so only in that one package.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var (_, attribute) = _symbols();

    await Assert.That((string?)_call(host.Assembly, ns + "AttributeUtilities", "GetStringValue", attribute, "Name"))
      .IsEqualTo("orders");
    await Assert.That((bool)_call(host.Assembly, ns + "AttributeUtilities", "GetBoolValue", attribute, "Cached", false)!)
      .IsTrue();
    await Assert.That((int)_call(host.Assembly, ns + "AttributeUtilities", "GetIntValue", attribute, "Limit", 10)!)
      .IsEqualTo(25);
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_FallsBackWhenAnArgumentIsAbsentAsync(MergedHost host) {
    // The defaults matter as much as the values: an attribute that omits a setting must yield
    // the documented default rather than zero or null, which downstream code would treat as a
    // real choice.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var (_, attribute) = _symbols();

    await Assert.That((string?)_call(host.Assembly, ns + "AttributeUtilities", "GetStringValue", attribute, "Missing"))
      .IsNull();
    await Assert.That((bool)_call(host.Assembly, ns + "AttributeUtilities", "GetBoolValue", attribute, "Missing", true)!)
      .IsTrue()
      .Because("an absent flag takes the supplied default, not false");
    await Assert.That((int)_call(host.Assembly, ns + "AttributeUtilities", "GetIntValue", attribute, "Missing", 7)!)
      .IsEqualTo(7);
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReadsTypeSymbolsIdenticallyAsync(MergedHost host) {
    // Property discovery drives the columns a perspective table gets and the fields an endpoint
    // exposes. A host that enumerated them differently would generate a table missing a column.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var (type, _) = _symbols();

    var simple = (string)_call(host.Assembly, ns + "TypeNameUtilities", "GetSimpleName", type)!;
    await Assert.That(simple).IsEqualTo("OrderReadModel");

    var extensions = _type(host.Assembly, ns + "TypeSymbolExtensions");
    var names = (string[])extensions
      .GetMethod("GetAllPublicPropertyNames", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
      .Invoke(null, [type])!;

    await Assert.That(names).Contains("Id");
    await Assert.That(names).Contains("Total");
  }

  [Test]
  public async Task EveryHostCopy_ReadsTheSameAttributeTheSameWayAsync() {
    // The cross-host check for the symbol surface: four copies reading one attribute must agree,
    // or the same annotated model yields different output depending on which generator saw it.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var (_, attribute) = _symbols();

    var values = _hosts
      .Select(h => (string?)_call(h.Assembly, ns + "AttributeUtilities", "GetStringValue", attribute, "Name"))
      .Distinct()
      .ToList();

    await Assert.That(values.Count).IsEqualTo(1);
    await Assert.That(values[0]).IsEqualTo("orders");
  }


  private const string NESTED_SYMBOL_SOURCE = """
    namespace App {
      public class ActiveAccount {
        public class ActiveAccountModel { }
        public class Snapshot { }
      }
      public class Order { }
      public class Envelope<TPayload> { }
    }
    """;

  private static INamedTypeSymbol _nested(string metadataName) {
    var compilation = GeneratorTestHelper.CreateCompilation(NESTED_SYMBOL_SOURCE);
    return compilation.GetTypeByMetadataName(metadataName)
      ?? throw new InvalidOperationException($"test compilation did not produce {metadataName}");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_SimplifiesTypeNamesIdenticallyAsync(MergedHost host) {
    // Every generator derives table, endpoint and property names from this. A copy that
    // simplified differently would emit a different schema from the same source, and only in
    // whichever package carried it.
    const string ns = "Whizbang.Generators.Shared.Utilities.";

    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetSimpleName", "App.Models.OrderReadModel")!)
      .IsEqualTo("OrderReadModel");
    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetSimpleName", "OrderReadModel")!)
      .IsEqualTo("OrderReadModel")
      .Because("a name with no namespace is already simple");
    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetSimpleName", "App.Models.Order[]")!)
      .IsEqualTo("Order[]")
      .Because("the array suffix survives simplification of the element type");
    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetSimpleName", "(App.Models.Order, App.Models.Line)")!)
      .IsEqualTo("(Order, Line)")
      .Because("tuple members are simplified individually and the shape is preserved");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_SplitsTupleMembersAtTheOuterLevelOnlyAsync(MergedHost host) {
    // The depth counter is what stops a nested tuple being torn apart at its inner comma. A
    // copy that lost it would split "(a, (b, c))" into three members and generate a signature
    // that does not match the type it came from.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var parts = (string[])_call(host.Assembly, ns + "TypeNameUtilities", "SplitTupleParts", "int, (string, bool), long")!;

    await Assert.That(parts.Length).IsEqualTo(3)
      .Because("the comma inside the inner tuple is not a member separator");
    await Assert.That(parts[1].Trim()).IsEqualTo("(string, bool)");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_NamesDbSetsAndTablesIdenticallyAsync(MergedHost host) {
    // Table naming decides the physical schema. Two packages disagreeing here means one of them
    // reads and writes a table the other never creates.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var topLevel = _nested("App.Order");
    var nestedEchoing = _nested("App.ActiveAccount+ActiveAccountModel");
    var nestedDistinct = _nested("App.ActiveAccount+Snapshot");

    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetDbSetPropertyName", topLevel)!)
      .IsEqualTo("Orders");
    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetDbSetPropertyName", nestedDistinct)!)
      .IsEqualTo("ActiveAccountModels")
      .Because("a nested model takes its DbSet name from the containing type");

    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetTableBaseName", topLevel)!)
      .IsEqualTo("Order");
    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetTableBaseName", nestedEchoing)!)
      .IsEqualTo("ActiveAccount")
      .Because("a nested name that repeats its container collapses, or the table would be "
             + "wh_per_active_account_active_account");
    await Assert.That((string)_call(host.Assembly, ns + "TypeNameUtilities", "GetTableBaseName", nestedDistinct)!)
      .IsEqualTo("ActiveAccountSnapshot")
      .Because("a nested name that differs is concatenated so the two tables stay distinct");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_BuildsClrTypeNamesIdenticallyAsync(MergedHost host) {
    // Event types are stored in the database in CLR format and looked up by exact string. A
    // copy that emitted '.' where the runtime writes '+' produces rows that never match on read.
    const string ns = "Whizbang.Generators.Shared.Utilities.";

    var nestedClr = (string)_call(host.Assembly, ns + "TypeNameUtilities", "BuildClrTypeName",
      _nested("App.ActiveAccount+Snapshot"))!;
    await Assert.That(nestedClr).Contains("ActiveAccount+Snapshot")
      .Because("nested types join with '+' to match Type.FullName, which is what the lookup uses");

    var genericClr = (string)_call(host.Assembly, ns + "TypeNameUtilities", "BuildClrTypeName",
      _nested("App.Envelope`1"))!;
    await Assert.That(genericClr).Contains("Envelope`1")
      .Because("generic arity is part of the CLR name");

    var runtime = (string)_call(host.Assembly, ns + "TypeNameUtilities", "FormatTypeNameForRuntime",
      _nested("App.Order"))!;
    await Assert.That(runtime).Contains("App.Order");
    await Assert.That(runtime).Contains(",")
      .Because("the runtime form is assembly-qualified");
  }


  private const string INHERITANCE_SOURCE = """
    namespace App {
      [System.AttributeUsage(System.AttributeTargets.All)]
      public sealed class MarkAttribute : System.Attribute { }

      public class BaseEntity {
        [Mark] public string InheritedKey { get; set; } = "";
        [Mark] public virtual void Configure() { }
        internal string Hidden { get; set; } = "";
      }

      public class OrderEntity : BaseEntity {
        public int Total { get; set; }
        public override void Configure() { }
      }
    }
    """;

  private static INamedTypeSymbol _orderEntity() {
    var compilation = GeneratorTestHelper.CreateCompilation(INHERITANCE_SOURCE);
    return compilation.GetTypeByMetadataName("App.OrderEntity")
      ?? throw new InvalidOperationException("test compilation did not produce App.OrderEntity");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_FindsAttributedMembersOnBaseTypesAsync(MergedHost host) {
    // Generators locate the key property and the configure hook by attribute, then walk up to
    // the base type. A copy that stopped at the declaring type would silently emit a schema
    // missing every inherited column -- the generator succeeds and the table is wrong.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    const string mark = "global::App.MarkAttribute";
    var entity = _orderEntity();

    var property = (IPropertySymbol?)_call(
      host.Assembly, ns + "TypeSymbolExtensions", "FindPropertyWithAttribute", entity, mark, true);
    await Assert.That(property?.Name).IsEqualTo("InheritedKey")
      .Because("the attributed property is declared on the base type, not on OrderEntity");

    var method = (IMethodSymbol?)_call(
      host.Assembly, ns + "TypeSymbolExtensions", "FindMethodWithAttribute", entity, mark, true);
    await Assert.That(method?.Name).IsEqualTo("Configure")
      .Because("the override carries no attribute, so the walk has to reach the base declaration");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReturnsAnOverriddenMethodOnceAsync(MergedHost host) {
    // Configure is declared twice -- virtual on the base, override on the derived type. The
    // signature dedupe is what stops a generator emitting the same registration twice, which
    // for a receptor means the message is handled twice per delivery.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var entity = _orderEntity();

    var byName = (IEnumerable<IMethodSymbol>)_call(
      host.Assembly, ns + "TypeSymbolExtensions", "GetAllMethodsByName", entity, "Configure", false)!;

    await Assert.That(byName.Count()).IsEqualTo(1)
      .Because("an override and the method it overrides are one method, not two");

    var all = (IEnumerable<IMethodSymbol>)_call(
      host.Assembly, ns + "TypeSymbolExtensions", "GetAllMethods", entity, false, false)!;
    var names = all.Select(m => m.Name).ToList();

    await Assert.That(names).Contains("Configure");
    await Assert.That(names.Count(n => n == "Configure")).IsEqualTo(1);
  }


  /// <summary>The assembly that actually carries the embedded templates.</summary>
  private static Assembly _templateAssembly
    => typeof(core_generators::Whizbang.Generators.MessageJsonContextGenerator).Assembly;

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_LeavesATemplateAloneWhenTheRegionIsNotThereAsync(MergedHost host) {
    // Migration scripts run to 50KB. Both guards return the template untouched rather than
    // splicing at a boundary that was never found -- a copy that lost them would write a
    // corrupted script that still looks like a script.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    const string template = "before\n#region KNOWN\nold\n#endregion\nafter";

    var missingRegion = (string)_call(
      host.Assembly, ns + "TemplateUtilities", "ReplaceRegion", template, "ABSENT", "new")!;
    await Assert.That(missingRegion).IsEqualTo(template)
      .Because("a region that is not present is not a licence to edit the template");

    var unterminated = (string)_call(
      host.Assembly, ns + "TemplateUtilities", "ReplaceRegion", "#region KNOWN\nold", "KNOWN", "new")!;
    await Assert.That(unterminated).IsEqualTo("#region KNOWN\nold")
      .Because("a region with no #endregion has no end to splice against");

    var replaced = (string)_call(
      host.Assembly, ns + "TemplateUtilities", "ReplaceRegion", template, "KNOWN", "new")!;
    await Assert.That(replaced).Contains("new")
      .Because("the baseline: a region that is present really is replaced");
    await Assert.That(replaced).DoesNotContain("old");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReadsEmbeddedSnippetsIdenticallyAsync(MergedHost host) {
    // The snippets are the literal text these generators emit. A copy that read them
    // differently would emit different source from the same template, in one package only.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    const string snippets = "MessageRegistrySnippets.cs";
    const string snippetNs = "Whizbang.Generators.Templates.Snippets";

    var entry = (string)_call(host.Assembly, ns + "TemplateUtilities", "ExtractSnippet",
      _templateAssembly, snippets, "MESSAGE_ENTRY_HEADER", snippetNs)!;
    await Assert.That(entry).IsNotNullOrEmpty();
    await Assert.That(entry).DoesNotContain("#region")
      .Because("the region markers delimit the snippet, they are not part of it");
    await Assert.That(entry).DoesNotStartWith("// ERROR")
      .Because("this region exists, so the not-found sentinel means the read itself broke");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReportsAMissingTemplateRatherThanEmptyAsync(MergedHost host) {
    // Returning "" for an absent resource would let a generator emit a file with the region
    // silently blank. The sentinel is what makes the failure visible in the generated output.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    const string snippetNs = "Whizbang.Generators.Templates.Snippets";

    var missingTemplate = (string)_call(host.Assembly, ns + "TemplateUtilities", "GetEmbeddedTemplate",
      _templateAssembly, "NoSuchTemplate.cs", snippetNs)!;
    await Assert.That(missingTemplate).Contains("ERROR")
      .Because("an absent template has to announce itself in the generated file");
    await Assert.That(missingTemplate).Contains("NoSuchTemplate.cs")
      .Because("the sentinel names what was missing, or it cannot be acted on");

    var missingRegion = (string)_call(host.Assembly, ns + "TemplateUtilities", "ExtractSnippet",
      _templateAssembly, "MessageRegistrySnippets.cs", "NO_SUCH_REGION", snippetNs)!;
    await Assert.That(missingRegion).Contains("ERROR");
    await Assert.That(missingRegion).Contains("NO_SUCH_REGION");
  }


  private sealed class StubConfigOptions(Dictionary<string, string> options) : AnalyzerConfigOptions {
    public override bool TryGetValue(
        string key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
      => options.TryGetValue(key, out value!);
  }

  private sealed class StubConfigOptionsProvider(Dictionary<string, string> options)
      : AnalyzerConfigOptionsProvider {
    public override AnalyzerConfigOptions GlobalOptions => new StubConfigOptions(options);
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new StubConfigOptions([]);
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new StubConfigOptions([]);
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReadsTableNameBuildPropertiesIdenticallyAsync(MergedHost host) {
    // These MSBuild properties decide the physical table names a project generates. Two packages
    // reading them differently means one strips a suffix the other keeps, and the same model
    // maps to two different tables depending on which generator emitted the mapping.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var options = new StubConfigOptions(new Dictionary<string, string> {
      ["build_property.WhizbangStripTableNameSuffixes"] = "false",
      ["build_property.WhizbangTableNameSuffixesToStrip"] = "Model, Dto"
    });

    var config = _call(host.Assembly, ns + "ConfigurationUtilities", "GetTableNameConfig", options)!;
    var configType = config.GetType();

    await Assert.That((bool)configType.GetProperty("StripSuffixes")!.GetValue(config)!).IsFalse()
      .Because("the property says not to strip, so the default of true has to be overridden");
    await Assert.That((string[])configType.GetProperty("SuffixesToStrip")!.GetValue(config)!)
      .Contains("Dto")
      .Because("the property is documented as comma-separated and the entries are "
             + "trimmed; parsing it wrong silently loses a suffix");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_RejectsAnUnusableIdentifierLengthOverrideAsync(MergedHost host) {
    // Returning 0 or a negative would let a generator truncate every identifier to nothing.
    // null means "use the provider's own limit", which is the only safe reading of bad input.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    const string key = "build_property.WhizbangMaxIdentifierLength";

    var valid = (int?)_call(host.Assembly, ns + "ConfigurationUtilities", "GetMaxIdentifierLengthOverride",
      new StubConfigOptions(new Dictionary<string, string> { [key] = "42" }));
    await Assert.That(valid).IsEqualTo(42);

    foreach (var bad in new[] { "0", "-1", "not-a-number", "  " }) {
      var rejected = (int?)_call(host.Assembly, ns + "ConfigurationUtilities", "GetMaxIdentifierLengthOverride",
        new StubConfigOptions(new Dictionary<string, string> { [key] = bad }));
      await Assert.That(rejected).IsNull()
        .Because($"'{bad}' is not a usable identifier length, so the provider default stands");
    }

    var absent = (int?)_call(host.Assembly, ns + "ConfigurationUtilities", "GetMaxIdentifierLengthOverride",
      new StubConfigOptions([]));
    await Assert.That(absent).IsNull();
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_SelectorsReadThroughToTheGlobalOptionsAsync(MergedHost host) {
    // The generators wire these into the incremental pipeline via Select, so this is the shape
    // the build actually calls. A copy whose selector ignored GlobalOptions would silently use
    // defaults for every project that configured them.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var provider = new StubConfigOptionsProvider(new Dictionary<string, string> {
      ["build_property.WhizbangMaxIdentifierLength"] = "31",
      ["build_property.WhizbangStripTableNameSuffixes"] = "false"
    });

    var length = (int?)_call(host.Assembly, ns + "ConfigurationUtilities",
      "SelectMaxIdentifierLengthOverride", provider, CancellationToken.None);
    await Assert.That(length).IsEqualTo(31);

    var config = _call(host.Assembly, ns + "ConfigurationUtilities",
      "SelectTableNameConfig", provider, CancellationToken.None)!;
    await Assert.That((bool)config.GetType().GetProperty("StripSuffixes")!.GetValue(config)!).IsFalse();
  }


  private const string ARRAY_ATTRIBUTE_SOURCE = """
    namespace App {
      [System.AttributeUsage(System.AttributeTargets.Class)]
      public sealed class TaggedAttribute : System.Attribute {
        public TaggedAttribute(string[] channels) { Channels = channels; }
        public string[] Channels { get; }
        public string[]? Tags { get; set; }
      }

      [Tagged(new[] { "orders", "billing" }, Tags = new[] { "public", "v2" })]
      public class TaggedModel { }
    }
    """;

  private static AttributeData _arrayAttribute() {
    var compilation = GeneratorTestHelper.CreateCompilation(ARRAY_ATTRIBUTE_SOURCE);
    var type = compilation.GetTypeByMetadataName("App.TaggedModel")
      ?? throw new InvalidOperationException("test compilation did not produce App.TaggedModel");
    return type.GetAttributes().First(a => a.AttributeClass?.Name == "TaggedAttribute");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_ReadsStringArrayArgumentsFromEitherPositionAsync(MergedHost host) {
    // An array argument can arrive named or positional, and the generators read both. A copy
    // that handled only one would drop the list and fall back to null, which downstream reads
    // as "no channels configured" rather than as a value it failed to parse.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var attribute = _arrayAttribute();

    var named = (string[]?)_call(
      host.Assembly, ns + "AttributeUtilities", "GetStringArrayValue", attribute, "Tags");
    await Assert.That(named).IsNotNull();
    await Assert.That(named!).Contains("public");
    await Assert.That(named!).Contains("v2");

    var positional = (string[]?)_call(
      host.Assembly, ns + "AttributeUtilities", "GetStringArrayValue", attribute, "channels");
    await Assert.That(positional).IsNotNull()
      .Because("the constructor parameter is matched by name, case-insensitively");
    await Assert.That(positional!).Contains("orders");

    var absent = (string[]?)_call(
      host.Assembly, ns + "AttributeUtilities", "GetStringArrayValue", attribute, "Missing");
    await Assert.That(absent).IsNull()
      .Because("an argument that is not there is null, not an empty array a caller would use");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_GeneratesPerspectiveTableNamesIdenticallyAsync(MergedHost host) {
    // This is the physical table a perspective reads and writes. Two packages disagreeing here
    // means one of them queries a table the other never created, and the mismatch only appears
    // once both generators have run over the same model.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var configType = _type(host.Assembly, "Whizbang.Generators.Shared.Models.TableNameConfig");
    var stripping = Activator.CreateInstance(configType, true, _tableSuffixes)!;
    var keeping = Activator.CreateInstance(configType, false, _tableSuffixes)!;

    var stripped = (string)_call(host.Assembly, ns + "NamingConventionUtilities",
      "GenerateTableName", "OrderProjection", stripping)!;
    await Assert.That(stripped).IsEqualTo("wh_per_order")
      .Because("the suffix is stripped before the snake_case conversion, not after");

    var kept = (string)_call(host.Assembly, ns + "NamingConventionUtilities",
      "GenerateTableName", "OrderProjection", keeping)!;
    await Assert.That(kept).IsEqualTo("wh_per_order_projection")
      .Because("stripping disabled has to keep the suffix, or the table silently changes name");
  }

  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_StripsTheFirstMatchingSuffixOnlyAsync(MergedHost host) {
    // First match wins, and the guards return the name untouched. Stripping twice would turn
    // OrderModelProjection into Order and collapse two distinct models onto one table.
    const string ns = "Whizbang.Generators.Shared.Utilities.";
    var configType = _type(host.Assembly, "Whizbang.Generators.Shared.Models.TableNameConfig");
    var config = Activator.CreateInstance(configType, true, _tableSuffixes)!;
    var noSuffixes = Activator.CreateInstance(configType, true, _noSuffixes)!;

    var once = (string)_call(host.Assembly, ns + "NamingConventionUtilities",
      "StripConfigurableSuffixes", "OrderModelProjection", config)!;
    await Assert.That(once).IsEqualTo("OrderModel")
      .Because("only the first matching suffix comes off; the result is not re-examined");

    var untouched = (string)_call(host.Assembly, ns + "NamingConventionUtilities",
      "StripConfigurableSuffixes", "OrderProjection", noSuffixes)!;
    await Assert.That(untouched).IsEqualTo("OrderProjection")
      .Because("an empty suffix list strips nothing rather than everything");

    var empty = (string)_call(host.Assembly, ns + "NamingConventionUtilities",
      "StripConfigurableSuffixes", "", config)!;
    await Assert.That(empty).IsEqualTo("");
  }


  [Test]
  [MethodDataSource(nameof(Hosts))]
  public async Task MergedCopy_PassesTheSharedAssemblysOwnSelfTestAsync(MergedHost host) {
    // IdentifierValidation's methods take an IDbProviderLimits, and each merged copy has its own
    // type identity for that interface -- so no class declared in THIS assembly can satisfy all
    // four copies, and the reflection tests above structurally cannot reach that surface.
    //
    // The self-test lives inside the shared assembly instead, carrying its own limits
    // implementation. ILRepack merges both into every host, so each copy holds an implementation
    // whose identity already matches its own interface. Calling it here is the only way this
    // surface gets exercised in the three hosts where the types are internal.
    var failures = (IReadOnlyList<string>)_call(
      host.Assembly, "Whizbang.Generators.Shared.Diagnostics.SharedSelfTest", "Run")!;

    await Assert.That(failures).IsEmpty()
      .Because($"{host.Host} carries its own copy of the identifier validation, and a copy that "
             + "diverged would truncate identifiers in the database while reporting success");
  }

}
