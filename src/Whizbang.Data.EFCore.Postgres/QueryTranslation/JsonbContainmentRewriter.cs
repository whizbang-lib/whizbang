using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Rewrites an equality over a perspective's JSON member into a containment marker, so the compiled
/// SQL can be answered from the GIN index on the data column rather than by reading every row.
/// </summary>
/// <remarks>
/// <para>
/// Runs after <see cref="PhysicalFieldExpressionVisitor"/>, which is what keeps promoted fields out
/// of its way: by the time this sees the tree, a physical field is already an
/// <c>EF.Property</c> call over a real column and no longer looks like a JSON member.
/// </para>
/// <para>
/// <strong>What is deliberately left alone.</strong> Containment is not equality, and the places the
/// two disagree are the places this stands down:
/// </para>
/// <list type="bullet">
/// <item>A comparison against null. An extraction reads a missing key as SQL NULL, while containment
/// of an explicit JSON null does not match a key that is absent, so a row written before the property
/// existed would answer differently.</item>
/// <item>Anything but equality. Ranges, ordering and pattern matching cannot be expressed as
/// containment at all.</item>
/// <item>Members reached through a collection, where containment means subset rather than equality.</item>
/// <item>A comparison anywhere but a filter. In a projection or an ordering the comparison's own
/// value is surfaced, and an absent key reads as null from an extraction and as false from a
/// containment test; inside a filter both exclude the row, so the difference cannot be observed.</item>
/// <item>Types whose serialized text and PostgreSQL's generated text are not guaranteed to agree,
/// which is every date and time type, enumerations, and binary floating point.</item>
/// </list>
/// <para>
/// The rewrite also stands down entirely when <see cref="JsonbContainmentSwitch"/> is off, when the
/// model has not registered the translations, or when
/// <see cref="ProviderCapabilities.ContainmentRewriteRequired"/> becomes false because a future
/// provider does this itself.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonbContainmentSqlMatrixTests.cs</tests>
public sealed class JsonbContainmentRewriter : ExpressionVisitor {
  private readonly IModel? _model;

  /// <summary>Creates a rewriter for a model whose registrations decide whether it acts.</summary>
  /// <param name="model">The model being queried, or null to stand down.</param>
  public JsonbContainmentRewriter(IModel? model) => _model = model;

  private bool _enabled =>
    _model is not null
    && JsonbContainmentSwitch.Enabled
    && ProviderCapabilities.ContainmentRewriteRequired
    && _model.FindDbFunction(JsonbContainment.Overloads[0]) is not null;

  private int _negationDepth;
  private int _predicateDepth;

  /// <summary>
  /// The operators whose lambda argument decides which rows survive. Only inside one of these is a
  /// comparison a filter, and only there do an extraction and a containment test agree.
  /// </summary>
  /// <remarks>
  /// Matched by name, because the same names arrive from <c>Queryable</c>, <c>Enumerable</c> and
  /// Entity Framework's asynchronous extensions. Projection and ordering operators are deliberately
  /// absent: those surface the comparison's own value, and for a key that is absent an extraction
  /// yields null where containment yields false. In a filter both exclude the row, so the difference
  /// cannot be observed; in a projection or an ordering it can.
  /// </remarks>
  private static readonly HashSet<string> _predicateOperators = new(StringComparer.Ordinal) {
    "Where", "Any", "All", "Count", "LongCount", "TakeWhile", "SkipWhile",
    "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
  };

  /// <summary>
  /// Enters a predicate scope for the lambda arguments of a filtering operator.
  /// </summary>
  /// <param name="node">The call being visited.</param>
  /// <returns>The visited node.</returns>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    ArgumentNullException.ThrowIfNull(node);

    var name = node.Method.Name;
    if (name.EndsWith("Async", StringComparison.Ordinal)) {
      name = name[..^"Async".Length];
    }

    if (!_predicateOperators.Contains(name) || node.Arguments.Count < 2) {
      // Not a filtering operator. Inside one, an Equals call is the same comparison as == and is
      // rewritten the same way; outside one, nothing here applies.
      if (_enabled && _predicateDepth > 0 && _negationDepth == 0) {
        if (_tryRewriteEquals(node, out var asEquality)) {
          return asEquality;
        }

        if (_tryRewriteContains(node, out var asMembership)) {
          return asMembership;
        }
      }

      return base.VisitMethodCall(node);
    }

