using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a perspective model that a request can sort or filter by while carrying no index on the
/// fields such a request could name.
/// </summary>
/// <remarks>
/// <para>
/// WHIZ302 reads source: it finds a filter because somebody wrote <c>Where(...)</c> where it could be
/// seen. A surface that composes sorting or filtering from the request writes no predicate anywhere,
/// because the field name arrives as a string and the <c>ORDER BY</c> is built when the request is
/// served. So WHIZ302 is silent on exactly the models that are queried the most, and its silence
/// there means nothing at all.
/// </para>
/// <para>
/// One analyzer rather than one per integration. The attribute marker is what lets an integration
/// participate, and it is readable from any compilation that can see the marked attribute, so a
/// second analyzer in each transport package would duplicate this logic and double-report every
/// model reachable through both. The transports participate by marking their own attributes, which is
/// the same door a third party's package uses.
/// </para>
/// <para>
/// Reported on the surface, not the model. The surface is the decision that created the exposure, it
/// is in the compilation being built, and it is where an author can act; the model may be in a
/// referenced assembly with no source to squiggle.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/QueryExposureIndexAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class QueryExposureIndexAnalyzer : DiagnosticAnalyzer {
  /// <summary>
  /// Fully qualified attribute names a consumer declares as query-composing, comma separated.
  /// </summary>
  /// <remarks>
  /// The last resort of the three ways in. The marker travels with the package that defines the
  /// attribute and the built-in list covers what the framework integrates with; this covers a third
  /// party's attribute that neither reaches. It is last because it is the only one that can go stale
  /// without saying so.
  /// </remarks>
  private const string EXTRA_ATTRIBUTES_OPTION = "build_property.WhizbangQueryComposingAttributes";

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [SortableExposureDiscovery.SortableFieldsHaveNoIndex];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    context.RegisterSymbolAction(_analyzeLens, SymbolKind.NamedType);
    context.RegisterSymbolAction(_analyzeQueryMember, SymbolKind.Method, SymbolKind.Property);
  }

  /// <summary>A lens declaration: the attribute is on the type, the model is in its interface.</summary>
  private static void _analyzeLens(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;

    if (!SortableExposureDiscovery.AllowsOrdering(_exposureOf(type, context))) {
      return;
    }

    foreach (var model in SortableExposureDiscovery.ModelsOfLens(type)) {
      _report(context, type, model);
    }
  }

  /// <summary>A resolver: the attribute is on the member, the model is under what it hands back.</summary>
  /// <remarks>
  /// Properties as well as methods, because a query surface is commonly an expression-bodied property
  /// and the middleware attaches to either the same way.
  /// </remarks>
  private static void _analyzeQueryMember(SymbolAnalysisContext context) {
    if (!SortableExposureDiscovery.AllowsOrdering(_exposureOf(context.Symbol, context))) {
      return;
    }

    var returned = context.Symbol switch {
      IMethodSymbol method => method.ReturnType,
      IPropertySymbol property => property.Type,
      _ => null,
    };

    if (SortableExposureDiscovery.ModelOfQueryable(returned) is { } model) {
      _report(context, context.Symbol, model);
    }
  }

  private static int _exposureOf(ISymbol symbol, SymbolAnalysisContext context) {
    var attributes = symbol.GetAttributes();

    // The overwhelmingly common case is a symbol with no attributes at all, and reading the
    // configuration for each of those would charge every symbol in the compilation for a feature
    // almost nothing uses.
    return attributes.Length == 0
      ? 0
      : SortableExposureDiscovery.ExposureOf(attributes, _extraComposingNames(context));
  }

  /// <summary>The consumer-configured attribute names, empty when none are configured.</summary>
  private static ImmutableArray<string> _extraComposingNames(SymbolAnalysisContext context) {
    if (!context.Options.AnalyzerConfigOptionsProvider.GlobalOptions
        .TryGetValue(EXTRA_ATTRIBUTES_OPTION, out var configured)
        || string.IsNullOrWhiteSpace(configured)) {
      return [];
    }

    return [.. configured
      .Split(',')
      .Select(static name => name.Trim())
      .Where(static name => name.Length > 0)];
  }

  /// <summary>Reports the model's unaccounted-for fields, if it has any.</summary>
  private static void _report(SymbolAnalysisContext context, ISymbol surface, INamedTypeSymbol model) {
    var unattributed = SortableExposureDiscovery.UnattributedFields(model);
    if (unattributed.Length == 0) {
      return;
    }

    var location = surface.Locations.FirstOrDefault(static l => l.IsInSource);
    if (location is null) {
      return;   // the surface is in metadata, so there is nothing here for an author to act on
    }

    context.ReportDiagnostic(Diagnostic.Create(
      SortableExposureDiscovery.SortableFieldsHaveNoIndex,
      location,
      model.Name,
      unattributed.Length,
      string.Join(", ", unattributed)));
  }
}
