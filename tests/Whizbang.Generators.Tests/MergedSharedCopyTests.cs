extern alias core_generators;
extern alias fastendpoints_generators;
extern alias hotchocolate_generators;
extern alias postgres_generators;
using System.Reflection;
using Microsoft.CodeAnalysis;

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

}
