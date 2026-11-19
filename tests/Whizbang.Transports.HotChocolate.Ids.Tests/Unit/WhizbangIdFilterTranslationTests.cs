// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

/// <summary>A table whose id column is the id, mapped through a value converter.</summary>
[SuppressIndexAdvisory("A query-translation fixture: no table is created.")]
public sealed class OrderEntity {
  public GraphQLOrderId Id { get; init; }
  public string Name { get; init; } = "";
}

/// <summary>
/// The two ways an id reaches PostgreSQL through Entity Framework Core: inside a perspective row's document, mapped
/// the way the generated perspective configuration maps it (<c>ComplexProperty().ToJson()</c>), and as a column with
/// a value converter.
/// </summary>
public sealed class IdFilterDbContext(DbContextOptions<IdFilterDbContext> options) : DbContext(options) {
  public DbSet<PerspectiveRow<OrderDocument>> OrderRows => Set<PerspectiveRow<OrderDocument>>();
  public DbSet<OrderEntity> Orders => Set<OrderEntity>();

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<PerspectiveRow<OrderDocument>>(entity => {
      entity.ToTable("wh_per_order_document");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
      entity.ComplexProperty(e => e.Scope, s => {
        s.ToJson("scope");
        s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
      });
      entity.Property(e => e.CreatedAt).HasColumnName("created_at");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
      entity.Property(e => e.Version).HasColumnName("version");
    });

    modelBuilder.Entity<OrderEntity>(entity => {
      entity.ToTable("orders");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id").HasConversion(id => id.Value, guid => GraphQLOrderId.From(guid));
      entity.Property(e => e.Name).HasColumnName("name");
    });
  }
}

/// <summary>Holds the SQL a captured query would run.</summary>
public sealed class CapturedSql {
  public string Text { get; set; } = "";
}

/// <summary>
/// Runs after filtering: records the SQL of the filtered query instead of running it, so no database is needed,
/// and returns no rows. Entity Framework Core throws while building the SQL for a filter it cannot translate.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
public sealed class CaptureSqlAttribute : ObjectFieldDescriptorAttribute {
  /// <summary>Takes its place among the field's middleware from its line, as HotChocolate's own attributes do.</summary>
  public CaptureSqlAttribute([System.Runtime.CompilerServices.CallerLineNumber] int order = 0) {
    Order = order;
  }

  protected override void OnConfigure(IDescriptorContext context, IObjectFieldDescriptor descriptor, System.Reflection.MemberInfo? member) =>
    descriptor.Use(next => async resolverContext => {
      await next(resolverContext);
      resolverContext.Service<CapturedSql>().Text = ((IQueryable)resolverContext.Result!).ToQueryString();
      resolverContext.Result = Array.Empty<object>();
    });
}

#pragma warning disable S2325, CA1822 // HotChocolate binds the instance methods of a query type as its resolvers
/// <summary>Filterable queries over the Entity Framework Core sets.</summary>
public class IdFilterQuery {
  [CaptureSql]
  [UseFiltering]
  public IQueryable<PerspectiveRow<OrderDocument>> OrderRows([Service] IdFilterDbContext db) => db.OrderRows;

  [CaptureSql]
  [UseFiltering]
  public IQueryable<OrderEntity> Orders([Service] IdFilterDbContext db) => db.Orders;
}
#pragma warning restore S2325, CA1822

