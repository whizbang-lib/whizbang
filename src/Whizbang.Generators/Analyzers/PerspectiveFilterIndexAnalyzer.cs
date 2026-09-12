using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer that reports a lens query filtering a perspective on a field that has no
/// physical column, which the database can only answer by reading every row of the table.
/// </summary>
/// <remarks>
/// <para>
/// A perspective stores its read model as a JSON document, so only properties promoted to a real
/// column by <c>[PhysicalField]</c> can carry an index. A predicate over any other property is
/// evaluated by extracting it from the JSON of every candidate row, which is correct and linear.
/// That cost is invisible in development, where the table holds tens of rows, and dominates in
/// production, where it holds millions.
/// </para>
/// <para>
/// The analyzer keys on <c>PerspectiveRow&lt;TModel&gt;.Data</c> rather than on any particular
/// query API, so both the scoped lens surface and the older direct one are covered, in method and
/// in query syntax.
/// </para>
/// <para>
/// Opting out is <c>[SuppressIndexAdvisory("reason")]</c> on the property, the model, or the
/// assembly. The reason is required and a blank one does not suppress, so the opt-out reads as a
/// decision in review; the same attribute also stands down the runtime index advisory, which a
/// <c>#pragma</c> would not.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz302</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/PerspectiveFilterIndexAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveFilterIndexAnalyzer : DiagnosticAnalyzer {
  // Diagnostic IDs: WHIZ300-399 reserved for perspective validation
  private const string CATEGORY = "Whizbang.PerspectiveValidation";

  private const string PERSPECTIVE_ROW_PREFIX = "Whizbang.Core.Lenses.PerspectiveRow<";
  private const string DATA_PROPERTY = "Data";
  private const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";
  private const string STREAM_ID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";
  private const string SUPPRESS_ATTRIBUTE = "Whizbang.Core.Perspectives.SuppressIndexAdvisoryAttribute";
  private const string ASYNC_SUFFIX = "Async";

  /// <summary>
  /// The operators whose lambda decides which rows the database has to look at. Projection and
  /// paging operators are deliberately absent: they read a field out of rows already chosen, so a
  /// missing index on them costs nothing.
  /// </summary>
  private static readonly HashSet<string> _rowSelectingOperators = new(StringComparer.Ordinal) {
    "Where", "Any", "All", "Count", "LongCount", "SkipWhile", "TakeWhile",
    "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
    "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
    "Min", "Max", "MinBy", "MaxBy",
  };

  /// <summary>
  /// WHIZ302: Warning - a lens query filters a perspective field that has no physical column.
  /// </summary>
  public static readonly DiagnosticDescriptor FilteredFieldHasNoIndex = new(
      id: "WHIZ302",
      title: "Filtered perspective field has no index",
      messageFormat: "This query filters '{0}.{1}', which is stored only in the model's JSON, so the database reads every row of the perspective. {2}.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A perspective stores its model as JSON, and the GIN index on that document answers containment and " +
                   "nothing else, so a range, an ordering or a pattern match on a JSON-only property reads every row. " +
                   "There are two fixes and they cost differently. [JsonIndexed] builds an index over the stored value: no " +
                   "column, no schema change, no write-path work, and it answers equality, ranges, ordering and null tests. " +
                   "[PhysicalField(Indexed = true)] promotes the property to a real column, which additionally allows " +
                   "constraints and foreign keys and is the only option for a type whose stored form cannot carry an index, " +
                   "a date being the case that matters. When a scan is the right answer, say so with " +
                   "[SuppressIndexAdvisory(\"reason\")]: the reason is required, a blank one does not suppress, and the same " +
                   "attribute also stands down the runtime index advisory raised by the maintenance cycle. A model holding " +
                   "a polymorphic member is stored as one serialized value rather than as mapped properties, so an index " +
                   "over a field inside it cannot be reached at all; there the message offers only the column, because " +
                   "taking the other advice would land on WHIZ304."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [FilteredFieldHasNoIndex];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();

    context.RegisterSyntaxNodeAction(
      _analyzeMemberAccess,
      SyntaxKind.SimpleMemberAccessExpression);
  }

  private static void _analyzeMemberAccess(SyntaxNodeAnalysisContext context) {
    var node = (MemberAccessExpressionSyntax)context.Node;

    // Only `<row>.Data.<Field>` is interesting: the row's own columns are not model JSON.
    var model = _modelBehindDataAccess(context, node.Expression);
    if (model is null) {
      return;
    }

    if (context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol is not IPropertySymbol field) {
      return;
    }

    if (_isIndexBacked(field) || !_decidesWhichRowsAreRead(node) || _containmentCanServe(node, field)) {
      return;
    }

    if (_isSuppressed(field, model, context.Compilation.Assembly)) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
      FilteredFieldHasNoIndex,
      node.Name.GetLocation(),
      TypeNameUtilities.MinimallyQualified(model),
      field.Name,
      _adviceFor(model)));
  }

  /// <summary>
  /// The fixes that actually work for this model, which depends on how its document is stored.
  /// </summary>
  /// <param name="model">The perspective's model type.</param>
  /// <returns>The sentence naming the available fixes.</returns>
  /// <remarks>
  /// A model holding a polymorphic member is stored as one serialized value rather than as mapped
  /// properties, so an index over an extraction from it is unreachable and the generator skips it.
  /// Offering <c>[JsonIndexed]</c> there would send an author who takes the advice straight into
  /// WHIZ304 for having taken it. The two diagnostics have to agree about what is possible, so the
  /// advice follows the storage rather than being fixed text.
  /// </remarks>
  private static string _adviceFor(INamedTypeSymbol model) =>
    PolymorphicModelDiscovery.IsPolymorphic(model)
      ? "This model holds a polymorphic member, so its document is stored as one serialized value and "
        + "an index over a field inside it cannot be reached. Promote it with "
        + "[PhysicalField(Indexed = true)] to get a real indexed column, or record the decision with "
        + "[SuppressIndexAdvisory(\"reason\")]"
      : "Mark it [JsonIndexed] for an index over the stored value, [PhysicalField(Indexed = true)] to "
        + "promote it to a column, or record the decision with [SuppressIndexAdvisory(\"reason\")]";

  /// <summary>
  /// Resolves <paramref name="expression"/> as <c>PerspectiveRow&lt;TModel&gt;.Data</c> and returns
  /// TModel, or null when the expression is anything else.
  /// </summary>
  private static INamedTypeSymbol? _modelBehindDataAccess(SyntaxNodeAnalysisContext context, ExpressionSyntax expression) {
    if (expression is not MemberAccessExpressionSyntax) {
      return null;
    }

    if (context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol is not IPropertySymbol data) {
      return null;
    }

    if (!string.Equals(data.Name, DATA_PROPERTY, StringComparison.Ordinal)) {
      return null;
    }

    var row = data.ContainingType;
    if (row is null || !TypeNameUtilities.Display(row).StartsWith(PERSPECTIVE_ROW_PREFIX, StringComparison.Ordinal)) {
      return null;
    }

    return row.TypeArguments.Length == 1 ? row.TypeArguments[0] as INamedTypeSymbol : null;
  }

  /// <summary>
  /// Whether this field reference sits where it narrows or orders the rows the database reads,
  /// rather than in a projection over rows already chosen.
  /// </summary>
  private static bool _decidesWhichRowsAreRead(SyntaxNode node) {
    for (var current = node.Parent; current is not null; current = current.Parent) {
      switch (current) {
        // Query syntax carries no lambda node of its own.
        case WhereClauseSyntax:
        case OrderingSyntax:
          return true;
        case LambdaExpressionSyntax lambda:
          return _feedsRowSelectingOperator(lambda);
        case AnonymousFunctionExpressionSyntax:
        case MemberDeclarationSyntax:
          return false;
      }
    }

    return false;
  }

  private static bool _feedsRowSelectingOperator(LambdaExpressionSyntax lambda) {
    if (lambda.Parent is not ArgumentSyntax argument ||
        argument.Parent?.Parent is not InvocationExpressionSyntax invocation ||
        invocation.Expression is not MemberAccessExpressionSyntax invoked) {
      return false;
    }

    var name = invoked.Name.Identifier.ValueText;

    // Entity Framework's asynchronous operators take the same predicates as their synchronous twins.
    if (name.EndsWith(ASYNC_SUFFIX, StringComparison.Ordinal)) {
      name = name.Substring(0, name.Length - ASYNC_SUFFIX.Length);
    }

    return _rowSelectingOperators.Contains(name);
  }

  /// <summary>
  /// Whether the lens can already answer this filter from the GIN index on the data column, which
  /// makes the advisory wrong rather than merely noisy.
  /// </summary>
  /// <remarks>
  /// <para>
  /// An equality filter on a JSON-only scalar is compiled into a jsonb containment test, so it is a
  /// lookup already and needs no physical column. The advisory is therefore about the shapes
  /// containment cannot express: ranges and inequalities, ordering, pattern matching, a comparison
  /// against null, anything under a negation, and the types whose serialized text and PostgreSQL's
  /// are not guaranteed to agree.
  /// </para>
  /// <para>
  /// The eligible type set is duplicated from <c>JsonbContainment</c> in the Postgres driver, and it
  /// has to be: an analyzer is referenced as an analyzer rather than as a library, so neither side
  /// can see the other's list and no single test can compare them. Each side pins its own list
  /// instead, here by <c>EqualityContainmentCanServe_IsNotReportedAsync</c> and there by
  /// <c>JsonbContainmentTypeSetTests</c>, and each names the other. Drift shows up as an advisory
  /// that fires on a filter already indexed, or one that stays silent on a filter that scans; both
  /// are quality faults rather than wrong answers.
  /// </para>
  /// </remarks>
  private static bool _containmentCanServe(MemberAccessExpressionSyntax node, IPropertySymbol field) {
    if (!_isContainmentEligibleType(field.Type) || _isUnderNegation(node)) {
      return false;
    }

    for (SyntaxNode? current = node; current is not null; current = current.Parent) {
      switch (current.Parent) {
        case ParenthesizedExpressionSyntax:
          continue;

        case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression):
          // Comparing against null is the one equality containment cannot reproduce.
          var other = binary.Left == current ? binary.Right : binary.Left;
          return !other.IsKind(SyntaxKind.NullLiteralExpression);

        case BinaryExpressionSyntax:
          // Any other binary operator: a range, an inequality, or a logical join of them.
          return false;

        case ArgumentSyntax argument when _isOrdinalEqualsCall(argument):
          return true;

        case MemberAccessExpressionSyntax member when member.Expression == current:
          return _isOrdinalEqualsCall(member);

        case LambdaExpressionSyntax:
        case ArgumentSyntax:
          return false;

        default:
          continue;
      }
    }

    return false;
  }

  /// <summary>
  /// Whether a negation encloses this reference anywhere up to the predicate that holds it.
  /// </summary>
  /// <remarks>
  /// Checked before the comparison rather than during the walk: the comparison is always the nearer
  /// ancestor, so looking for the negation on the way past finds the equality first and never sees
  /// the negation at all.
  /// </remarks>
  private static bool _isUnderNegation(SyntaxNode node) {
    for (var current = node.Parent; current is not null; current = current.Parent) {
      switch (current) {
        case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression):
          return true;
        case LambdaExpressionSyntax:
        case WhereClauseSyntax:
        case MemberDeclarationSyntax:
          return false;
        default:
          continue;
      }
    }

    return false;
  }

  /// <summary>
  /// Whether the enclosing call is <c>Equals</c> performing the ordinal comparison containment does.
  /// A case-insensitive or culture-aware comparison is a different question and keeps the advisory.
  /// </summary>
  private static bool _isOrdinalEqualsCall(SyntaxNode node) {
    var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
    if (invocation?.Expression is not MemberAccessExpressionSyntax invoked
        || !string.Equals(invoked.Name.Identifier.ValueText, "Equals", StringComparison.Ordinal)) {
      return false;
    }

    foreach (var argument in invocation.ArgumentList.Arguments) {
      var text = argument.Expression.ToString();
      if (text.Contains("StringComparison", StringComparison.Ordinal)) {
        return text.EndsWith(".Ordinal", StringComparison.Ordinal);
      }
    }

    return true;
  }

  /// <summary>
  /// The CLR types a perspective filter can be compiled into a containment test for, which is what
  /// makes the advisory unnecessary: the filter is already a lookup rather than a scan.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This list is the same one <c>JsonbContainment.OverloadFor</c> holds, and the duplication is
  /// forced: an analyzer is referenced as an analyzer rather than as a library, so this assembly
  /// cannot see that one. Each side pins its own list in a test and names the other.
  /// <c>JsonbContainmentTypeSetTests</c> is the counterpart.
  /// </para>
  /// <para>
  /// Disagreeing with that list costs an advisory rather than an answer. Naming a type here that the
  /// rewrite does not handle leaves a scanning filter with no warning; omitting one it does handle
  /// warns about a filter that is already indexed.
  /// </para>
  /// </remarks>
  private static bool _isContainmentEligibleType(ITypeSymbol type) {
    var bare = type is INamedTypeSymbol { IsGenericType: true, ConstructedFrom.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : type;

    // An enumeration is stored as its underlying number and compared through that overload, so it is
    // eligible exactly when the number is. An underlying type without an overload, such as an
    // unsigned one, therefore falls through to false rather than being assumed eligible.
    if (bare.TypeKind == TypeKind.Enum && bare is INamedTypeSymbol { EnumUnderlyingType: { } underlying }) {
      bare = underlying;
    }

    // The date family is deliberately absent. It is stored as a number, which is what made it
    // indexable, and a converted property compiles to an extraction against that number rather than
    // to a containment test: correct, and without a declared index, a scan. Treating it as served
    // would be the silent sequential scan this advisory exists to prevent, on a filter that used to
    // be a lookup. A declared index is also the better outcome, because a single-column btree probe
    // beats containment, which reads the document index and then rechecks every candidate row.
    return bare.SpecialType switch {
      SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Int16
        or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal
        or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Byte => true,
      _ => string.Equals(TypeNameUtilities.Display(bare), "System.Guid", StringComparison.Ordinal),
    };
  }

  /// <summary>
  /// Whether the generators would give this property an index: the stream id becomes the row key,
  /// a physical field asks for one outright or gets one from its unique constraint, and a vector
  /// field is indexed unless its declaration turns the index off.
  /// </summary>
  private static bool _isIndexBacked(IPropertySymbol property) {
    foreach (var attribute in property.GetAttributes()) {
      var name = attribute.AttributeClass is null ? null : TypeNameUtilities.Display(attribute.AttributeClass);

      switch (name) {
        case STREAM_ID_ATTRIBUTE:
          return true;
        case PHYSICAL_FIELD_ATTRIBUTE:
          if (_namedFlag(attribute, "Indexed") == true || _namedFlag(attribute, "Unique") == true) {
            return true;
          }

          break;
        case VECTOR_FIELD_ATTRIBUTE:
          if (_namedFlag(attribute, "Indexed") != false) {
            return true;
          }

          break;
        default:
          break;
      }
    }

    return false;
  }

  private static bool? _namedFlag(AttributeData attribute, string key) {
    foreach (var named in attribute.NamedArguments) {
      if (string.Equals(named.Key, key, StringComparison.Ordinal)) {
        return named.Value.Value as bool?;
      }
    }

    return null;
  }

  /// <summary>
  /// Whether the field, its model or any of the model's bases, the type that declares the field,
  /// or an assembly in play carries a reasoned opt-out. A blank reason is not a decision, so it
  /// does not count.
  /// </summary>
  private static bool _isSuppressed(IPropertySymbol field, INamedTypeSymbol model, IAssemblySymbol compiling) {
    if (_hasReasonedSuppression(field.GetAttributes())) {
      return true;
    }

    for (var type = model; type is not null; type = type.BaseType) {
      if (_hasReasonedSuppression(type.GetAttributes())) {
        return true;
      }
    }

    // The property may be declared on a type the model only composes.
    var declaring = field.ContainingType;
    if (declaring is not null &&
        !SymbolEqualityComparer.Default.Equals(declaring, model) &&
        _hasReasonedSuppression(declaring.GetAttributes())) {
      return true;
    }

    // The assembly-wide opt-out is honored from either side: the assembly being compiled, which is
    // where a team writes it, and the model's own assembly, for a model that arrives as a package.
    if (_hasReasonedSuppression(compiling.GetAttributes())) {
      return true;
    }

    var owning = model.ContainingAssembly;
    return owning is not null &&
           !SymbolEqualityComparer.Default.Equals(owning, compiling) &&
           _hasReasonedSuppression(owning.GetAttributes());
  }

  private static bool _hasReasonedSuppression(ImmutableArray<AttributeData> attributes) {
    foreach (var attribute in attributes) {
      if (attribute.AttributeClass is null ||
          !string.Equals(TypeNameUtilities.Display(attribute.AttributeClass), SUPPRESS_ATTRIBUTE, StringComparison.Ordinal)) {
        continue;
      }

      if (attribute.ConstructorArguments.Length > 0 &&
          attribute.ConstructorArguments[0].Value is string reason &&
          !string.IsNullOrWhiteSpace(reason)) {
        return true;
      }
    }

    return false;
  }
}
