// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// A GraphQL filter on a strong id in a perspective document (<c>orderId: { eq: "..." }</c>, <c>neq</c>, <c>in</c>,
/// <c>nin</c>), through <c>AddWhizbangLenses()</c>, against the generated perspective mapping and a real database:
/// Entity Framework Core translates each operation to SQL (it throws for a filter it cannot translate rather than
/// evaluating it in memory), and the SQL matches the rows the upsert wrote (#2073).
/// </summary>
/// <docs>apis/graphql/filtering</docs>
[Category("Integration")]
[Category("Shard1")]
public class GraphQLWhizbangIdFilterIntegrationTests : EFCoreTestBase {
  private const string TABLE = "wh_per_order";

  private static readonly DateTime _seededAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private readonly TestOrderId _first = TestOrderId.New();
  private readonly TestOrderId _second = TestOrderId.New();
  private readonly TestOrderId _third = TestOrderId.New();

  /// <summary>The GraphQL query type: one lens over the order perspective, with filtering.</summary>
  public sealed class GraphQuery {
    [UseFiltering]
    public IQueryable<PerspectiveRow<Order>> GetOrders([Service] WorkCoordinationDbContext context) =>
      context.Set<PerspectiveRow<Order>>();
  }

  [Before(Test)]
  public async Task SeedAsync() {
    await using var context = CreateDbContext();
    var strategy = new PostgresUpsertStrategy();
    foreach (var (id, status) in new[] { (_first, "first"), (_second, "second"), (_third, "third") }) {
      var order = new Order { OrderId = id, Amount = 1m, Status = status };
      var metadata = new PerspectiveMetadata { EventType = "OrderCreated", EventId = id.Value.ToString(), Timestamp = _seededAt };
      await strategy.UpsertPerspectiveRowAsync(context, TABLE, id.Value, order, metadata, new PerspectiveScope());
    }
  }

  private async Task<(string Json, string Sql)> _filterAsync(string where) {
    var capture = new PhysicalJsonbContainmentIntegrationTests.CommandCapture();
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions).AddInterceptors(capture).Options;
    var services = new ServiceCollection();
    services.AddScoped(_ => new WorkCoordinationDbContext(options));
    services.AddGraphQLServer().AddWhizbangLenses().AddQueryType<GraphQuery>();
    await using var provider = services.BuildServiceProvider();
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync($"{{ orders(where: {{ data: {{ orderId: {where} }} }}) {{ data {{ status }} }} }}");

    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("\"errors\"").Because(json);
    return (json, capture.Text);
  }

  [Test]
  public async Task Eq_FindsTheRowWithThatIdInSqlAsync() {
    var (json, sql) = await _filterAsync($"{{ eq: \"{_second.Value}\" }}");

    await Assert.That(json).Contains("\"second\"").Because(json);
    await Assert.That(json).DoesNotContain("\"first\"").Because(json);
    await Assert.That(json).DoesNotContain("\"third\"").Because(json);
    await Assert.That(sql).Contains("WHERE (CAST(w.data #>> '{OrderId,Value}' AS uuid)) = @").Because(sql);
  }

  [Test]
  public async Task Neq_DropsTheRowWithThatIdInSqlAsync() {
    var (json, sql) = await _filterAsync($"{{ neq: \"{_second.Value}\" }}");

    await Assert.That(json).Contains("\"first\"").Because(json);
    await Assert.That(json).DoesNotContain("\"second\"").Because(json);
    await Assert.That(json).Contains("\"third\"").Because(json);
    await Assert.That(sql).Contains("WHERE (CAST(w.data #>> '{OrderId,Value}' AS uuid)) <> @").Because(sql);
  }

  [Test]
  public async Task InAndNin_MatchEachIdInSqlAsync() {
    var ids = $"[\"{_first.Value}\", \"{_third.Value}\"]";

    var (inJson, inSql) = await _filterAsync($"{{ in: {ids} }}");
    var (ninJson, ninSql) = await _filterAsync($"{{ nin: {ids} }}");

    await Assert.That(inJson).Contains("\"first\"").Because(inJson);
    await Assert.That(inJson).DoesNotContain("\"second\"").Because(inJson);
    await Assert.That(inJson).Contains("\"third\"").Because(inJson);
    await Assert.That(inSql).Contains(" OR ").Because(inSql);
    await Assert.That(ninJson).DoesNotContain("\"first\"").Because(ninJson);
    await Assert.That(ninJson).Contains("\"second\"").Because(ninJson);
    await Assert.That(ninJson).DoesNotContain("\"third\"").Because(ninJson);
    await Assert.That(ninSql).Contains(" AND ").Because(ninSql);
  }

  [Test]
  public async Task InAndNin_WithThousandsOfIds_AnswerTheRequestAsync() {
    // Thousands of ids are thousands of comparisons. Nested one level per id they overflowed the stack of Entity
    // Framework Core's expression visitors and ended the process; balanced, they translate and the request answers.
    var ids = Enumerable.Range(0, 5_000).Select(_ => TestOrderId.New().Value.ToString())
      .Append(_first.Value.ToString())
      .Append(_third.Value.ToString());
    var list = $"[{string.Join(", ", ids.Select(id => $"\"{id}\""))}]";

    var (inJson, _) = await _filterAsync($"{{ in: {list} }}");
    var (ninJson, _) = await _filterAsync($"{{ nin: {list} }}");

    await Assert.That(inJson).Contains("\"first\"").Because(inJson);
    await Assert.That(inJson).DoesNotContain("\"second\"").Because(inJson);
    await Assert.That(inJson).Contains("\"third\"").Because(inJson);
    await Assert.That(ninJson).DoesNotContain("\"first\"").Because(ninJson);
    await Assert.That(ninJson).Contains("\"second\"").Because(ninJson);
    await Assert.That(ninJson).DoesNotContain("\"third\"").Because(ninJson);
  }
}