    // The source is not a predicate; the arguments after it are.
    var source = Visit(node.Arguments[0]);
    var arguments = new Expression[node.Arguments.Count];
    arguments[0] = source;

    _predicateDepth++;
    try {
      for (var i = 1; i < node.Arguments.Count; i++) {
        arguments[i] = Visit(node.Arguments[i]);
      }
    } finally {
      _predicateDepth--;
    }

    return node.Update(Visit(node.Object)!, arguments);
  }

  /// <summary>
  /// Tracks negation, because that is where the two forms stop agreeing.
  /// </summary>
  /// <remarks>
  /// An extraction of a key that is absent is SQL NULL, so the comparison is NULL and the row is
  /// excluded from a filter. Containment of the same absent key is false, and a filter excludes it
  /// too, which is why the rewrite is safe in a positive position. Under <c>NOT</c> the two diverge:
  /// <c>NOT NULL</c> is NULL and still excludes, while <c>NOT false</c> is true and includes. So a
  /// negated comparison keeps the extraction form.
  /// </remarks>
  /// <param name="node">The unary node being visited.</param>
  /// <returns>The visited node.</returns>
  protected override Expression VisitUnary(UnaryExpression node) {
    ArgumentNullException.ThrowIfNull(node);

    if (node.NodeType != ExpressionType.Not) {
      return base.VisitUnary(node);
    }

    _negationDepth++;
    try {
      return base.VisitUnary(node);
    } finally {
      _negationDepth--;
    }
  }

  /// <inheritdoc/>
  protected override Expression VisitBinary(BinaryExpression node) {
    ArgumentNullException.ThrowIfNull(node);

    if (node.NodeType != ExpressionType.Equal || !_enabled || _negationDepth > 0 || _predicateDepth == 0) {
      return base.VisitBinary(node);
    }

    var left = _stripConverts(node.Left);
    var right = _stripConverts(node.Right);

    // Either side may hold the member; the other must be a value.
    if (_tryRewrite(left, right, out var rewritten) || _tryRewrite(right, left, out rewritten)) {
      return rewritten;
    }

    return base.VisitBinary(node);
  }

  /// <summary>
  /// Treats an <c>Equals</c> call as the equality it is.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Covers the instance form <c>member.Equals(value)</c>, the static <c>string.Equals(a, b)</c> and
  /// the static <c>object.Equals(a, b)</c>, with either operand holding the member. These compile to
  /// the same comparison as <c>==</c>, so leaving them out would cost an index for nothing but the
  /// node type.
  /// </para>
  /// <para>
  /// A <see cref="StringComparison"/> argument is honored rather than ignored: only
  /// <see cref="StringComparison.Ordinal"/> is the comparison containment performs. Anything
  /// case-insensitive or culture-aware is a different question and keeps the extraction form.
  /// </para>
  /// </remarks>
  private bool _tryRewriteEquals(MethodCallExpression node, out Expression rewritten) {
    rewritten = Expression.Empty();

    if (!string.Equals(node.Method.Name, nameof(object.Equals), StringComparison.Ordinal)
        || node.Type != typeof(bool)) {
      return false;
    }

    Expression first;
    Expression second;
    Expression? comparison;

    if (node.Object is not null) {
      if (node.Arguments.Count is < 1 or > 2) {
        return false;
      }

      first = node.Object;
      second = node.Arguments[0];
      comparison = node.Arguments.Count == 2 ? node.Arguments[1] : null;
    } else {
      if (node.Arguments.Count is < 2 or > 3) {
        return false;
      }

      first = node.Arguments[0];
      second = node.Arguments[1];
      comparison = node.Arguments.Count == 3 ? node.Arguments[2] : null;
    }

    if (comparison is not null && comparison is not ConstantExpression { Value: StringComparison.Ordinal }) {
      return false;
    }

    var left = _stripConverts(first);
    var right = _stripConverts(second);

    return _tryRewrite(left, right, out rewritten) || _tryRewrite(right, left, out rewritten);
  }

  /// <summary>
  /// Turns "this field is any of these values" into a containment test against a set of documents.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Covers both spellings, <c>values.Contains(member)</c> as an instance call on a list and
  /// <c>Enumerable.Contains(values, member)</c> as a static one. The candidate collection is
  /// normalized to an array, which Entity Framework evaluates while extracting parameters because
  /// the subtree reads nothing from the row.
  /// </para>
  /// <para>
  /// Only a top-level member qualifies. The helper builds single-key documents and a nested path
  /// would need one nested document per candidate, which cannot be produced without a subquery. A
  /// nested member keeps the extraction form, which is correct and unindexed.
  /// </para>
  /// </remarks>
  private bool _tryRewriteContains(MethodCallExpression node, out Expression rewritten) {
    rewritten = Expression.Empty();

    if (!string.Equals(node.Method.Name, "Contains", StringComparison.Ordinal) || node.Type != typeof(bool)) {
      return false;
    }

    Expression collection;
    Expression member;

    if (node.Object is not null && node.Arguments.Count == 1) {
      collection = node.Object;
      member = node.Arguments[0];
    } else if (node.Object is null && node.Arguments.Count == 2) {
      collection = node.Arguments[0];
      member = node.Arguments[1];
    } else {
      return false;
    }

    member = _stripConverts(member);

    if (member is not MemberExpression m || !_isJsonMember(m) || !_isTopLevel(m)) {
      return false;
    }

    // The candidates have to be a value, not something read from the row being filtered.
    if (_referencesAQueryParameter(collection)) {
      return false;
    }

    // The collection arrives already parameterized, so it cannot be wrapped or converted: the
    // overload has to accept the shape as it stands. Arrays and lists are the two Npgsql maps to a
    // PostgreSQL array; anything else keeps the IN form, which is correct and unindexed.
    var overload = JsonbContainment.SetOverloadFor(m.Type, collection.Type);
    if (overload is null || _isValueConverted(m)) {
      return false;
    }

    var elementType = overload.GetParameters()[0].ParameterType;
    var memberArgument = m.Type == elementType ? (Expression)m : Expression.Convert(m, elementType);
    rewritten = Expression.Call(overload, memberArgument, collection);
    return true;
  }

  /// <summary>
  /// Whether the member sits directly on the document rather than inside a nested object, which is
  /// the only depth the set-membership helper can express.
  /// </summary>
  private static bool _isTopLevel(MemberExpression member) => member.Expression switch {
    MemberExpression inner => string.Equals(inner.Member.Name, nameof(PerspectiveRow<object>.Data), StringComparison.Ordinal),
    ParameterExpression => true,
    _ => false,
  };

  private bool _tryRewrite(Expression candidateMember, Expression candidateValue, out Expression rewritten) {
    rewritten = Expression.Empty();

    if (candidateMember is not MemberExpression member || !_isJsonMember(member)) {
      return false;
    }

    // The other side has to be a value, not a second column or a computed member of the same row.
    if (_referencesAQueryParameter(candidateValue)) {
      return false;
    }

    // Comparing against a literal null is the one case where containment answers differently.
    if (candidateValue is ConstantExpression { Value: null }) {
      return false;
    }

    var overload = JsonbContainment.OverloadFor(member.Type);
    if (overload is null || _isValueConverted(member)) {
      return false;
    }

    var parameterType = overload.GetParameters()[0].ParameterType;

    // A nullable member is compared through its underlying type; the rewrite only happens for a
    // non-null value, so the conversion cannot lose anything a containment test would have matched.
    var memberArgument = member.Type == parameterType ? member : (Expression)Expression.Convert(member, parameterType);
    var valueArgument = candidateValue.Type == parameterType
      ? candidateValue
      : Expression.Convert(candidateValue, parameterType);

    rewritten = Expression.Call(overload, memberArgument, valueArgument);
    return true;
  }

  /// <summary>
  /// Whether a value converter changes what this member's stored form looks like.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the difference between losing an index and giving a wrong answer, so it is checked here
  /// rather than at translation. A converted property is written in the converter's form: an
  /// <c>int</c> configured as text lands in the document as <c>"7"</c>, not <c>7</c>. The marker's
  /// parameter is typed by the CLR type, so the containment document would be built unconverted and
  /// match nothing, while the extraction it replaced matches fine because the text rendering of a
  /// JSON string and a JSON number are the same.
  /// </para>
  /// <para>
  /// Standing down at translation time is too late: by then the marker has been chosen and its
  /// parameter typed, and the only available fallback compares a text extraction with a numeric
  /// parameter, which the database rejects outright.
  /// </para>
  /// </remarks>
  private bool _isValueConverted(MemberExpression member) {
    if (_model is null) {
      return true;
    }

    // Rebuild the path from the document root outwards: Data.A.B has B outermost.
    var names = new List<string>();
    Type? rootModel = null;

    for (Expression? current = member; current is MemberExpression link; current = link.Expression) {
      if (string.Equals(link.Member.Name, nameof(PerspectiveRow<object>.Data), StringComparison.Ordinal)
          && _isPerspectiveRow(link.Expression?.Type)) {
        rootModel = link.Type;
        break;
      }

      names.Insert(0, link.Member.Name);

      if (link.Expression is ParameterExpression parameter) {
        rootModel = parameter.Type;
        break;
      }
    }

    if (rootModel is null || names.Count == 0) {
      return true;
    }

    var row = _model.FindEntityType(typeof(PerspectiveRow<>).MakeGenericType(rootModel));
    var complex = row?.FindComplexProperty(nameof(PerspectiveRow<object>.Data))?.ComplexType;
    if (complex is null) {
      return true;
    }

    for (var i = 0; i < names.Count - 1; i++) {
      complex = complex.FindComplexProperty(names[i])?.ComplexType;
      if (complex is null) {
        return true;
      }
    }

    var leaf = complex.FindProperty(names[^1]);
    if (leaf is null) {
      return true;
    }

    // The converter can sit on either the property or its type mapping depending on how it was
    // configured, so check both. The rule itself lives next to the emission that has to agree with
    // it: the two answering differently is what would compile a filter into a test matching nothing.
    var converter = leaf.GetValueConverter() ?? leaf.GetTypeMapping().Converter;
    return !JsonbContainment.StoredFormIsNatural(converter, leaf.ClrType);
  }

  /// <summary>
  /// Whether the expression is a member read out of a perspective's model document, rather than a
  /// real column or something else entirely.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Two shapes reach the same column. The obvious one keeps the row: <c>row.Data.Field</c>, where
  /// the chain passes through <c>Data</c> on a <see cref="PerspectiveRow{TModel}"/>.
  /// </para>
  /// <para>
  /// The other projects the row away first, <c>Query.Select(r =&gt; r.Data).Where(m =&gt; m.Field ==
  /// value)</c>, which is how most repositories are written. The predicate then reads a member of
  /// the model directly and there is no <c>Data</c> left in the chain, but the query still runs
  /// against the same table and the member still compiles to a path into the same JSON document. It
  /// is recognized by asking the model whether a perspective row exists for that model type, which
  /// is a much tighter test than the type's shape alone.
  /// </para>
  /// </remarks>
  private bool _isJsonMember(MemberExpression member) {
    for (var current = member.Expression; current is not null;) {
      switch (current) {
        case MemberExpression inner
          when string.Equals(inner.Member.Name, nameof(PerspectiveRow<object>.Data), StringComparison.Ordinal)
               && _isPerspectiveRow(inner.Expression?.Type):
          return true;

        case MemberExpression inner:
          current = inner.Expression;
          continue;

        // The root of the chain is the range variable itself, so this is a projected model.
        case ParameterExpression parameter:
          return _isProjectedPerspectiveModel(parameter.Type);

        default:
          return false;
      }
    }

    return false;
  }

  /// <summary>
  /// Whether a perspective is stored for this model type, which is what makes a bare member of it a
  /// path into the data column rather than an ordinary property.
  /// </summary>
  private bool _isProjectedPerspectiveModel(Type type) {
    if (_model is null || !type.IsClass || type == typeof(string)) {
      return false;
    }

    return _model.FindEntityType(typeof(PerspectiveRow<>).MakeGenericType(type)) is not null;
  }

  private static bool _isPerspectiveRow(Type? type) {
    for (var current = type; current is not null; current = current.BaseType) {
      if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(PerspectiveRow<>)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>Whether the subtree reads from the query's range variable, which makes it not a value.</summary>
  private static bool _referencesAQueryParameter(Expression expression) {
    var finder = new _parameterFinder();
    finder.Visit(expression);
    return finder.Found;
  }

  private static Expression _stripConverts(Expression expression) {
    var current = expression;
    while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary) {
      current = unary.Operand;
    }

    return current;
  }

  private sealed class _parameterFinder : ExpressionVisitor {
    public bool Found { get; private set; }

    protected override Expression VisitParameter(ParameterExpression node) {
      Found = true;
      return base.VisitParameter(node);
    }
  }
}
