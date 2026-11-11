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
/// property with id values. <c>in</c> is a chain of equalities rather than a <c>Contains</c> call: Entity Framework
/// Core translates an id equality both for a value-converted column and for an id inside a JSON document, but
/// cannot translate <c>Contains</c> over ids in a document.
/// </summary>
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

  /// <summary>
  /// Whether the property equals any of the ids: one equality per id, or false for none.
  /// </summary>
  /// <param name="property">The id property.</param>
  /// <param name="ids">The parsed list of ids.</param>
  /// <returns>The expression.</returns>
  internal static Expression AnyOf(Expression property, object? ids) =>
    ((IEnumerable)ids!).Cast<object?>().Aggregate(
      (Expression)Expression.Constant(false),
      (any, id) => Expression.OrElse(any, FilterExpressionBuilder.Equals(property, id)));
}
