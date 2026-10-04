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
/// <para>
/// Filtering is the second thing a request can compose, and it matters for a different index. An
/// equality or <c>in</c> filter on a field with no index of its own compiles to a whole-document
/// match, answered only by the index over the whole document. A model exposed to request-composed
/// filtering that declares <c>[PerspectiveQueries(MatchOnAnyField = false)]</c> while it still has
/// such fields is reported (WHIZ307), and one that declares nothing is noted (WHIZ308), on the same
/// surface and for the same reason: no source shows the filter.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <docs>operations/diagnostics/whiz307</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/QueryExposureIndexAnalyzerTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/QueryExposureDocumentMatchTests.cs</tests>
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
      [
        SortableExposureDiscovery.SortableFieldsHaveNoIndex,
        PerspectiveQueriesDiscovery.WholeDocumentMatchHasNoIndex,
        PerspectiveQueriesDiscovery.WholeDocumentMatchReliesOnDefault,
      ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    context.RegisterSymbolAction(_analyzeLens, SymbolKind.NamedType);
    context.RegisterSymbolAction(_analyzeMethod, SymbolKind.Method);
    context.RegisterSymbolAction(_analyzeProperty, SymbolKind.Property);
  }

  /// <summary>A lens declaration: the attribute is on the type, the model is in its interface.</summary>
  private static void _analyzeLens(SymbolAnalysisContext context) {
    var type = (INamedTypeSymbol)context.Symbol;
    var exposure = _exposureOf(type, context);
    if (exposure == 0) {
      return;
    }

    foreach (var model in SortableExposureDiscovery.ModelsOfLens(type)) {
      _reportExposure(context, type, model, exposure);
    }
  }

  /// <summary>A resolver method: the model is under what it hands back.</summary>
  private static void _analyzeMethod(SymbolAnalysisContext context) =>
    _analyzeQueryMember(context, ((IMethodSymbol)context.Symbol).ReturnType);

  /// <summary>
  /// A resolver written as a property, which the middleware attaches to the same way.
  /// </summary>
  private static void _analyzeProperty(SymbolAnalysisContext context) =>
    _analyzeQueryMember(context, ((IPropertySymbol)context.Symbol).Type);

  /// <summary>The shape both member kinds share, once the declared type is in hand.</summary>
  /// <remarks>
  /// Registered per symbol kind and cast at the entry point rather than switched on here, so there is
  /// no arm for a kind this analyzer never asks for. An unreachable arm is a line no test can honestly
  /// cover and a claim that the code handles a case it has never seen.
  /// </remarks>
  private static void _analyzeQueryMember(SymbolAnalysisContext context, ITypeSymbol returned) {
    var exposure = _exposureOf(context.Symbol, context);
    if (exposure == 0) {
      return;
    }

    if (SortableExposureDiscovery.ModelOfQueryable(returned) is { } model) {
      _reportExposure(context, context.Symbol, model, exposure);
    }
  }

  /// <summary>Each question the exposure raises, for one model it reaches.</summary>
  private static void _reportExposure(SymbolAnalysisContext context, ISymbol surface, INamedTypeSymbol model, int exposure) {
    if (SortableExposureDiscovery.AllowsOrdering(exposure)) {
      _report(context, surface, model);
    }

    if (SortableExposureDiscovery.AllowsFiltering(exposure)) {
      _reportDocumentMatch(context, surface, model);
    }
  }

  /// <summary>
  /// A model a request can filter, checked against what it declares about whole-document matches.
  /// </summary>
  /// <remarks>
  /// Only the fields nothing accounts for can reach a whole-document match: a field with an ordered
  /// index of its own keeps that index for equality and for <c>in</c>, a promoted field is a column,
  /// and a model that records a decision wholesale has made one. So the same list WHIZ306 reports is
  /// the list that decides this, and an empty one means there is nothing to say.
  /// </remarks>
  private static void _reportDocumentMatch(SymbolAnalysisContext context, ISymbol surface, INamedTypeSymbol model) {
    var declared = PerspectiveQueriesDiscovery.From(model).AnyField;
    if (declared == DocumentMatchDeclaration.On) {
      return;
    }

    var unattributed = SortableExposureDiscovery.UnattributedFields(model);
    if (unattributed.Length == 0) {
      return;
    }

    var subject = $"An equality or 'in' filter a request composes on '{model.Name}' ({string.Join(", ", unattributed)})";
    var location = surface.Locations.FirstOrDefault() ?? Location.None;

    context.ReportDiagnostic(declared == DocumentMatchDeclaration.Off
      ? Diagnostic.Create(
          PerspectiveQueriesDiscovery.WholeDocumentMatchHasNoIndex,
          location,
          subject,
          $"'{model.Name}' declares [PerspectiveQueries(MatchOnAnyField = false)]",
          "Mark each field a request can filter on [Indexed], declare MatchOnAnyField = true, or record the "
            + "decision with [SuppressIndexAdvisory(\"reason\")]")
      : Diagnostic.Create(
          PerspectiveQueriesDiscovery.WholeDocumentMatchReliesOnDefault,
          location,
          subject,
          model.Name,
          "; a request can name any of these fields, so the only other answer is to mark each of them [Indexed]"));
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

    // A symbol action only fires for symbols this compilation declares, so a source location is
    // always there; the fallback exists so a surprise cannot throw inside an analyzer, where the
    // failure would surface as AD0001 rather than as anything an author could read.
    context.ReportDiagnostic(Diagnostic.Create(
      SortableExposureDiscovery.SortableFieldsHaveNoIndex,
      surface.Locations.FirstOrDefault() ?? Location.None,
      model.Name,
      unattributed.Length,
      string.Join(", ", unattributed)));
  }
}
