// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Testing.Containers;
using Whizbang.Transports.HotChocolate;
using SchemaRegistry = Whizbang.Generators.Shared.Utilities.PerspectiveRegistryPayload;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// That a GraphQL filter on a strong id inside a perspective document is answered by the index the schema builds for
/// the document, not by a sequential scan (#2073).
/// </summary>
/// <remarks>
/// <para>
/// A strong id cannot carry an index over its own extraction: its document form is an object, which no declared
/// cast reaches (WHIZ303). The index that answers an equality on it is the whole-document GIN index a model asks for
/// with <c>[PerspectiveQueries(MatchOnAnyField = true)]</c>, through the containment rewrite, which turns an equality
/// on a value inside the document into <c>data @&gt; ...</c>. The rewrite answers the id's inner <c>Value</c>, not the
/// id object, so the filter compares that.
/// </para>
/// <para>
/// The index is built from the schema's own declaration of it (<see cref="SchemaRegistry.Indexes"/>, the
/// decisions the table DDL makes), the context runs the shipped query pipeline (<c>UseWhizbangPhysicalFields</c>,
/// <c>UseWhizbangJsonbContainment</c>), and the query comes from the GraphQL filter, so a change on either side that
/// stops the index from answering fails here rather than as a slower query in production. Sequential scans are
/// switched off for the plan, so the planner takes the index whenever it can answer the filter at all.
/// </para>
/// </remarks>
/// <docs>apis/graphql/filtering</docs>
[Category("Integration")]
[Category("Shard5")]
public class GraphQLWhizbangIdFilterIndexTests : IAsyncDisposable {
  private const string TABLE = "wh_per_id_filter";

  // Enough rows that the plan is about the index rather than a table that fits in a page.
  private const int ROWS = 20_000;

  private static readonly DateTime _seededAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private readonly TestOrderId _first = TestOrderId.New();
  private readonly TestOrderId _second = TestOrderId.New();
  private readonly TestOrderId _third = TestOrderId.New();

  private string _databaseName = null!;
  private DbContextOptions<IdFilterIndexDbContext> _options = null!;

  /// <summary>A perspective model with a strong id in its document.</summary>
  [SuppressIndexAdvisory("this fixture exists to show which index answers an id filter")]
  public sealed class IdFilterDocument {
    public TestOrderId OrderId { get; init; }
    public string Status { get; init; } = "";
  }