/// <summary>
/// A strong-id filter translates to SQL for PostgreSQL through Entity Framework Core, both for an id in a perspective
/// row's document and for an id column with a value converter (#2073). In a document it compares the Guid inside the
/// id, the shape the containment rewrite turns into an indexed <c>data @&gt; ...</c>; on a column it compares the id
/// with an id, so the provider maps the operand through the column's converter. Entity Framework Core refuses to
/// evaluate a filter in memory, so a filter it cannot translate throws here instead of passing.
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdFilterTranslationTests {
  private static readonly GraphQLOrderId _first = GraphQLOrderId.New();
  private static readonly GraphQLOrderId _second = GraphQLOrderId.New();

  private static async Task<string> _sqlAsync(string document) {
    var services = new ServiceCollection()
      .AddSingleton<CapturedSql>()
      .AddDbContext<IdFilterDbContext>(options => options.UseNpgsql("Host=localhost;Database=unused"));
    services
      .AddGraphQL()
      .AddQueryType<IdFilterQuery>()
      .AddWhizbangLenses();
    await using var provider = services.BuildServiceProvider();
    var executor = await provider.GetRequestExecutorAsync();

    var result = (IOperationResult)await executor.ExecuteAsync(document);

    await Assert.That(result.Errors).IsNull();
    return provider.GetRequiredService<CapturedSql>().Text;
  }

  [Test]
  public async Task LensRow_Eq_ComparesTheGuidInsideTheIdInTheDocumentInSqlAsync() {
    var sql = await _sqlAsync($"{{ orderRows(where: {{ data: {{ id: {{ eq: \"{_first.Value}\" }} }} }}) {{ version }} }}");

    await Assert.That(sql).Contains("WHERE (CAST(w.data #>> '{Id,Value}' AS uuid)) = @p");
    await Assert.That(sql).Contains($"@p='{_first.Value}'");
  }

  [Test]
  public async Task LensRow_Neq_ComparesTheGuidInsideTheIdInTheDocumentInSqlAsync() {
    var sql = await _sqlAsync($"{{ orderRows(where: {{ data: {{ id: {{ neq: \"{_first.Value}\" }} }} }}) {{ version }} }}");

    await Assert.That(sql).Contains("WHERE (CAST(w.data #>> '{Id,Value}' AS uuid)) <> @p");
  }

  [Test]
  public async Task LensRow_InAndNin_CompareEachIdInSqlAsync() {
    var ids = $"[\"{_first.Value}\", \"{_second.Value}\"]";

    var inSql = await _sqlAsync($"{{ orderRows(where: {{ data: {{ id: {{ in: {ids} }} }} }}) {{ version }} }}");
    var ninSql = await _sqlAsync($"{{ orderRows(where: {{ data: {{ id: {{ nin: {ids} }} }} }}) {{ version }} }}");

    await Assert.That(inSql).Contains($"@p='{_first.Value}'");
    await Assert.That(inSql).Contains($"@p0='{_second.Value}'");
    await Assert.That(inSql).Contains("WHERE (CAST(w.data #>> '{Id,Value}' AS uuid)) = @p OR (CAST(w.data #>> '{Id,Value}' AS uuid)) = @p0");
    await Assert.That(ninSql).Contains("WHERE (CAST(w.data #>> '{Id,Value}' AS uuid)) <> @p AND (CAST(w.data #>> '{Id,Value}' AS uuid)) <> @p0");
  }

  [Test]
  public async Task Column_EqAndIn_GoThroughTheValueConverterAsync() {
    var eqSql = await _sqlAsync($"{{ orders(where: {{ id: {{ eq: \"{_first.Value}\" }} }}) {{ name }} }}");
    var inSql = await _sqlAsync($"{{ orders(where: {{ id: {{ in: [\"{_first.Value}\", \"{_second.Value}\"] }} }}) {{ name }} }}");

    await Assert.That(eqSql).Contains("WHERE o.id = @");
    await Assert.That(eqSql).Contains($"='{_first.Value}'");
    await Assert.That(inSql).Contains($"='{_second.Value}'");
  }

  [Test]
  public async Task InAndNin_WithThousandsOfIds_TranslateForADocumentAndAColumnAsync() {
    // One comparison per id; joined as a chain nested one level per id, a list this long overflowed the stack of
    // Entity Framework Core's expression visitors and ended the process.
    var ids = $"[{string.Join(", ", Enumerable.Range(0, 5_000).Select(_ => $"\"{GraphQLOrderId.New().Value}\""))}]";

    var rowSql = await _sqlAsync($"{{ orderRows(where: {{ data: {{ id: {{ in: {ids} }} }} }}) {{ version }} }}");
    var ninRowSql = await _sqlAsync($"{{ orderRows(where: {{ data: {{ id: {{ nin: {ids} }} }} }}) {{ version }} }}");
    var columnSql = await _sqlAsync($"{{ orders(where: {{ id: {{ in: {ids} }} }}) {{ name }} }}");

    await Assert.That(rowSql).Contains(" OR ");
    await Assert.That(ninRowSql).Contains(" AND ");
    await Assert.That(columnSql).Contains("WHERE o.id");
  }
}
