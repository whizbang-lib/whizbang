// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The model properties a collective spec's setters assign, read from the expression tree without executing it
/// (#1045). A setter is <c>SetProperty(m =&gt; m.X, …)</c> or <c>UpsertElement(m =&gt; m.Items, …)</c>; the property is
/// the member path its selector names, dotted for a nested member (<c>Address.City</c>).
/// </summary>
/// <remarks>
/// A setter that points a row at another record, such as a key to an overlay or a template, reports that key: what a
/// reader perceives as changed belongs to the referenced record, which the framework cannot resolve.
/// </remarks>
/// <docs>fundamentals/messages/message-tags#changed-properties</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/CollectiveChangedPropertiesTests.cs</tests>
public static class CollectiveChangedProperties {
  /// <summary>The properties the setters assign, in the order they are written, each once.</summary>
  /// <param name="setters">A spec's setters.</param>
  /// <returns>The property paths; empty for setters that assign nothing the reader can name.</returns>
  public static IReadOnlyList<string> Of(LambdaExpression setters) {
    ArgumentNullException.ThrowIfNull(setters);
    var collector = new SetterCollector();
    collector.Visit(setters.Body);
    return collector.Ordered();
  }

  private sealed class SetterCollector : ExpressionVisitor {
    private readonly List<(int Depth, string Path)> _found = [];
    private int _depth;

    // A chain is written left to right but nests right to left, so the call written first is the deepest.
    public IReadOnlyList<string> Ordered() {
      var seen = new HashSet<string>(StringComparer.Ordinal);
      return [.. _found.OrderByDescending(f => f.Depth).Select(f => f.Path).Where(seen.Add)];
    }

    protected override Expression VisitMethodCall(MethodCallExpression node) {
      if (node.Method.Name is nameof(ICollectiveSetters<>.SetProperty) or nameof(ICollectiveSetters<>.UpsertElement)
          && node.Arguments.Count > 0
          && _path(node.Arguments[0]) is { } path) {
        _found.Add((_depth, path));
      }
      _depth++;
      try {
        return base.VisitMethodCall(node);
      } finally {
        _depth--;
      }
    }

    private static string? _path(Expression selector) {
      while (selector is UnaryExpression { NodeType: ExpressionType.Quote or ExpressionType.Convert } unary) {
        selector = unary.Operand;
      }
      if (selector is not LambdaExpression lambda) {
        return null;
      }
      var body = lambda.Body;
      while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert) {
        body = convert.Operand;
      }
      var names = new Stack<string>();
      while (body is MemberExpression member) {
        names.Push(member.Member.Name);
        body = member.Expression;
      }
      return body is ParameterExpression && names.Count > 0 ? string.Join('.', names) : null;
    }
  }
}
