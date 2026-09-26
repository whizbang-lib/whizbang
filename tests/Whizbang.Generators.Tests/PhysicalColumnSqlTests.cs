extern alias shared;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using PhysicalColumnSql = shared::Whizbang.Generators.Shared.Models.PhysicalColumnSql;
using PhysicalFieldInfo = shared::Whizbang.Generators.Shared.Models.PhysicalFieldInfo;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A physical field added to a model whose table already exists reaches that table: the generated schema
/// adds the column, fills it from the document, and only then builds its index.
/// </summary>
/// <remarks>
/// The table is created with <c>CREATE TABLE IF NOT EXISTS</c>, which skips a table that exists. Before
/// this, a new physical column was never added there; an index on it then failed the schema pass, and
/// without an index the query translator read the missing or empty column for every older row.
/// Whether the backfill reproduces each stored value exactly is proven against a real database in
/// PhysicalColumnBackfillIntegrationTests; these lock what the generator emits and in what order.
/// </remarks>
public class PhysicalColumnSqlTests {

  private static string _source(string storage) => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    {{storage}}
    public record OrderModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      [Indexed]
      public DateTimeOffset PlacedAt { get; init; }

      [PhysicalField(ColumnType = "citext")]
      public string? Code { get; init; }
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, TestEvent> {
      public OrderModel Apply(OrderModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _schemaAsync(string storage) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(storage));
    var schema = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal));
    return schema.SourceText.ToString();
  }

  [Test]
  public async Task ExistingTable_GetsTheColumn_ThenTheBackfill_ThenTheIndexAsync() {
    var sql = await _schemaAsync("");

    var add = sql.IndexOf("ADD COLUMN IF NOT EXISTS placed_at TIMESTAMPTZ;", StringComparison.Ordinal);
    var backfill = sql.IndexOf("SET placed_at = (TIMESTAMPTZ 'epoch'", StringComparison.Ordinal);
    var index = sql.IndexOf("_placed_at ON ", StringComparison.Ordinal);

    await Assert.That(add).IsGreaterThan(-1).Because("a table that predates the field needs the column added");
    await Assert.That(backfill).IsGreaterThan(add).Because("rows written before the column existed are filled from the document");
    await Assert.That(index).IsGreaterThan(backfill).Because("the index is built over the column only once it exists and is filled");
  }

  [Test]
  public async Task AColumnTypeTheAuthorChose_IsAddedButNotBackfilledAsync() {
    var sql = await _schemaAsync("");

    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS code citext;");
    await Assert.That(sql).DoesNotContain("SET code =")
      .Because("the column's encoding of an author-chosen type is not something the backfill can know");
  }

  [Test]
  public async Task SplitStorage_AddsTheColumn_ButHasNoDocumentCopyToBackfillFromAsync() {
    var sql = await _schemaAsync("[PerspectiveStorage(FieldStorageMode.Split)]");

    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS placed_at TIMESTAMPTZ;");
    await Assert.That(sql).DoesNotContain("SET placed_at =")
      .Because("a Split field lives only in the column; the document has no copy to fill it from");
  }

  [Test]
  [Arguments("global::System.Guid", "::uuid")]
  [Arguments("global::System.Int16", "::smallint")]
  [Arguments("global::System.DateOnly", "::date")]
  [Arguments("global::System.TimeOnly", "(TIME '00:00' + ")]
  [Arguments("string", "(data ->> 'P')")]
  public async Task Extraction_CoversEachKnownTypeAsync(string typeName, string expected) {
    await Assert.That(PhysicalColumnSql.Extraction(_field(typeName))).Contains(expected);
  }

  [Test]
  public async Task Extraction_AnUnknownType_OrAVector_IsNotBackfilledAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(_field("global::TestApp.Status"))).IsNull();
    await Assert.That(PhysicalColumnSql.Extraction(_field("global::System.Single[]") with { IsVector = true })).IsNull();
    await Assert.That(PhysicalColumnSql.Backfill("t", _field("global::TestApp.Status"))).IsNull();
  }

  private static PhysicalFieldInfo _field(string typeName) =>
    new("P", "p", typeName, IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);
}
