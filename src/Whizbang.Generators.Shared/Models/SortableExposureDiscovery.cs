using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The fields a client could sort or filter on that carry no index and no recorded decision.
/// </summary>
/// <remarks>
/// <para>
/// The index advisory reads source. It finds a filter because someone wrote <c>Where(...)</c> where
/// it can see it. An integration that composes sorting and filtering from a request does not write
/// that predicate anywhere: the field name arrives as a string and the <c>ORDER BY</c> is built at
/// request time. So the advisory is silent on exactly the models that are queried the most, and its
/// silence there means nothing.
/// </para>
/// <para>
/// What is visible is the exposure. A method carrying HotChocolate's sorting or filtering
/// middleware, or a lens marked for a REST surface with sorting enabled, is a statement that any
/// field of the model may end up in an <c>ORDER BY</c>. That statement can be checked, and this is
/// what checks it.
/// </para>
/// <para>
/// Deliberately not "every field must be indexed". The set a client can actually sort by is usually
/// a handful, and indexing fifty-three fields to silence a warning would be worse than the warning.
/// The answer names how many fields are unaccounted for and leaves the choice to the author, whose
/// options are to index the ones the surface really offers, to index them all, or to record that a
/// scan is acceptable here.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Generators.Tests/SortableExposureDiscoveryTests.cs</tests>
public static class SortableExposureDiscovery {
  private const string INDEX_ALL_FIELDS = "Whizbang.Core.Perspectives.IndexAllFieldsAttribute";
  private const string PHYSICAL_FIELD = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string VECTOR_FIELD = "Whizbang.Core.Perspectives.VectorFieldAttribute";
  private const string STREAM_ID = "Whizbang.Core.StreamIdAttribute";
  private const string SUPPRESS = "Whizbang.Core.Perspectives.SuppressIndexAdvisoryAttribute";

  private const string MARKER = "Whizbang.Core.Perspectives.ComposesQueryFromRequestAttribute";
  private const string LENS_QUERY = "Whizbang.Core.Lenses.ILensQuery";
  private const string PERSPECTIVE_ROW = "Whizbang.Core.Lenses.PerspectiveRow<";

  /// <summary>
  /// The attribute names that compose a query from the request but cannot carry the marker.
  /// </summary>
  /// <remarks>
  /// A third party's attribute cannot be annotated by this assembly, so the ones the framework ships
  /// an integration for are named here. Anything else is reached either by the marker, which travels
  /// with the package that defines it, or by an analyzer configuration option, which travels with
  /// the consumer. Naming is the last resort of the three because it is the only one that goes stale
  /// silently.
  /// </remarks>
  private static readonly string[] _knownComposingAttributes = [
    "HotChocolate.Data.UseSortingAttribute",
    "HotChocolate.Data.UseFilteringAttribute",
    "HotChocolate.Types.UseSortingAttribute",
    "HotChocolate.Types.UseFilteringAttribute",
  ];

  /// <summary>
  /// What a request can shape, given the attributes on a method or a lens declaration.
  /// </summary>
  /// <param name="attributes">The attributes on the method or type.</param>
  /// <param name="extraComposingNames">
  /// Fully qualified attribute names a consumer has declared as query-composing, from analyzer
  /// configuration. Empty is the common case.
  /// </param>
  /// <returns>The combined exposure, or none when nothing composes a query here.</returns>
  /// <remarks>
  /// <para>
  /// Three ways in, on purpose. The marker is how anything that can reference the framework opts in,
  /// including a domain language over expressions the framework knows nothing about. The known names
  /// cover the attributes the framework integrates with but does not own. Configuration covers the
  /// rest.
  /// </para>
  /// <para>
  /// An <c>EnableSorting</c> or <c>EnableFiltering</c> property set to false narrows what the marker
  /// declared, because a surface that turns sorting off is not exposing ordering however the
  /// attribute is marked.
  /// </para>
  /// </remarks>
  public static int ExposureOf(
      ImmutableArray<AttributeData> attributes, ImmutableArray<string> extraComposingNames) {
    var exposure = 0;

    foreach (var attribute in attributes) {
      var declared = _declaredExposure(attribute, extraComposingNames);
      if (declared == 0) {
        continue;
      }

      exposure |= _narrowedBySwitches(attribute, declared);
    }

    return exposure;
  }

