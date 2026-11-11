// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using HotChocolate.Data.Filters;
using HotChocolate.Data.Filters.Expressions;
using HotChocolate.Language;
using HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate.Types;

/// <summary>
/// The filter input of one <c>[WhizbangId]</c> type, named after it (<c>OrderIdOperationFilterInput</c>): <c>eq</c>,
/// <c>neq</c>, <c>in</c> and <c>nin</c>, each operand being the id's own scalar, so a filter value goes through the
/// id's <c>From(Guid)</c> and fails with the same error as an argument. Each operation compares the id with an id
/// (<see cref="WhizbangIdFilterHandler"/>), never with a bare Guid, so a query provider maps the operand the way it
/// maps the property: through a value converter for an id column, or as the id's JSON object inside a perspective
/// row's document.
/// </summary>
/// <docs>apis/graphql/filtering</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdFilteringTests.cs</tests>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdFilterTranslationTests.cs</tests>
/// <param name="idType">The <c>[WhizbangId]</c> struct type.</param>
/// <param name="inputParser">The schema's input parser, which the operations' handlers read their values with.</param>
internal sealed class WhizbangIdOperationFilterInputType(Type idType, InputParser inputParser) : FilterInputType {
  /// <inheritdoc />
  protected override void Configure(IFilterInputTypeDescriptor descriptor) {
    descriptor.Name($"{idType.Name}OperationFilterInput");

    // The list operands name the id's scalar, which the eq operand's runtime type has already added to the schema.
    var ids = new ListTypeNode(new NonNullTypeNode(new NamedTypeNode(idType.Name)));

    _handle(descriptor.Operation(DefaultFilterOperations.Equals).Type(idType),
      canBeNull: true, FilterExpressionBuilder.Equals);
    _handle(descriptor.Operation(DefaultFilterOperations.NotEquals).Type(idType),
      canBeNull: true, FilterExpressionBuilder.NotEquals);
    _handle(descriptor.Operation(DefaultFilterOperations.In).Type(ids),
      canBeNull: false, WhizbangIdFilterHandler.AnyOf);
    _handle(descriptor.Operation(DefaultFilterOperations.NotIn).Type(ids),
      canBeNull: false, (property, values) => Expression.Not(WhizbangIdFilterHandler.AnyOf(property, values)));

    descriptor.AllowAnd(false).AllowOr(false);
  }

  /// <summary>
  /// Gives an operation its handler up front, so the filter convention does not look one up: no convention's
  /// handlers know this type. An operation that takes null has a nullable operand (the list operands already are).
  /// </summary>
  private void _handle(
      IFilterOperationFieldDescriptor operation,
      bool canBeNull,
      Func<Expression, object?, Expression> compare) {
    if (canBeNull) {
      operation.MakeNullable();
    }

    operation.Extend().Definition.Handler = new WhizbangIdFilterHandler(inputParser, canBeNull, compare);
  }
}
