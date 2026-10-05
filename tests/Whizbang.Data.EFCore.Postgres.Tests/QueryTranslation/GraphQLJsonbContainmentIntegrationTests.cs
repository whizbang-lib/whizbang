// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The consumer's path for a filter on a promoted jsonb column: a GraphQL list filter, built by HotChocolate as an
/// expression over the lens query, compiled by Entity Framework against a real database. It runs as containment,
/// is answered from the column's GIN index, and returns the rows the filter describes.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-filters</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class GraphQLJsonbContainmentIntegrationTests {
  private const string TABLE = "wh_per_jsonb_graph_item";

  private string _databaseName = null!;
  private string _connectionString = null!;

  /// <summary>The GraphQL query type: one lens over the jsonb-column model, with filtering.</summary>
  public sealed class GraphQuery {
    [UseFiltering]
    public IQueryable<PerspectiveRow<JsonbGraphItem.Model>> GetItems([Service] JsonbColumnsDbContext context) =>
      context.Set<PerspectiveRow<JsonbGraphItem.Model>>();
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("jsonb_graph");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = PhysicalJsonbContainmentIntegrationTests.Context(_connectionString);
    await context.EnsureWhizbangDatabaseInitializedAsync();

    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var seed = new NpgsqlCommand($"""
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version, labels, tags)
      SELECT gen_random_uuid(), jsonb_build_object('Title', 'item-' || g), jsonb_build_object(), jsonb_build_object(),
             now(), now(), now(), now(), 1,
             jsonb_build_array(jsonb_build_object('Key', 'team', 'Value', 't-' || g)),
             jsonb_build_array('tag-' || g)
      FROM generate_series(1, 2000) g;
      ANALYZE {TABLE};
      """, db);
    await seed.ExecuteNonQueryAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  [Test]
  public async Task AListFilterOnAJsonbColumn_RunsAsContainmentOnItsGinIndexAsync() {
    var capture = new PhysicalJsonbContainmentIntegrationTests.CommandCapture();
    var services = new ServiceCollection();
    services.AddScoped(_ => PhysicalJsonbContainmentIntegrationTests.Context(_connectionString, capture));
    services.AddGraphQLServer().AddWhizbangLenses().AddQueryType<GraphQuery>();
    await using var provider = services.BuildServiceProvider();
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("""
      {
        items(where: { data: { labels: { some: { key: { eq: "team" }, value: { eq: "t-42" } } } } }) {
          data { title }
        }
      }
      """);

    var json = result.ToJson();
    await Assert.That(json).Contains("\"title\": \"item-42\"");
    await Assert.That(json).DoesNotContain("\"errors\"");
    await Assert.That(json.Split("\"title\"").Length - 1).IsEqualTo(1);
    await Assert.That(capture.Text).Contains("labels @> ");

    var plan = await PhysicalJsonbContainmentIntegrationTests.PlanAsync(_connectionString, capture);
    await Assert.That(plan).Contains("idx_jsonb_graph_item_labels_gin");
  }

  [Test]
  public async Task AScalarListFilter_RunsAsContainmentTooAsync() {
    var capture = new PhysicalJsonbContainmentIntegrationTests.CommandCapture();
    var services = new ServiceCollection();
    services.AddScoped(_ => PhysicalJsonbContainmentIntegrationTests.Context(_connectionString, capture));
    services.AddGraphQLServer().AddWhizbangLenses().AddQueryType<GraphQuery>();
    await using var provider = services.BuildServiceProvider();
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("""
      { items(where: { data: { tags: { some: { eq: "tag-7" } } } }) { data { title } } }
      """);

    await Assert.That(result.ToJson()).Contains("\"title\": \"item-7\"");
    await Assert.That(capture.Text).Contains("tags @> ");
    await Assert.That(await PhysicalJsonbContainmentIntegrationTests.PlanAsync(_connectionString, capture))
      .Contains("idx_jsonb_graph_item_tags_gin");
  }
}
