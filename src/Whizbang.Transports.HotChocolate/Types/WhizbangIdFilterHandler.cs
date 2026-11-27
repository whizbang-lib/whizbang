// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Linq.Expressions;
using HotChocolate.Configuration;
using HotChocolate.Data.Filters;
using HotChocolate.Data.Filters.Expressions;
using HotChocolate.Language;
using HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate.Types;

/// <summary>
/// Builds the expression of one operation of a <see cref="WhizbangIdOperationFilterInputType"/>, comparing the id
/// property with id values. <c>in</c> is a tree of equalities rather than a <c>Contains</c> call, so each equality can
/// be answered by an index the way a single <c>eq</c> is.
/// </summary>
/// <remarks>
/// <para>
/// The id is compared with the id itself everywhere: a value-converted column, a field promoted to a column of its own,
/// an optional id, and an id inside a perspective row's document. Inside a document a strongly-typed id is a uuid
/// scalar stored as its UUID string (#2518), so the equality translates to a uuid comparison of the stored text.
/// Before #2518 the id was stored as the object <c>{"Value": ...}</c> and a document's id was compared by its inner
/// <c>Value</c>, a member the scalar mapping no longer has.
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
    FilterExpressionBuilder.Equals(property, id);

  /// <summary>Whether the property differs from the id.</summary>
  /// <param name="property">The id property.</param>
  /// <param name="id">The parsed id, or null.</param>
  /// <returns>The inequality.</returns>
  internal static Expression IdNotEquals(Expression property, object? id) =>
    FilterExpressionBuilder.NotEquals(property, id);

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

  private static Expression _balanced(List<Expression> equalities, int start, int count) {
    if (count == 1) {
      return equalities[start];
    }

    var half = count / 2;
    return Expression.OrElse(_balanced(equalities, start, half), _balanced(equalities, start + half, count - half));
  }
}
