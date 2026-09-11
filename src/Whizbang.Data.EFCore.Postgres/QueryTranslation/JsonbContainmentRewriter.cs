using System.Linq.Expressions;
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

  private static bool _tryRewrite(Expression candidateMember, Expression candidateValue, out Expression rewritten) {
    rewritten = Expression.Empty();

    if (candidateMember is not MemberExpression member || !_isJsonMemberOfPerspectiveData(member)) {
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
    if (overload is null) {
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
  /// Whether the expression is a member read out of a perspective row's model document, rather than
  /// a real column or something else entirely.
  /// </summary>
  private static bool _isJsonMemberOfPerspectiveData(MemberExpression member) {
    // Walk in towards the row: Data.A.B.C has C outermost.
    for (var current = member.Expression; current is not null;) {
      if (current is MemberExpression inner) {
        if (string.Equals(inner.Member.Name, nameof(PerspectiveRow<object>.Data), StringComparison.Ordinal)
            && _isPerspectiveRow(inner.Expression?.Type)) {
          return true;
        }

        current = inner.Expression;
        continue;
      }

      return false;
    }

    return false;
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
