extern alias core_generators;
extern alias fastendpoints_generators;
extern alias hotchocolate_generators;
extern alias postgres_generators;
using System.Reflection;

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
}
