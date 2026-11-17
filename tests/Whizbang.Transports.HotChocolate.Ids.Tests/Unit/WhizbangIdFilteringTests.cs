// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

/// <summary>A row with a strong id, and an optional one, for filtering and sorting.</summary>
[SuppressIndexAdvisory("An in-memory list in a schema test: no perspective table to index.")]
public sealed class OrderRow {
  public GraphQLOrderId Id { get; init; }
  public GraphQLOrderId? ParentId { get; init; }
  public string Name { get; init; } = "";
}

/// <summary>The document of a perspective row with a strong id.</summary>
[SuppressIndexAdvisory("An in-memory list in a schema test: no perspective table to index.")]
public sealed class OrderDocument {
  public GraphQLOrderId Id { get; init; }
  public string Name { get; init; } = "";
}

#pragma warning disable S2325, CA1822 // HotChocolate binds the instance methods of a query type as its resolvers
/// <summary>Filterable, sortable lists of rows with strong ids: plain rows and lens rows.</summary>
public class OrderRowQuery {
  internal static readonly GraphQLOrderId First = GraphQLOrderId.New();
  internal static readonly GraphQLOrderId Second = GraphQLOrderId.New();
  internal static readonly GraphQLOrderId Third = GraphQLOrderId.New();

  [UseFiltering]
  [UseSorting]
  public IQueryable<OrderRow> Orders() =>
    new[] {
      new OrderRow { Id = First, Name = "first" },
      new OrderRow { Id = Second, ParentId = First, Name = "second" },
      new OrderRow { Id = Third, Name = "third" }
    }.AsQueryable();

  [UseFiltering]
  public IQueryable<PerspectiveRow<OrderDocument>> OrderRows() =>
    new[] { _row(First, "first"), _row(Second, "second") }.AsQueryable();

  private static PerspectiveRow<OrderDocument> _row(GraphQLOrderId id, string name) => new() {
    Id = id.Value,
    Data = new OrderDocument { Id = id, Name = name },
    Metadata = new PerspectiveMetadata { EventType = "OrderCreated", EventId = id.Value.ToString(), Timestamp = DateTime.UnixEpoch },
    Scope = new PerspectiveScope(),
    CreatedAt = DateTime.UnixEpoch,
    UpdatedAt = DateTime.UnixEpoch,
    Version = 1
  };
}
#pragma warning restore S2325, CA1822

