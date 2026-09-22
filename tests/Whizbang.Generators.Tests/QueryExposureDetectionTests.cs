using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Recognizing a surface that lets the request shape the query, and finding the model behind it.
/// </summary>
/// <remarks>
/// <para>
/// This is the half the ordinary advisory cannot do. A predicate written in source can be read from
/// source; one composed when a request arrives leaves nothing to read, and the only durable trace is
/// the attribute that put the middleware there. So the attribute is the evidence, and these tests fix
/// what counts as evidence and what the model behind it is.
/// </para>
/// <para>
/// Three ways an attribute can qualify, tested separately because they fail separately. The marker
/// travels with the package that defines it and is the path anything referencing the framework should
/// take. The known names cover what the framework integrates with but does not own. Configuration
/// covers a third party's attribute that neither reaches.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
public class QueryExposureDetectionTests {
  private const int FILTERING = 1;
  private const int ORDERING = 2;
  private const int EXPRESSION = 4;

  private static Compilation _compile(string source) =>
    AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);

  private static int _exposureOfType(string source, string typeName = "TestApp.Surface") {
    var compilation = _compile(source);
    var type = compilation.GetTypeByMetadataName(typeName);

    // An attribute that fails to bind is indistinguishable from one nobody wrote, so a test
    // expecting "no exposure" would pass for the wrong reason and this file would be measuring the
    // reference list instead of the behavior.
    _rejectUnboundAttributes(compilation);

    return type is null
      ? throw new InvalidOperationException($"{typeName} was not found in the test compilation")
      : SortableExposureDiscovery.ExposureOf(type.GetAttributes(), []);
  }

  private static int _exposureOfMethod(string source, string methodName = "Query") {
    var compilation = _compile(source);
    _rejectUnboundAttributes(compilation);

    var method = compilation.GetTypeByMetadataName("TestApp.Surface")
      ?.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault()
      ?? throw new InvalidOperationException($"{methodName} was not found in the test compilation");

    return SortableExposureDiscovery.ExposureOf(method.GetAttributes(), []);
  }

  private static void _rejectUnboundAttributes(Compilation compilation) {
    var unbound = compilation.GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Where(m => m.Contains("ComposesQueryFromRequest", StringComparison.Ordinal)
        || m.Contains("QueryExposures", StringComparison.Ordinal)
        || m.Contains("ILensQuery", StringComparison.Ordinal)
        || m.Contains("PerspectiveRow", StringComparison.Ordinal))
      .ToList();

    if (unbound.Count > 0) {
      throw new InvalidOperationException(
        "the framework types did not bind, so this test would measure the harness: "
        + string.Join("; ", unbound.Take(3)));
    }
  }

  /// <summary>An attribute nobody marked says nothing, which is the common case.</summary>
  [Test]
  public async Task AnUnmarkedAttributeDeclaresNoExposureAsync() {
    var exposure = _exposureOfType("""
      using System;

      namespace TestApp;

      [AttributeUsage(AttributeTargets.Class)]
      public sealed class SomeOtherAttribute : Attribute { }

      [SomeOther]
      public class Surface { }
      """);

    await Assert.That(exposure).IsEqualTo(0)
      .Because("most attributes have nothing to do with queries and must not be reported");
  }

  /// <summary>The marker on an attribute covers everything carrying that attribute.</summary>
  /// <remarks>
  /// This is the extensibility answer. The framework never sees the third party's attribute, and does
  /// not need to: the claim travels on the attribute's own declaration.
  /// </remarks>
  [Test]
  public async Task AMarkedAttributeDeclaresItsExposureAsync() {
    var exposure = _exposureOfType("""
      using System;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [ComposesQueryFromRequest]
      [AttributeUsage(AttributeTargets.Class)]
      public sealed class ThirdPartySortingAttribute : Attribute { }

      [ThirdPartySorting]
      public class Surface { }
      """);

    await Assert.That(exposure).IsEqualTo(ORDERING | FILTERING)
      .Because("the marker's default is what a sorting and filtering middleware offers");
  }

  /// <summary>
  /// A domain language over expressions declares the widest exposure there is.
  /// </summary>
  /// <remarks>
  /// The case that makes the marker worth having over a list of known names: an extension that turns
  /// a request into an arbitrary predicate is invisible to any list the framework could ship, while
  /// being the exposure most worth reporting.
  /// </remarks>
  [Test]
  public async Task AnExpressionSurfaceDeclaresTheWidestExposureAsync() {
    var exposure = _exposureOfType("""
      using System;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [ComposesQueryFromRequest(QueryExposures.Expression)]
      [AttributeUsage(AttributeTargets.Class)]
      public sealed class UseExpressionAttribute : Attribute { }

      [UseExpression]
      public class Surface { }
      """);

    await Assert.That(exposure).IsEqualTo(EXPRESSION);
    await Assert.That(SortableExposureDiscovery.AllowsOrdering(exposure)).IsTrue()
      .Because("an arbitrary expression can produce an ORDER BY over any field");
  }

  /// <summary>
  /// An attribute the framework integrates with but does not own is recognized by name.
  /// </summary>
  /// <remarks>
  /// Matching is by fully qualified name precisely so the test can declare the type itself. What is
  /// verified is the name list, not a package reference.
  /// </remarks>
  /// <summary>
  /// A known name grants what that middleware actually offers, not both.
  /// </summary>
  /// <remarks>
  /// Returning ordering for a filtering-only surface is the false positive this whole design claims
  /// to avoid: WHIZ306 reports only ordering, so a name list that hands ordering to
  /// <c>[UseFiltering]</c> reports every filterable collection as an unindexed sort. Measured against
  /// a real consumer, that was 78 repositories offering filtering alone and still being reported.
  /// </remarks>
  [Test]
  public async Task AFilteringOnlyNameGrantsFilteringOnlyAsync() {
    var exposure = _exposureOfType("""
      using System;

      namespace HotChocolate.Data {
        [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
        public sealed class UseFilteringAttribute : Attribute { }
      }

      namespace TestApp {
        [global::HotChocolate.Data.UseFiltering]
        public class Surface { }
      }
      """);

    await Assert.That(exposure).IsEqualTo(FILTERING);
    await Assert.That(SortableExposureDiscovery.AllowsOrdering(exposure)).IsFalse()
      .Because("a filtering middleware narrows rows and does not choose their order");
  }

  /// <summary>A sorting name grants ordering.</summary>
  [Test]
  public async Task ASortingOnlyNameGrantsOrderingAsync() {
    var exposure = _exposureOfType("""
      using System;

      namespace HotChocolate.Data {
        [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
        public sealed class UseSortingAttribute : Attribute { }
      }

      namespace TestApp {
        [global::HotChocolate.Data.UseSorting]
        public class Surface { }
      }
      """);

    await Assert.That(exposure).IsEqualTo(ORDERING);
    await Assert.That(SortableExposureDiscovery.AllowsOrdering(exposure)).IsTrue();
  }

  [Test]
  [Arguments("UseSortingAttribute")]
  [Arguments("UseFilteringAttribute")]
  public async Task AKnownComposingAttributeIsRecognizedByNameAsync(string attributeName) {
    var exposure = _exposureOfType($$"""
      using System;

      namespace HotChocolate.Data {
        [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
        public sealed class {{attributeName}} : Attribute { }
      }

      namespace TestApp {
        [global::HotChocolate.Data.{{attributeName}}]
        public class Surface { }
      }
      """);

    await Assert.That(exposure).IsNotEqualTo(0)
      .Because("both names are recognized; which capability each grants is pinned above");
  }

  /// <summary>A consumer can name an attribute the framework cannot reach.</summary>
  [Test]
  public async Task AConfiguredAttributeNameIsRecognizedAsync() {
    var compilation = _compile("""
      using System;

      namespace Vendor {
        [AttributeUsage(AttributeTargets.Class)]
        public sealed class QueryableAttribute : Attribute { }
      }

      namespace TestApp {
        [global::Vendor.Queryable]
        public class Surface { }
      }
      """);

    var type = compilation.GetTypeByMetadataName("TestApp.Surface")!;

    await Assert.That(SortableExposureDiscovery.ExposureOf(type.GetAttributes(), []))
      .IsEqualTo(0)
      .Because("nothing in the source says this attribute composes a query");

    await Assert.That(
        SortableExposureDiscovery.ExposureOf(type.GetAttributes(), ["Vendor.QueryableAttribute"]))
      .IsEqualTo(ORDERING | FILTERING)
      .Because("configuration is the way in for an attribute neither the framework nor its author "
        + "can mark");
  }

  /// <summary>A surface that turns sorting off is not exposing ordering.</summary>
  /// <remarks>
  /// The framework's own lens attributes default both switches on and let the author turn either off.
  /// Reporting ordering anyway would be advice about something the surface does not offer, and the
  /// author would be right to ignore it.
  /// </remarks>
  [Test]
  public async Task ASwitchedOffCapabilityIsRemovedAsync() {
    const string SOURCE = """
      using System;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [ComposesQueryFromRequest]
      [AttributeUsage(AttributeTargets.Class)]
      public sealed class LensAttribute : Attribute {
        public bool EnableSorting { get; set; } = true;
        public bool EnableFiltering { get; set; } = true;
      }

      [Lens(EnableSorting = false)]
      public class Surface { }

      [Lens(EnableFiltering = false)]
      public class FilteringOff { }

      [Lens(EnableSorting = false, EnableFiltering = false)]
      public class BothOff { }
      """;

    await Assert.That(_exposureOfType(SOURCE)).IsEqualTo(FILTERING)
      .Because("filtering is still on offer, ordering is not");
    await Assert.That(_exposureOfType(SOURCE, "TestApp.FilteringOff")).IsEqualTo(ORDERING);
    await Assert.That(_exposureOfType(SOURCE, "TestApp.BothOff")).IsEqualTo(0)
      .Because("a surface offering neither composes nothing from the request");
  }

  /// <summary>The switches only narrow, so leaving them on keeps what was declared.</summary>
  [Test]
  public async Task SwitchesLeftOnChangeNothingAsync() {
    var exposure = _exposureOfType("""
      using System;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [ComposesQueryFromRequest]
      [AttributeUsage(AttributeTargets.Class)]
      public sealed class LensAttribute : Attribute {
        public bool EnableSorting { get; set; } = true;
      }

      [Lens(EnableSorting = true)]
      public class Surface { }
      """);

    await Assert.That(exposure).IsEqualTo(ORDERING | FILTERING);
  }

  /// <summary>Two surfaces over one declaration combine rather than replace.</summary>
  [Test]
  public async Task SeveralAttributesCombineAsync() {
    var exposure = _exposureOfMethod("""
      using System;
      using System.Linq;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [ComposesQueryFromRequest(QueryExposures.Ordering)]
      [AttributeUsage(AttributeTargets.Method)]
      public sealed class SortsAttribute : Attribute { }

      [ComposesQueryFromRequest(QueryExposures.Filtering)]
      [AttributeUsage(AttributeTargets.Method)]
      public sealed class FiltersAttribute : Attribute { }

      public class Model { public string Name { get; set; } = string.Empty; }

      public class Surface {
        [Sorts]
        [Filters]
        public IQueryable<Model> Query() => throw new NotImplementedException();
      }
      """);

    await Assert.That(exposure).IsEqualTo(ORDERING | FILTERING)
      .Because("each middleware states only what it adds");
  }

  /// <summary>A resolver hands back the query, and the model is under it.</summary>
  [Test]
  [Arguments("IQueryable<Model>")]
  [Arguments("IOrderedQueryable<Model>")]
  [Arguments("System.Threading.Tasks.Task<IQueryable<Model>>")]
  [Arguments("PerspectiveRow<Model>>")]
  public async Task TheModelUnderAQueryableIsFoundAsync(string returnType) {
    var declared = returnType == "PerspectiveRow<Model>>" ? "IQueryable<PerspectiveRow<Model>>" : returnType;

    var compilation = _compile($$"""
      using System;
      using System.Linq;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      public class Model { public string Name { get; set; } = string.Empty; }

      public class Surface {
        public {{declared}} Query() => throw new NotImplementedException();
      }
      """);

    _rejectUnboundAttributes(compilation);

    var method = compilation.GetTypeByMetadataName("TestApp.Surface")!
      .GetMembers("Query").OfType<IMethodSymbol>().Single();

    var model = SortableExposureDiscovery.ModelOfQueryable(method.ReturnType);

    await Assert.That(model?.Name).IsEqualTo("Model")
      .Because("the fields a request can order by belong to the model, however it is wrapped");
  }

  /// <summary>
  /// A member that has already run its query is not an exposure.
  /// </summary>
  /// <remarks>
  /// Nothing can add an <c>ORDER BY</c> to a materialized list, so the sort happens in memory over
  /// rows already read and an index would not change what the database did.
  /// </remarks>
  [Test]
  [Arguments("System.Collections.Generic.List<Model>")]
  [Arguments("Model")]
  public async Task AMaterializedReturnIsNotAQueryableAsync(string returnType) {
    var compilation = _compile($$"""
      using System;

      namespace TestApp;

      public class Model { public string Name { get; set; } = string.Empty; }

      public class Surface {
        public {{returnType}} Query() => throw new NotImplementedException();
      }
      """);

    var method = compilation.GetTypeByMetadataName("TestApp.Surface")!
      .GetMembers("Query").OfType<IMethodSymbol>().Single();

    await Assert.That(SortableExposureDiscovery.ModelOfQueryable(method.ReturnType)).IsNull();
  }

  /// <summary>A lens names its model by implementing the query interface over it.</summary>
  [Test]
  public async Task ALensNamesItsModelThroughTheQueryInterfaceAsync() {
    var compilation = _compile("""
      using Whizbang.Core.Lenses;

      namespace TestApp;

      public class Model { public string Name { get; set; } = string.Empty; }

      public interface IModelLens : ILensQuery<Model>;
      """);

    _rejectUnboundAttributes(compilation);

    var models = SortableExposureDiscovery.ModelsOfLens(
      compilation.GetTypeByMetadataName("TestApp.IModelLens"));

    await Assert.That(models.Select(m => m.Name)).IsEquivalentTo(["Model"]);
  }

  /// <summary>A lens over several models exposes all of them through the one surface.</summary>
  /// <remarks>
  /// Reporting only the first would under-report exactly the widest surfaces, which are the ones
  /// worth reporting.
  /// </remarks>
  [Test]
  public async Task AMultiModelLensNamesEveryModelAsync() {
    var compilation = _compile("""
      using Whizbang.Core.Lenses;

      namespace TestApp;

      public class Left { public string Name { get; set; } = string.Empty; }
      public class Right { public string Name { get; set; } = string.Empty; }

      public interface IPairLens : ILensQuery<Left, Right>;
      """);

    _rejectUnboundAttributes(compilation);

    var models = SortableExposureDiscovery.ModelsOfLens(
      compilation.GetTypeByMetadataName("TestApp.IPairLens"));

    await Assert.That(models.Select(m => m.Name)).IsEquivalentTo(["Left", "Right"]);
  }

  /// <summary>A queryable of something that is not a named type exposes no model.</summary>
  /// <remarks>
  /// An array element is the reachable case: a surface handing back a queryable of arrays is not a
  /// perspective query, and reading its element as a model would invent one.
  /// </remarks>
  [Test]
  public async Task AQueryableOfANonNamedTypeHasNoModelAsync() {
    var compilation = _compile("""
      using System.Linq;

      namespace TestApp;

      public class Surface {
        public IQueryable<string[]> Query() => throw new System.NotImplementedException();
      }
      """);

    var method = compilation.GetTypeByMetadataName("TestApp.Surface")!
      .GetMembers("Query").OfType<IMethodSymbol>().Single();

    await Assert.That(SortableExposureDiscovery.ModelOfQueryable(method.ReturnType)).IsNull();
  }

  /// <summary>
  /// A symbol kind that is not a surface exposes no model, rather than being guessed at.
  /// </summary>
  /// <remarks>
  /// The shared dispatch answers for a lens type, a method and a property. A field is none of those,
  /// and the arm that says so is what lets both callers pass whatever they hold without each of them
  /// repeating the check.
  /// </remarks>
  [Test]
  public async Task ASymbolThatIsNotASurfaceHasNoModelAsync() {
    var compilation = _compile("""
      namespace TestApp;

      public class Surface {
        public int Field;
      }
      """);

    var field = compilation.GetTypeByMetadataName("TestApp.Surface")!
      .GetMembers("Field").OfType<IFieldSymbol>().Single();

    await Assert.That(SortableExposureDiscovery.ModelExposedBy(field)).IsNull();
    await Assert.That(SortableExposureDiscovery.ModelExposedBy(null)).IsNull();
  }

  /// <summary>The shared dispatch reads each surface shape the same way its callers do.</summary>
  [Test]
  public async Task TheSharedDispatchReadsEverySurfaceShapeAsync() {
    var compilation = _compile("""
      using System.Linq;
      using Whizbang.Core.Lenses;

      namespace TestApp;

      public class Model { public string Name { get; set; } = string.Empty; }

      public interface IModelLens : ILensQuery<Model>;

      public class Surface {
        public IQueryable<Model> Query() => throw new System.NotImplementedException();
        public IQueryable<Model> Queryable => throw new System.NotImplementedException();
      }
      """);

    _rejectUnboundAttributes(compilation);
    var surface = compilation.GetTypeByMetadataName("TestApp.Surface")!;

    await Assert.That(SortableExposureDiscovery.ModelExposedBy(
      compilation.GetTypeByMetadataName("TestApp.IModelLens"))?.Name).IsEqualTo("Model");
    await Assert.That(SortableExposureDiscovery.ModelExposedBy(
      surface.GetMembers("Query").OfType<IMethodSymbol>().Single())?.Name).IsEqualTo("Model");
    await Assert.That(SortableExposureDiscovery.ModelExposedBy(
      surface.GetMembers("Queryable").OfType<IPropertySymbol>().Single())?.Name).IsEqualTo("Model");
  }

  /// <summary>A type that queries nothing has no model, and is not a mistake to be reported.</summary>  /// <summary>A type that queries nothing has no model, and is not a mistake to be reported.</summary>
  [Test]
  public async Task ATypeThatIsNotALensHasNoModelAsync() {
    var compilation = _compile("""
      namespace TestApp;

      public interface INotALens { void Go(); }
      """);

    await Assert.That(SortableExposureDiscovery.ModelsOfLens(
      compilation.GetTypeByMetadataName("TestApp.INotALens"))).IsEmpty();
    await Assert.That(SortableExposureDiscovery.ModelsOfLens(null)).IsEmpty();
  }
}
