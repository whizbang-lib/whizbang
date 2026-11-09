// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

/// <summary>A row with a strong id, for filtering and sorting.</summary>
[SuppressIndexAdvisory("An in-memory list in a schema test: no perspective table to index.")]
public sealed class OrderRow {
  public GraphQLOrderId Id { get; init; }
  public string Name { get; init; } = "";
}

#pragma warning disable S2325, CA1822 // HotChocolate binds the instance methods of a query type as its resolvers
/// <summary>A filterable, sortable list of rows with strong ids.</summary>
public class OrderRowQuery {
  internal static readonly GraphQLOrderId First = GraphQLOrderId.New();
  internal static readonly GraphQLOrderId Second = GraphQLOrderId.New();

  [UseFiltering]
  [UseSorting]
  public IQueryable<OrderRow> Orders() =>
    new[] { new OrderRow { Id = First, Name = "first" }, new OrderRow { Id = Second, Name = "second" } }.AsQueryable();
}
#pragma warning restore S2325, CA1822

/// <summary>
/// A strong id on a filterable, sortable row, with the lens conventions (#2073). The id scalar leaves both as they
/// were: filtering keeps the id's own filter input (<c>id: { value: { eq: ... } }</c>) and sorting orders by the id
/// itself (<c>id: DESC</c>).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdFilteringTests {
  private static async Task<IRequestExecutor> _executorAsync() =>
    await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<OrderRowQuery>()
      .AddWhizbangLenses()
      .BuildRequestExecutorAsync();

  [Test]
  public async Task Row_WithAnId_FiltersByTheIdsValueAsync() {
    var executor = await _executorAsync();

    var result = (IOperationResult)await executor.ExecuteAsync(
      $"{{ orders(where: {{ id: {{ value: {{ eq: \"{OrderRowQuery.Second.Value}\" }} }} }}) {{ id name }} }}");

    await Assert.That(executor.Schema.ToString()).Contains("id: GraphQLOrderIdFilterInput");
    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"id\": \"{OrderRowQuery.Second.Value}\"");
    await Assert.That(result.ToJson()).DoesNotContain("\"first\"");
  }

  [Test]
  public async Task Row_WithAnId_SortsByTheIdAsync() {
    var executor = await _executorAsync();

    var result = (IOperationResult)await executor.ExecuteAsync("{ orders(order: { id: DESC }) { name } }");

    await Assert.That(executor.Schema.ToString()).Contains("id: SortEnumType");
    await Assert.That(result.Errors).IsNull();
    var json = result.ToJson();
    await Assert.That(json.IndexOf("\"second\"", StringComparison.Ordinal))
      .IsLessThan(json.IndexOf("\"first\"", StringComparison.Ordinal));
  }
}