/// <summary>
/// A strong id on a filterable, sortable row, with the lens conventions (#2073). A filter compares the id itself:
/// <c>id: { eq: "..." }</c>, with <c>neq</c>, <c>in</c> and <c>nin</c>, each operand being the id's own scalar, so
/// a filter value is checked by the id's <c>From(Guid)</c> just as an argument is. Sorting orders by the id
/// (<c>id: DESC</c>).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdFilteringTests {
  private const string V4 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

  private static async Task<IRequestExecutor> _executorAsync() =>
    await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<OrderRowQuery>()
      .AddWhizbangLenses()
      .BuildRequestExecutorAsync();

  private static async Task<IOperationResult> _executeAsync(string document, Dictionary<string, object?>? variables = null) {
    var executor = await _executorAsync();
    var request = OperationRequestBuilder.New().SetDocument(document);
    if (variables is not null) {
      request.SetVariableValues(variables);
    }

    return (IOperationResult)await executor.ExecuteAsync(request.Build());
  }

  private static async Task<string> _namesAsync(string where) {
    var result = await _executeAsync($"{{ orders(where: {where}) {{ name }} }}");

    await Assert.That(result.Errors).IsNull();
    return result.ToJson();
  }

  [Test]
  public async Task Schema_FiltersAnIdWithAnOperationInputOverItsScalarAsync() {
    var sdl = (await _executorAsync()).Schema.ToString();

    await Assert.That(sdl).Contains("id: GraphQLOrderIdOperationFilterInput");
    await Assert.That(sdl).Contains("parentId: GraphQLOrderIdOperationFilterInput");
    await Assert.That(sdl).Contains("input GraphQLOrderIdOperationFilterInput {");
    await Assert.That(sdl).Contains("eq: GraphQLOrderId");
    await Assert.That(sdl).Contains("neq: GraphQLOrderId");
    await Assert.That(sdl).Contains("in: [GraphQLOrderId!]");
    await Assert.That(sdl).Contains("nin: [GraphQLOrderId!]");
    await Assert.That(sdl).DoesNotContain("GraphQLOrderIdFilterInput");
  }

  [Test]
  public async Task Eq_KeepsTheRowWithThatIdAsync() {
    var json = await _namesAsync($"{{ id: {{ eq: \"{OrderRowQuery.Second.Value}\" }} }}");

    await Assert.That(json).Contains("\"second\"");
    await Assert.That(json).DoesNotContain("\"first\"");
    await Assert.That(json).DoesNotContain("\"third\"");
  }

  [Test]
  public async Task Neq_DropsTheRowWithThatIdAsync() {
    var json = await _namesAsync($"{{ id: {{ neq: \"{OrderRowQuery.Second.Value}\" }} }}");

    await Assert.That(json).Contains("\"first\"");
    await Assert.That(json).DoesNotContain("\"second\"");
    await Assert.That(json).Contains("\"third\"");
  }

  [Test]
  public async Task In_KeepsTheRowsWithThoseIdsAsync() {
    var json = await _namesAsync(
      $"{{ id: {{ in: [\"{OrderRowQuery.First.Value}\", \"{OrderRowQuery.Third.Value}\"] }} }}");

    await Assert.That(json).Contains("\"first\"");
    await Assert.That(json).DoesNotContain("\"second\"");
    await Assert.That(json).Contains("\"third\"");
  }

  [Test]
  public async Task Nin_DropsTheRowsWithThoseIdsAsync() {
    var json = await _namesAsync(
      $"{{ id: {{ nin: [\"{OrderRowQuery.First.Value}\", \"{OrderRowQuery.Third.Value}\"] }} }}");

    await Assert.That(json).DoesNotContain("\"first\"");
    await Assert.That(json).Contains("\"second\"");
    await Assert.That(json).DoesNotContain("\"third\"");
  }

  [Test]
  public async Task In_WithNoIds_KeepsNoRowAsync() {
    var json = await _namesAsync("{ id: { in: [] } }");

    await Assert.That(json).Contains("\"orders\": []");
  }

  [Test]
  public async Task Eq_OnAnOptionalId_ComparesTheIdAndNullAsync() {
    var withParent = await _namesAsync($"{{ parentId: {{ eq: \"{OrderRowQuery.First.Value}\" }} }}");
    var withoutParent = await _namesAsync("{ parentId: { eq: null } }");

    await Assert.That(withParent).Contains("\"second\"");
    await Assert.That(withParent).DoesNotContain("\"first\"");
    await Assert.That(withoutParent).Contains("\"first\"");
    await Assert.That(withoutParent).DoesNotContain("\"second\"");
  }

  [Test]
  public async Task Eq_FromAVariable_KeepsTheRowWithThatIdAsync() {
    var result = await _executeAsync(
      "query($id: GraphQLOrderId!) { orders(where: { id: { eq: $id } }) { name } }",
      new Dictionary<string, object?> { ["id"] = OrderRowQuery.Second.Value.ToString() });

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains("\"second\"");
    await Assert.That(result.ToJson()).DoesNotContain("\"first\"");
  }

  [Test]
  public async Task Eq_WithAGuidTheIdRefuses_IsTheIdsErrorAsync() {
    var result = await _executeAsync($"{{ orders(where: {{ id: {{ eq: \"{V4}\" }} }}) {{ name }} }}");

    await Assert.That(result.Errors).IsNotNull();
    await Assert.That(result.Errors!.Count).IsEqualTo(1);
    await Assert.That(result.Errors[0].Message)
      .IsEqualTo("The value is not a valid GraphQLOrderId: GraphQLOrderId requires UUIDv7 but received version 4");
    await Assert.That(result.Errors[0].Code).IsEqualTo("WHIZBANG_INVALID_ID");
  }

  [Test]
  public async Task In_WithAValueThatIsNotAUuid_IsTheIdsErrorAsync() {
    var result = await _executeAsync("{ orders(where: { id: { in: [\"not-a-uuid\"] } }) { name } }");

    await Assert.That(result.Errors).IsNotNull();
    await Assert.That(result.Errors!.Count).IsEqualTo(1);
    await Assert.That(result.Errors[0].Message).IsEqualTo("The value is not a valid GraphQLOrderId: it is not a UUID.");
    await Assert.That(result.Errors[0].Code).IsEqualTo("WHIZBANG_INVALID_ID");
  }

  [Test]
  public async Task LensRow_FiltersOnTheIdInItsDocumentAsync() {
    var result = await _executeAsync(
      $"{{ orderRows(where: {{ data: {{ id: {{ eq: \"{OrderRowQuery.Second.Value}\" }} }} }}) {{ data {{ id name }} }} }}");

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
