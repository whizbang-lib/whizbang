using System.Linq.Expressions;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation.Compatibility;

/// <summary>
/// Replaces an ordinal <c>Equals</c> with the equality it is, so Entity Framework will translate it.
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework refuses to translate <c>Equals</c> with a <see cref="StringComparison"/> argument
/// at all, and the refusal is principled rather than cautious. Such a call would have to compile to
/// <c>=</c> on text, whose meaning follows the column's collation, and that is not what any
/// particular <see cref="StringComparison"/> asks for. Rather than pick a translation that is right
/// for some collations and wrong for others, it declines.
/// </para>
/// <para>
/// For <see cref="StringComparison.Ordinal"/> the answer is not a matter of opinion. Ordinal
/// comparison is byte equality, which is exactly what <c>=</c> means under a deterministic collation,
/// so the call can be replaced with <c>==</c> and translated correctly. Nothing else can:
/// a case-insensitive or culture-aware comparison means something SQL equality does not, and
/// translating it would be a claim rather than a convenience.
/// </para>
/// <para>
/// <strong>Why this is its own pass.</strong> It does one thing, turn an untranslatable shape into a
/// translatable one, and knows nothing about containment or indexes. Whatever compiles the result, if
/// anything, then sees an ordinary equality and needs no special case for it. That is also the only
/// reason it has to run on the expression tree: after translation the refusal has already been
/// raised, so there is nothing left to inspect.
/// </para>
/// <para>
/// No reflection. The method is recognized by name and signature shape on a known declaring type.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/OrdinalEqualsRewriterTests.cs</tests>
public sealed class OrdinalEqualsRewriter : ExpressionVisitor {
  /// <inheritdoc/>
  protected override Expression VisitMethodCall(MethodCallExpression node) {
    ArgumentNullException.ThrowIfNull(node);

    if (!_isOrdinalEquals(node, out var left, out var right)) {
      return base.VisitMethodCall(node);
    }

    return Expression.Equal(Visit(left), Visit(right));
  }

  /// <summary>
  /// Whether this call is a string <c>Equals</c> that asks for ordinal comparison, and if so what it
  /// compares.
  /// </summary>
  /// <remarks>
  /// Covers the instance form and the static one, and requires the comparison to be a literal. A
  /// comparison supplied as a variable is not known here, and assuming it is ordinal would turn a
  /// case-insensitive filter into a case-sensitive one, which is a wrong answer rather than a lost
  /// index.
  /// </remarks>
  private static bool _isOrdinalEquals(
      MethodCallExpression node,
      out Expression left,
      out Expression right) {
    left = node;
    right = node;

    if (!string.Equals(node.Method.Name, nameof(string.Equals), StringComparison.Ordinal)
        || node.Method.DeclaringType != typeof(string)
        || node.Type != typeof(bool)) {
      return false;
    }

    // Instance: a.Equals(b, comparison). Static: string.Equals(a, b, comparison). An overload
    // without a comparison argument is one Entity Framework already translates, so it is left alone.
    Expression first;
    Expression second;
    Expression comparison;

    if (node.Object is not null) {
      if (node.Arguments.Count != 2) {
        return false;
      }

      first = node.Object;
      second = node.Arguments[0];
      comparison = node.Arguments[1];
    } else {
      if (node.Arguments.Count != 3) {
        return false;
      }

      first = node.Arguments[0];
      second = node.Arguments[1];
      comparison = node.Arguments[2];
    }

    if (comparison is not ConstantExpression { Value: StringComparison.Ordinal }) {
      return false;
    }

    left = first;
    right = second;
    return true;
  }
}
