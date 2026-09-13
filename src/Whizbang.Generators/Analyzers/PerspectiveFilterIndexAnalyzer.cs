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

  /// <summary>Which expression a comparison is over, of the ones an index can be built on.</summary>
  private enum Fold {
    /// <summary>The stored value, compared as it is stored.</summary>
    Respects,

    /// <summary>The value folded down, which is the fold a declaration can ask for.</summary>
    Lower,

    /// <summary>The value folded up, which no declaration builds an index over.</summary>
    Upper,
  }

  private const string ASYNC_SUFFIX = "Async";

  /// <summary>The string operations a trigram index answers.</summary>
  private static readonly HashSet<string> _substringOperators = new(StringComparer.Ordinal) {
    "Contains", "StartsWith", "EndsWith",
  };

  /// <summary>The operations that fold case in the database, and which fold they compile to.</summary>
  /// <remarks>
  /// <para>
  /// Only the parameterless forms, and that is measured rather than chosen: Entity Framework maps
  /// <c>ToLower()</c> and <c>ToUpper()</c> to <c>lower()</c> and <c>upper()</c>, and has no mapping
  /// for the invariant forms or the ones taking a culture. A query written with those does not scan,
  /// it fails to translate, so there is no index question to answer about it and listing them here
  /// would attach index advice to a query that never reaches the database.
  /// </para>
  /// <para>
  /// Nothing else is listed for a different reason: another function over the field is another
  /// expression again, and no declaration can ask for an index over it.
  /// </para>
  /// </remarks>
  private static readonly Dictionary<string, Fold> _foldingOperators = new(StringComparer.Ordinal) {
    ["ToLower"] = Fold.Lower,
    ["ToUpper"] = Fold.Upper,
  };

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
                   "There are two fixes and they cost differently. [Indexed] builds an index over the stored value: no " +
                   "column, no schema change, no write-path work, and it answers equality, ranges, ordering and null tests. " +
                   "[PhysicalField] plus [Indexed] promotes the property to a real column, which additionally allows " +
                   "constraints and foreign keys and is the only option for a type whose stored form cannot carry an index, " +
                   "a date being the case that matters. When a scan is the right answer, say so with " +
                   "[SuppressIndexAdvisory(\"reason\")]: the reason is required, a blank one does not suppress, and the same " +
                   "attribute also stands down the runtime index advisory raised by the maintenance cycle. A declaration " +
                   "counts for the comparison it matches rather than for every query on the field: the capability has to " +
                   "answer the shape, and the index has to be built over the expression the comparison produces, so a " +
                   "comparison folding case with ToLower() is answered only by [Indexed(caseInsensitive: true)]. A model holding " +
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

    // A declared index is an index. Without this the advisory would tell an author to mark the field
    // [Indexed] and then keep reporting after they did, which is advice with no exit.
    if (_declaredIndexServes(node, field)) {
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
      _adviceFor(model, _foldOf(node))));
  }

  /// <summary>
  /// Whether a [Indexed] declaration on this field covers the shape being written.
  /// </summary>
  /// <param name="node">The member access being analyzed.</param>
  /// <param name="field">The property it resolves to.</param>
  /// <returns><c>true</c> when a declared index answers this filter.</returns>
  /// <remarks>
  /// <para>
  /// Two questions, and both have to be yes. The kind decides which shapes are answered: the ordered
  /// capability covers ranges, orderings, equality and null tests, and substring matching covers
  /// pattern matching and nothing else, which is the same distinction the runtime makes when it
  /// decides whether to stand the containment rewrite down. The fold decides which expression the
  /// index is over, and a comparison can only be answered by an index over the expression it
  /// produces.
  /// </para>
  /// <para>
  /// The kind is matched to the shape in both directions. An ordered declaration used to cover every
  /// shape, including a pattern match, which an ordered index does not answer with a leading wildcard
  /// and does not answer for a prefix either under any ordinary collation.
  /// </para>
  /// <para>
  /// Asking only the first question is what made a declared index look like it served every query on
  /// the field. It is the quietest version of this failure: the author declared the index, can see it
  /// in the database, and every folded comparison still reads every row with nothing reported.
  /// </para>
  /// <para>
  /// Asked only of a field indexed through its document. A promoted column is settled earlier, and
  /// deliberately: the attribute cannot ask for a functional index on a column, so a report there
  /// would name a problem with no fix to offer.
  /// </para>
  /// <para>
  /// Kept narrow on purpose. Treating any declaration as covering any shape would silence the
  /// advisory on a filter that really does read every row, and a silent scan is the failure this
  /// exists to prevent; over-reporting merely annoys.
  /// </para>
  /// </remarks>
  private static bool _declaredIndexServes(MemberAccessExpressionSyntax node, IPropertySymbol field) {
    var fold = _foldOf(node);

    // Only the lower fold is declarable, so a comparison over the upper one is answered by no index
    // the author can ask for, whatever the field declares.
    if (fold == Fold.Upper) {
      return false;
    }

    var kind = JsonIndexDiscovery.DeclaredKind(field, caseInsensitive: fold == Fold.Lower);
    if (kind is null) {
      return false;
    }

    // Each shape has one capability that answers it, and asking the other way round is what let a
    // declaration cover shapes it cannot serve.
    return _isSubstringMatch(node)
      ? JsonIndexDiscovery.IncludesSubstring(kind.Value)
      : JsonIndexDiscovery.IncludesOrdered(kind.Value);
  }

  /// <summary>Which expression the comparison this member access feeds is over.</summary>
  private static Fold _foldOf(MemberAccessExpressionSyntax node) =>
    node.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax } call
    && _foldingOperators.TryGetValue(call.Name.Identifier.ValueText, out var fold)
      ? fold
      : Fold.Respects;

  /// <summary>Whether this expression is the receiver of a substring match.</summary>
  /// <remarks>
  /// Looks through a fold, because a case-insensitive search applies the operator to the folded
  /// value: the shape is <c>field.ToLower().Contains(…)</c> and the operator is one call further out
  /// than it would otherwise be. Whether the fold itself is served is a separate question, answered
  /// by the declaration's own folding; missing it here would report the most common search shape
  /// there is to an author who had declared exactly the right index for it.
  /// </remarks>
  private static bool _isSubstringMatch(SyntaxNode node) {
    if (node.Parent is not MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax invocation } call) {
      return false;
    }

    var name = call.Name.Identifier.ValueText;

    return _foldingOperators.ContainsKey(name)
      ? _isSubstringMatch(invocation)
      : _substringOperators.Contains(name);
  }

  /// <summary>
  /// The fixes that actually work for this model, which depends on how its document is stored.
  /// </summary>
  /// <param name="model">The perspective's model type.</param>
  /// <param name="fold">The expression the comparison is over.</param>
  /// <returns>The sentence naming the available fixes.</returns>
  /// <remarks>
  /// <para>
  /// A model holding a polymorphic member is stored as one serialized value rather than as mapped
  /// properties, so an index over an extraction from it is unreachable and the generator skips it.
  /// Offering <c>[Indexed]</c> there would send an author who takes the advice straight into
  /// WHIZ304 for having taken it. The two diagnostics have to agree about what is possible, so the
  /// advice follows the storage rather than being fixed text.
  /// </para>
  /// <para>
  /// It follows the comparison for the same reason. A field reported for a folded comparison is
  /// commonly already marked <c>[Indexed]</c>, and repeating that advice reads as the diagnostic
  /// being wrong rather than as one word being missing.
  /// </para>
  /// </remarks>
  private static string _adviceFor(INamedTypeSymbol model, Fold fold) {
    if (PolymorphicModelDiscovery.IsPolymorphic(model)) {
      // The fold is beside the point here: no index over this document is reachable at all.
      return "This model holds a polymorphic member, so its document is stored as one serialized value and "
        + "an index over a field inside it cannot be reached. Promote it with "
        + "[PhysicalField] plus [Indexed] to get a real indexed column, or record the decision with "
        + "[SuppressIndexAdvisory(\"reason\")]";
    }

    const string PROMOTE_OR_SUPPRESS = "[PhysicalField] plus [Indexed] to promote it to a column, or "
      + "record the decision with [SuppressIndexAdvisory(\"reason\")]";

    return fold switch {
      Fold.Lower => "This comparison folds case, which is a different expression from the stored value "
        + "and so a different index. Mark it [Indexed(caseInsensitive: true)] for an index over the "
        + "folded value, " + PROMOTE_OR_SUPPRESS,
      Fold.Upper => "This comparison folds case upward, and the index a declaration builds is over the "
        + "downward fold. Compare with ToLower() and mark it [Indexed(caseInsensitive: true)], use "
        + PROMOTE_OR_SUPPRESS,
      _ => "Mark it [Indexed] for an index over the stored value, " + PROMOTE_OR_SUPPRESS,
    };
  }

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
  /// <remarks>
  /// The nearest ancestor that has an opinion decides, and the walk stops there. Written as the first
  /// non-null verdict rather than as a loop with a terminal <c>return</c>: every ancestor chain
  /// reaches a member declaration, a top-level statement being one too, so a fall-through after the
  /// walk would be a line nothing can execute.
  /// </remarks>
  private static bool _decidesWhichRowsAreRead(SyntaxNode node) =>
    node.Ancestors().Select(_rowSelectionVerdict).FirstOrDefault(v => v.HasValue) ?? false;

  /// <summary>Whether one ancestor settles the question, and how.</summary>
  private static bool? _rowSelectionVerdict(SyntaxNode current) => current switch {
    // Query syntax carries no lambda node of its own.
    WhereClauseSyntax or OrderingSyntax => true,
    LambdaExpressionSyntax lambda => _feedsRowSelectingOperator(lambda),
    AnonymousFunctionExpressionSyntax or MemberDeclarationSyntax => false,
    _ => null,
  };

  private static bool _feedsRowSelectingOperator(LambdaExpressionSyntax lambda) {
    if (lambda.Parent is not ArgumentSyntax argument ||
        argument.Parent?.Parent is not InvocationExpressionSyntax invocation ||
        invocation.Expression is not MemberAccessExpressionSyntax invoked) {
      return false;
    }

    var name = invoked.Name.Identifier.ValueText;

    // Entity Framework's asynchronous operators take the same predicates as their synchronous twins.
    if (name.EndsWith(ASYNC_SUFFIX, StringComparison.Ordinal)) {
      name = name[..^ASYNC_SUFFIX.Length];
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
  private static bool _isUnderNegation(SyntaxNode node) =>
    node.Ancestors().Select(_negationVerdict).FirstOrDefault(v => v.HasValue) ?? false;

  /// <summary>
  /// Whether one ancestor settles whether a negation encloses the reference.
  /// </summary>
  /// <remarks>
  /// The predicate's own boundary answers no, which is what stops the search at the filter rather
  /// than letting it find a negation somewhere else in the method. As with the row-selection walk,
  /// every chain reaches one of these, so there is no fall-through to express.
  /// </remarks>
  private static bool? _negationVerdict(SyntaxNode current) => current switch {
    PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression) => true,
    LambdaExpressionSyntax or WhereClauseSyntax or MemberDeclarationSyntax => false,
    _ => null,
  };

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
          // The promotion's own flags, and the universal attribute written alongside it. Either way
          // the index is on a real column, where the capabilities do not apply: they describe
          // indexes over an extraction from the document, and nothing can ask for a pattern-matching
          // index on a column. Settled here so the shape matching below is only ever asked about a
          // field held in the document, and so a promoted field is never reported with advice it
          // has already taken and a message about JSON it no longer lives in.
          if (_namedFlag(attribute, "Indexed") == true
              || _namedFlag(attribute, "Unique") == true
              || JsonIndexDiscovery.DeclaredKind(property) is > 0) {
            return true;
          }

          break;
        case VECTOR_FIELD_ATTRIBUTE:
          // A vector column exists whether or not it is indexed, and [Indexed] is what asks. The
          // declaration is checked by _declaredIndexServes rather than assumed here, so a vector
          // nobody asked to index reports like any other unindexed field.
          break;
        default:
          break;
      }
    }

    return false;
  }

  private static bool? _namedFlag(AttributeData attribute, string key) =>
    attribute.NamedArguments
        .Where(n => string.Equals(n.Key, key, StringComparison.Ordinal))
        .Select(n => n.Value.Value as bool?)
        .FirstOrDefault();

  /// <summary>
  /// Whether the field, its model or any of the model's bases, the type that declares the field,
  /// or an assembly in play carries a reasoned opt-out. A blank reason is not a decision, so it
  /// does not count.
  /// </summary>
  private static bool _isSuppressed(IPropertySymbol field, INamedTypeSymbol model, IAssemblySymbol compiling) {
    if (_hasReasonedSuppression(field.GetAttributes())) {
      return true;
    }

    // Walks the bases, which is also what covers a property declared on one rather than on the model:
    // the decision belongs where the property is declared, since one base carries fields for many
    // models and repeating the attribute on each of them is how the reasons drift apart. A separate
    // test of the declaring type used to sit here and could never answer differently, because the only
    // way it differs from the model is inheritance, and a property reached through a composed type is
    // refused earlier than this by the walk that identifies the model.
    for (var type = model; type is not null; type = type.BaseType) {
      if (_hasReasonedSuppression(type.GetAttributes())) {
        return true;
      }
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
