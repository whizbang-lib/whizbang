// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using HotChocolate.Types;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures;
using Whizbang.Transports.HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

/// <summary>A perspective model whose id is promoted to a column of its own.</summary>
public sealed class PromotedIdDocument {
  public GraphQLOrderId Id { get; init; }
}

/// <summary>A perspective model with an optional id.</summary>
public sealed class OptionalIdDocument {
  public GraphQLOrderId? ParentId { get; init; }
}

/// <summary>An object with a Data member that is not a perspective row.</summary>
public sealed class DataHolder {
  public OrderDocument Data { get; init; } = new();
}

/// <summary>A generic object with a Data member that is not a perspective row.</summary>
public sealed class GenericDataHolder<T> where T : new() {
  public T Data { get; init; } = new();
}

/// <summary>
/// The handler of a strong-id filter operation is set on its own field, so the filter convention never chooses it
/// for another one, and the comparisons it builds (#2073): the inner <c>Value</c> for an id inside a perspective
/// document, the id itself everywhere else, and a balanced tree for a list.
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdFilterHandlerTests {
  [Test]
  public async Task CanHandle_AnyField_IsFalseAsync() {
    var handler = new WhizbangIdFilterHandler(
      new InputParser(), canBeNull: true, (property, _) => Expression.Constant(true));

    await Assert.That(handler.CanHandle(null!, null!, null!)).IsFalse();
  }

  private static readonly ParameterExpression _documentRow = Expression.Parameter(typeof(PerspectiveRow<OrderDocument>), "w");

  private static MemberExpression _documentField(string name) =>
    Expression.Property(Expression.Property(_documentRow, nameof(PerspectiveRow<>.Data)), name);

  private static int _depth(Expression expression) =>
    expression is BinaryExpression { NodeType: ExpressionType.OrElse } or
      ? 1 + Math.Max(_depth(or.Left), _depth(or.Right))
      : 0;

  [Test]
  public async Task IdEquals_InADocument_ComparesTheInnerValueAsync() {
    var id = GraphQLOrderId.New();

    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(_documentField(nameof(OrderDocument.Id)), id);

    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(Guid));
    await Assert.That(comparison.Left.ToString()).IsEqualTo("w.Data.Id.Value");
  }

  [Test]
  public async Task IdEquals_OnAColumn_ComparesTheIdAsync() {
    var row = Expression.Parameter(typeof(OrderRow), "o");

    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(Expression.Property(row, nameof(OrderRow.Id)), GraphQLOrderId.New());

    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(GraphQLOrderId));
  }

  private static MemberExpression _optionalField() {
    var row = Expression.Parameter(typeof(PerspectiveRow<OptionalIdDocument>), "w");
    return Expression.Property(
      Expression.Property(row, nameof(PerspectiveRow<>.Data)), nameof(OptionalIdDocument.ParentId));
  }

  [Test]
  public async Task IdEquals_AnOptionalIdInADocument_ComparesTheIdAsync() {
    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(_optionalField(), GraphQLOrderId.New());

    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(GraphQLOrderId?));
  }

  [Test]
  public async Task IdEquals_Null_ComparesTheIdWithNullAsync() {
    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(_optionalField(), null);

    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(GraphQLOrderId?));
  }

  [Test]
  public async Task IdEquals_AnIdPromotedToAColumn_ComparesTheIdAsync() {
    PerspectivePhysicalFieldRegistry.Register(
      typeof(PromotedIdDocument), nameof(PromotedIdDocument.Id), "id_column", FieldStorageMode.Split);
    var row = Expression.Parameter(typeof(PerspectiveRow<PromotedIdDocument>), "w");
    var property = Expression.Property(Expression.Property(row, nameof(PerspectiveRow<>.Data)), nameof(PromotedIdDocument.Id));

    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(property, GraphQLOrderId.New());

    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(GraphQLOrderId));
  }

  [Test]
  public async Task IdEquals_UnderADataMemberOfSomethingElse_ComparesTheIdAsync() {
    var holder = Expression.Parameter(typeof(DataHolder), "h");
    var generic = Expression.Parameter(typeof(GenericDataHolder<OrderDocument>), "g");

    var plain = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(
      Expression.Property(Expression.Property(holder, nameof(DataHolder.Data)), nameof(OrderDocument.Id)), GraphQLOrderId.New());
    var ofGeneric = (BinaryExpression)WhizbangIdFilterHandler.IdEquals(
      Expression.Property(Expression.Property(generic, nameof(GenericDataHolder<>.Data)), nameof(OrderDocument.Id)),
      GraphQLOrderId.New());

    await Assert.That(plain.Left.Type).IsEqualTo(typeof(GraphQLOrderId));
    await Assert.That(ofGeneric.Left.Type).IsEqualTo(typeof(GraphQLOrderId));
  }

  [Test]
  public async Task IdNotEquals_OnAColumn_ComparesTheIdAsync() {
    var row = Expression.Parameter(typeof(OrderRow), "o");

    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdNotEquals(Expression.Property(row, nameof(OrderRow.Id)), GraphQLOrderId.New());

    await Assert.That(comparison.NodeType).IsEqualTo(ExpressionType.NotEqual);
    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(GraphQLOrderId));
  }

  [Test]
  public async Task IdNotEquals_InADocument_ComparesTheInnerValueAsync() {
    var comparison = (BinaryExpression)WhizbangIdFilterHandler.IdNotEquals(
      _documentField(nameof(OrderDocument.Id)), GraphQLOrderId.New());

    await Assert.That(comparison.NodeType).IsEqualTo(ExpressionType.NotEqual);
    await Assert.That(comparison.Left.Type).IsEqualTo(typeof(Guid));
  }

  [Test]
  [Arguments(1, 0)]
  [Arguments(2, 1)]
  [Arguments(3, 2)]
  [Arguments(8, 3)]
  [Arguments(5_000, 13)]
  public async Task AnyOf_NestsTheComparisonsAsABalancedTreeAsync(int count, int depth) {
    var ids = Enumerable.Range(0, count).Select(_ => GraphQLOrderId.New()).ToList();

    var any = WhizbangIdFilterHandler.AnyOf(_documentField(nameof(OrderDocument.Id)), ids);

    await Assert.That(_depth(any)).IsEqualTo(depth);
  }

  [Test]
  public async Task AnyOf_WithNoIds_IsFalseAsync() {
    var any = WhizbangIdFilterHandler.AnyOf(_documentField(nameof(OrderDocument.Id)), Array.Empty<GraphQLOrderId>());

    await Assert.That(any).IsTypeOf<ConstantExpression>();
    await Assert.That(((ConstantExpression)any).Value).IsEqualTo(false);
  }
}