  /// <summary>The exposure an attribute declares, before its own switches narrow it.</summary>
  private static int _declaredExposure(
      AttributeData attribute, ImmutableArray<string> extraComposingNames) {
    var name = attribute.AttributeClass is null
      ? null
      : TypeNameUtilities.Display(attribute.AttributeClass);

    if (name is not null
        && (_knownComposingAttributes.Contains(name)
            || (!extraComposingNames.IsDefaultOrEmpty && extraComposingNames.Contains(name)))) {
      // A sorting or filtering middleware offers both in practice: the same surface that orders also
      // narrows, and naming them apart would make the list twice as long for no gain.
      return EXPOSURE_ORDERING | EXPOSURE_FILTERING;
    }

    var marker = attribute.AttributeClass?.GetAttributes()
        .FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, MARKER));

    if (marker is null) {
      return 0;
    }

    return marker.ConstructorArguments.Length > 0
           && marker.ConstructorArguments[0].Value is int declared
      ? declared
      : EXPOSURE_ORDERING | EXPOSURE_FILTERING;
  }

  /// <summary>Removes what the attribute's own switches turn off.</summary>
  private static int _narrowedBySwitches(AttributeData attribute, int declared) {
    foreach (var named in attribute.NamedArguments) {
      if (named.Value.Value is not false) {
        continue;
      }

      if (string.Equals(named.Key, "EnableSorting", StringComparison.Ordinal)) {
        declared &= ~EXPOSURE_ORDERING;
      } else if (string.Equals(named.Key, "EnableFiltering", StringComparison.Ordinal)) {
        declared &= ~EXPOSURE_FILTERING;
      }
    }

    return declared;
  }

  /// <summary>The models a lens declaration exposes.</summary>
  /// <param name="lens">The interface or class carrying the surface's attribute.</param>
  /// <returns>Each model the lens queries, empty when the type is not a lens.</returns>
  /// <remarks>
  /// Read from the implemented interface rather than the declaration itself, because a lens states
  /// its model by implementing <c>ILensQuery</c> of it and the attribute sits on the declaring type.
  /// Every type argument counts: a multi-model lens exposes all of them through the one surface, and
  /// taking only the first would quietly under-report the wider ones.
  /// </remarks>
  public static ImmutableArray<INamedTypeSymbol> ModelsOfLens(INamedTypeSymbol? lens) {
    var query = lens?.AllInterfaces.FirstOrDefault(
        i => TypeNameUtilities.Display(i.OriginalDefinition).StartsWith(LENS_QUERY, StringComparison.Ordinal));

    if (query is null || query.TypeArguments.Length == 0) {
      return [];
    }

    return [.. query.TypeArguments.Select(_unwrapRow).Where(m => m is not null).Cast<INamedTypeSymbol>()];
  }

  /// <summary>The model behind a member that hands back a queryable.</summary>
  /// <param name="type">The member's declared type.</param>
  /// <returns>The model, or null when this hands back something else.</returns>
  /// <remarks>
  /// A resolver returns the query rather than its results, which is what lets a middleware add the
  /// ordering after the fact. The model can therefore sit under an awaitable, under the queryable,
  /// and under the row wrapper, so all three are peeled before asking what is left.
  /// </remarks>
  public static INamedTypeSymbol? ModelOfQueryable(ITypeSymbol? type) {
    var unwrapped = _unwrapAwaitable(type);

    if (unwrapped is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named) {
      return null;
    }

    return _queryableDefinitions.Contains(_definitionName(named))
      ? _unwrapRow(named.TypeArguments[0])
      : null;
  }

  /// <summary>A generic type's name without its type parameters.</summary>
  /// <remarks>
  /// The display form of an open generic carries its parameter list (<c>IQueryable&lt;T&gt;</c>), so
  /// comparing it against a bare name silently never matches. Cutting at the bracket is what makes
  /// the comparison say what it looks like it says.
  /// </remarks>
  private static string _definitionName(INamedTypeSymbol type) {
    var display = TypeNameUtilities.Display(type.OriginalDefinition);
    var bracket = display.IndexOf('<');

    return bracket < 0 ? display : display[..bracket];
  }

  /// <summary>
  /// The declared types that hand back a query rather than its results.
  /// </summary>
  /// <remarks>
  /// Named rather than matched by interface, because a method's declared return type is what the
  /// middleware attaches to. A method returning a materialized list has already run its query, so
  /// nothing can add an ordering to it and there is no exposure to report.
  /// </remarks>
  private static readonly string[] _queryableDefinitions = [
    "System.Linq.IQueryable",
    "System.Linq.IOrderedQueryable",
    "HotChocolate.IExecutable",
    "HotChocolate.Data.IQueryableExecutable",
  ];

  /// <summary>Peels an awaitable, since a resolver may be asynchronous.</summary>
  private static ITypeSymbol? _unwrapAwaitable(ITypeSymbol? type) {
    if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named) {
      return type;
    }

    return _definitionName(named) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask"
      ? named.TypeArguments[0]
      : type;
  }

  /// <summary>The model inside a perspective row, or the type itself when it is the model.</summary>
  /// <remarks>
  /// The framework's own query surfaces hand back the row, which carries the model alongside the
  /// bookkeeping columns. The fields a request can order by are the model's either way.
  /// </remarks>
  private static INamedTypeSymbol? _unwrapRow(ITypeSymbol? type) {
    if (type is not INamedTypeSymbol named) {
      return null;
    }

    return named is { IsGenericType: true, TypeArguments.Length: 1 }
           && TypeNameUtilities.Display(named.ConstructedFrom)
               .StartsWith(PERSPECTIVE_ROW, StringComparison.Ordinal)
      ? named.TypeArguments[0] as INamedTypeSymbol
      : named;
  }

  /// <summary>QueryExposure.Filtering, as the enumeration spells it.</summary>
  private const int EXPOSURE_FILTERING = 1;

  /// <summary>QueryExposure.Ordering.</summary>
  private const int EXPOSURE_ORDERING = 2;

  /// <summary>QueryExposure.Expression.</summary>
  private const int EXPOSURE_EXPRESSION = 4;

  /// <summary>Whether an exposure lets a request choose the order, which is the expensive case.</summary>
  /// <param name="exposure">The combined exposure.</param>
  /// <returns><c>true</c> when ordering or an arbitrary expression is on offer.</returns>
  public static bool AllowsOrdering(int exposure) =>
    (exposure & (EXPOSURE_ORDERING | EXPOSURE_EXPRESSION)) != 0;

  /// <summary>
  /// The model's fields that a request could order by and that nothing has accounted for.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The field names, empty when the model is accounted for or has nothing to report.</returns>
  /// <remarks>
  /// Empty for several different reasons, all of them meaning "do not report": the model asks for
  /// every field to be indexed, the model records a decision wholesale, or every field is already
  /// indexed, promoted, the stream key, or of a type no index could be built over anyway.
  /// </remarks>
  public static ImmutableArray<string> UnattributedFields(INamedTypeSymbol? modelType) {
    if (modelType is null || _accountedForWholesale(modelType)) {
      return [];
    }

    // A document stored as one opaque value has no per-field extraction to index, so naming its
    // fields would be advice that cannot be taken. WHIZ304 already covers a declared index there.
    if (MappedPathDiscovery.MustStoreOpaquely(modelType)) {
      return [];
    }

    var found = new List<string>();

    foreach (var property in modelType.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic)) {
      if (_accountedFor(property)) {
        continue;
      }

      // A field whose stored form cannot carry an index is not actionable: indexing it is not on
      // offer, so reporting it would be noise rather than a choice.
      if (JsonIndexDiscovery.CastFor(property.Type) is null) {
        continue;
      }

      found.Add(property.Name);
    }

    return [.. found];
  }

  /// <summary>Whether the model answers the question for all of its fields at once.</summary>
  private static bool _accountedForWholesale(INamedTypeSymbol modelType) =>
    modelType.GetAttributes().Any(a =>
      TypeNameUtilities.IsNamed(a.AttributeClass, INDEX_ALL_FIELDS)
      || TypeNameUtilities.IsNamed(a.AttributeClass, SUPPRESS));

  /// <summary>Whether this field already has an answer, of any kind.</summary>
  /// <remarks>
  /// The stream key is the row's primary key in either storage form, so it is answered by being
  /// what it is. A promoted or vector field carries its own column and its own index question.
  /// </remarks>
  private static bool _accountedFor(IPropertySymbol property) {
    if (JsonIndexDiscovery.DeclaredKind(property) is not null) {
      return true;
    }

    return property.GetAttributes().Any(a =>
      TypeNameUtilities.IsNamed(a.AttributeClass, SUPPRESS)
      || TypeNameUtilities.IsNamed(a.AttributeClass, STREAM_ID)
      || TypeNameUtilities.IsNamed(a.AttributeClass, PHYSICAL_FIELD)
      || TypeNameUtilities.IsNamed(a.AttributeClass, VECTOR_FIELD));
  }

  /// <summary>
  /// WHIZ306: Warning - a model reachable by request-time sorting has fields with no index.
  /// </summary>
  /// <remarks>
  /// Shared so both integrations report the same thing. Two analyzers inventing two descriptions of
  /// one condition is how a consumer comes to believe they are unrelated problems.
  /// </remarks>
  public static readonly DiagnosticDescriptor SortableFieldsHaveNoIndex = new(
      id: "WHIZ306",
      title: "Fields reachable by request-time sorting have no index",
      messageFormat: "'{0}' is exposed with sorting or filtering composed from the request, so any of "
          + "its fields can reach an ORDER BY or WHERE that this build never sees. {1} of its fields "
          + "carry no index and no recorded decision ({2}), and a sort on any one of those reads every "
          + "row of the perspective. Mark the fields the surface actually offers with [Indexed], or "
          + "[IndexAllFields] on the model if it is queried every way, or record the decision with "
          + "[SuppressIndexAdvisory(\"reason\")].",
      category: "Whizbang.PerspectiveValidation",
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The index advisory finds a filter by reading the source that wrote it. Sorting and "
          + "filtering composed from a request are not written in source at all: the field name arrives as "
          + "a string and the ORDER BY is built when the request is served. The advisory is therefore "
          + "silent on the models that are queried the most, and that silence is not evidence of anything. "
          + "What can be checked is the exposure, which is a statement that any field of the model may be "
          + "ordered by. Reported once per exposure rather than once per field, because the number of "
          + "fields a client can really sort by is small and the analyzer cannot tell which they are: the "
          + "author can. An unindexed sort on a large perspective is the most expensive query shape there "
          + "is, and the one least likely to be noticed, because it returns correct rows."
  );
}