  /// <summary>The perspective row mapped as the generated configuration maps it, with the shipped query pipeline.</summary>
  public sealed class IdFilterIndexDbContext(DbContextOptions<IdFilterIndexDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<IdFilterDocument>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      });

      modelBuilder.UseWhizbangJsonbContainment();
    }
  }

  /// <summary>The GraphQL query type: one lens over the perspective, with filtering.</summary>
  public sealed class GraphQuery {
    [UseFiltering]
    public IQueryable<PerspectiveRow<IdFilterDocument>> GetRows([Service] IdFilterIndexDbContext context) =>
      context.Set<PerspectiveRow<IdFilterDocument>>();
  }

  /// <summary>
  /// Plans each query the filter runs, on the query's own connection with sequential scans off, before running it.
  /// </summary>
  private sealed class PlanCapture : DbCommandInterceptor {
    public string Plan { get; private set; } = "";

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default) {
      var connection = (NpgsqlConnection)command.Connection!;
      await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection)) {
        await off.ExecuteNonQueryAsync(cancellationToken);
      }

      await using (var explain = new NpgsqlCommand($"EXPLAIN {command.CommandText}", connection)) {
        foreach (NpgsqlParameter parameter in command.Parameters) {
          explain.Parameters.Add(parameter.Clone());
        }

        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
          lines.Add(reader.GetString(0));
        }

        Plan = string.Join('\n', lines);
      }

      await using (var reset = new NpgsqlCommand("RESET enable_seqscan", connection)) {
        await reset.ExecuteNonQueryAsync(cancellationToken);
      }

      return result;
    }
  }

  /// <summary>The whole-document index the schema builds for a model that matches on any field.</summary>
  private static IndexSchema _documentIndex =>
    SchemaRegistry.Indexes(new PerspectiveRegistryEntry(
        nameof(IdFilterDocument), TABLE, nameof(IdFilterDocument), [], BuildsDataIndex: true, BuildsMetadataIndex: false))
      .Single(index => index.Columns.SequenceEqual(["data"]));

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("id_filter_index")).Name;
    var connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;

    var index = _documentIndex;
    await _executeAsync(connectionString, $"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version integer NOT NULL,
        data jsonb NOT NULL,
        metadata jsonb NOT NULL,
        scope jsonb NOT NULL
      );
      CREATE INDEX {index.Name} ON {TABLE} USING {index.Type} ({string.Join(", ", index.Columns)});
      """);

    _options = new DbContextOptionsBuilder<IdFilterIndexDbContext>()
      .UseNpgsql(connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

    // The rows the filters look for go through the real mapping, so the document's shape is the shipped one; the
    // bulk is written in SQL in that same shape.
    await using (var context = new IdFilterIndexDbContext(_options)) {
      foreach (var (id, status) in new[] { (_first, "first"), (_second, "second"), (_third, "third") }) {
        context.Add(new PerspectiveRow<IdFilterDocument> {
          Id = id.Value,
          Data = new IdFilterDocument { OrderId = id, Status = status },
          Metadata = new PerspectiveMetadata { EventType = "OrderCreated", EventId = id.Value.ToString(), Timestamp = _seededAt },
          Scope = new PerspectiveScope(),
          CreatedAt = _seededAt,
          UpdatedAt = _seededAt,
          Version = 1,
        });
      }

      await context.SaveChangesAsync();
    }

    await _executeAsync(connectionString, $"""
      INSERT INTO {TABLE} (id, created_at, updated_at, version, data, metadata, scope)
      SELECT gen_random_uuid(), shape.created_at, shape.updated_at, 1,
             jsonb_build_object('OrderId', jsonb_build_object('Value', gen_random_uuid()::text), 'Status', 'bulk'),
             shape.metadata, shape.scope
      FROM generate_series(1, {ROWS}) g
      CROSS JOIN (SELECT created_at, updated_at, metadata, scope FROM {TABLE} LIMIT 1) shape;
      ANALYZE {TABLE};
      """);
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }

    GC.SuppressFinalize(this);
  }

  private static async Task _executeAsync(string connectionString, string sql) {
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<(string Json, string Plan)> _filterAsync(string where) {
    var capture = new PlanCapture();
    var options = new DbContextOptionsBuilder<IdFilterIndexDbContext>(_options).AddInterceptors(capture).Options;
    var services = new ServiceCollection();
    services.AddScoped(_ => new IdFilterIndexDbContext(options));
    services.AddGraphQLServer().AddWhizbangLenses().AddQueryType<GraphQuery>();
    await using var provider = services.BuildServiceProvider();
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync($"{{ rows(where: {{ data: {{ orderId: {where} }} }}) {{ data {{ status }} }} }}");

    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("\"errors\"").Because(json);
    return (json, capture.Plan);
  }

  [Test]
  public async Task Eq_IsAnsweredByTheDocumentIndexAsync() {
    var (json, plan) = await _filterAsync($"{{ eq: \"{_second.Value}\" }}");

    await Assert.That(json).Contains("\"second\"").Because(json);
    await Assert.That(json).DoesNotContain("\"first\"").Because(json);
    await Assert.That(plan).Contains(_documentIndex.Name).Because(plan);
  }

  [Test]
  public async Task In_IsAnsweredByTheDocumentIndexAsync() {
    var (json, plan) = await _filterAsync($"{{ in: [\"{_first.Value}\", \"{_third.Value}\"] }}");

    await Assert.That(json).Contains("\"first\"").Because(json);
    await Assert.That(json).DoesNotContain("\"second\"").Because(json);
    await Assert.That(json).Contains("\"third\"").Because(json);
    await Assert.That(plan).Contains(_documentIndex.Name).Because(plan);
  }
}
