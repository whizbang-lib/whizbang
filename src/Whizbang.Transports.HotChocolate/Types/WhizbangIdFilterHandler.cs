// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Linq.Expressions;
using HotChocolate.Configuration;
using HotChocolate.Data.Filters;
using HotChocolate.Data.Filters.Expressions;
using HotChocolate.Language;
using HotChocolate.Types;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Transports.HotChocolate.Types;

/// <summary>
/// Builds the expression of one operation of a <see cref="WhizbangIdOperationFilterInputType"/>, comparing the id
/// property with id values. <c>in</c> is a tree of equalities rather than a <c>Contains</c> call: Entity Framework
/// Core translates an id equality both for a value-converted column and for an id inside a JSON document, but
/// cannot translate <c>Contains</c> over ids in a document.
/// </summary>
/// <remarks>
/// <para>
/// An id inside a perspective row's document is compared by its inner <c>Value</c>
/// (<c>w.Data.OrderId.Value == guid</c>), everywhere else by the id itself. The document case is the one an index can answer: a strong id carries
/// no index over its own extraction (its stored form is an object, which no declared cast reaches), so the index that
/// answers it is the whole-document GIN index of <c>[PerspectiveQueries(MatchOnAnyField = true)]</c>, through the
/// containment rewrite, and that rewrite turns an equality on the inner value into <c>data @&gt; ...</c> but leaves an
/// equality on the id object as an unindexed extraction. A value-converted column, a field promoted to a column of its
/// own and an optional id keep the id-to-id comparison, which the provider maps through the column's converter.
/// </para>
/// <para>
/// The equalities of <c>in</c> are joined as a balanced tree, so a list of n ids nests about log2(n) deep. A chain
/// nested one level per id overflowed the stack of Entity Framework Core's expression visitors at a few thousand ids,
/// which ends the process rather than failing the request.
/// </para>
/// </remarks>
/// <docs>apis/graphql/filtering</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdFilteringTests.cs</tests>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdFilterTranslationTests.cs</tests>
internal sealed class WhizbangIdFilterHandler : QueryableOperationHandlerBase {
  private readonly Func<Expression, object?, Expression> _compare;

  /// <summary>Creates the handler of one operation.</summary>
  /// <param name="inputParser">The schema's input parser.</param>
  /// <param name="canBeNull">Whether the operation takes null (<c>eq</c>, <c>neq</c>) or not (<c>in</c>, <c>nin</c>).</param>
  /// <param name="compare">Builds the comparison from the id property and the parsed value.</param>
  public WhizbangIdFilterHandler(InputParser inputParser, bool canBeNull, Func<Expression, object?, Expression> compare)
      : base(inputParser) {
    CanBeNull = canBeNull;
    _compare = compare;
  }

  /// <summary>
  /// Never offered to other fields: <see cref="WhizbangIdOperationFilterInputType"/> sets this handler on its own
  /// operations, so the filter convention does not choose it.
  /// </summary>
  public override bool CanHandle(
      ITypeCompletionContext context,
      IFilterInputTypeDefinition typeDefinition,
      IFilterFieldDefinition fieldDefinition) => false;

  /// <inheritdoc />
  public override Expression HandleOperation(
      QueryableFilterContext context,
      IFilterOperationField field,
      IValueNode value,
      object? parsedValue) => _compare(context.GetInstance(), parsedValue);

  /// <summary>Whether the property equals the id.</summary>
  /// <param name="property">The id property.</param>
  /// <param name="id">The parsed id, or null.</param>
  /// <returns>The equality.</returns>
  internal static Expression IdEquals(Expression property, object? id) =>
    id is IWhizbangId value && _isInDocument(property)
      ? FilterExpressionBuilder.Equals(_valueOf(property), value.ToGuid())
      : FilterExpressionBuilder.Equals(property, id);

  /// <summary>Whether the property differs from the id.</summary>
  /// <param name="property">The id property.</param>
  /// <param name="id">The parsed id, or null.</param>
  /// <returns>The inequality.</returns>
  internal static Expression IdNotEquals(Expression property, object? id) =>
    id is IWhizbangId value && _isInDocument(property)
      ? FilterExpressionBuilder.NotEquals(_valueOf(property), value.ToGuid())
      : FilterExpressionBuilder.NotEquals(property, id);

  /// <summary>
  /// Whether the property equals any of the ids: one equality per id joined as a balanced tree, or false for none.
  /// </summary>
  /// <param name="property">The id property.</param>
  /// <param name="ids">The parsed list of ids.</param>
  /// <returns>The expression.</returns>
  internal static Expression AnyOf(Expression property, object? ids) {
    var equalities = ((IEnumerable)ids!).Cast<object?>().Select(id => IdEquals(property, id)).ToList();
    return equalities.Count == 0 ? Expression.Constant(false) : _balanced(equalities, 0, equalities.Count);
  }

  /// <summary>
  /// The id's Guid, read through the <c>Value</c> member its generated registration recorded. An id reaches a filter
  /// only once its scalar exists, which needs its recorded conversion, recorded by the same registration.
  /// </summary>
  private static MemberExpression _valueOf(Expression property) =>
    Expression.MakeMemberAccess(property, WhizbangIdProviderRegistry.GetValueMembers()[property.Type]);

  private static Expression _balanced(List<Expression> equalities, int start, int count) {
    if (count == 1) {
      return equalities[start];
    }

    var half = count / 2;
    return Expression.OrElse(_balanced(equalities, start, half), _balanced(equalities, start + half, count - half));
  }

  /// <summary>
  /// Whether the property is an id, not an optional one, read from a field of a perspective row's document that is
  /// not promoted to a column of its own.
  /// </summary>
  private static bool _isInDocument(Expression property) {
    if (Nullable.GetUnderlyingType(property.Type) is not null) {
      return false;
    }

    // Walks out to the model's own field, the member read straight from the row's Data.
    for (var member = property as MemberExpression; member is not null; member = member.Expression as MemberExpression) {
      if (member.Expression is MemberExpression { Member.Name: nameof(PerspectiveRow<>.Data) } data
          && _isPerspectiveRow(data.Expression!.Type)) {
        return !PerspectivePhysicalFieldRegistry.TryResolve(member.Member.DeclaringType!, member.Member.Name, out _);
      }
    }

    return false;
  }

  private static bool _isPerspectiveRow(Type type) =>
    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PerspectiveRow<>);
}
