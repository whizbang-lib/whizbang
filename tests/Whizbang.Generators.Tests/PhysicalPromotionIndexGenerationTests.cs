extern alias shared;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using JsonIndexCast = shared::Whizbang.Generators.Shared.Models.JsonIndexCast;
using JsonIndexInfo = shared::Whizbang.Generators.Shared.Models.JsonIndexInfo;
using JsonIndexSql = shared::Whizbang.Generators.Shared.Models.JsonIndexSql;
using PhysicalColumnSql = shared::Whizbang.Generators.Shared.Models.PhysicalColumnSql;
using PhysicalFieldInfo = shared::Whizbang.Generators.Shared.Models.PhysicalFieldInfo;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #1009: the schema for a promoted field drops the document indexes it built for the field, builds
/// indexes of the same kind over the column, and arms the column for the rows a rolling deploy writes.
/// </summary>
/// <remarks>
/// That the drops leave an operator's index alone, that the column indexes are used, and that the armed
/// column is filled are proven against a real database in PhysicalFieldPromotionTests and
/// PhysicalColumnFillMaintenanceStepTests; these lock what the generator emits and in what order.
/// </remarks>
public class PhysicalPromotionIndexGenerationTests {

  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    public record ItemModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      [Indexed]
      [Indexed(IndexKinds.Search)]
      public string? Name { get; init; }

      [PhysicalField]
      [Indexed(IndexKinds.Substring, caseInsensitive: true)]
      [Indexed(IndexKinds.Ordered, caseInsensitive: true)]
      public string? Code { get; init; }

      [PhysicalField]
      public int Rank { get; init; }

      [Indexed]
      public string Status { get; init; } = "";
    }

    public class ItemPerspective : IPerspectiveFor<ItemModel, TestEvent> {
      public ItemModel Apply(ItemModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _schemaAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var source = result.GeneratedSources.First(s => s.HintName.EndsWith("_SchemaExtensions.g.cs", StringComparison.Ordinal));
    return source.SourceText.ToString();
  }

  [Test]
  public async Task EachDocumentIndexOfAPromotedField_IsDroppedByTheNameTheSchemaGaveItAsync() {
    var schema = await _schemaAsync();

    await Assert.That(schema).Contains("wh_drop_document_index('\"\"testapp\"\".wh_per_item', 'idx_item_name_json', 'Name');");
    await Assert.That(schema).Contains("wh_drop_document_index('\"\"testapp\"\".wh_per_item', 'idx_item_name_fold_trgm', 'Name');");
    await Assert.That(schema).Contains("wh_drop_document_index('\"\"testapp\"\".wh_per_item', 'idx_item_code_ci_trgm', 'Code');");
    await Assert.That(schema).Contains("wh_drop_document_index('\"\"testapp\"\".wh_per_item', 'idx_item_code_ci_json', 'Code');");
  }

  [Test]
  public async Task AFieldThatDeclaresNoIndex_AndADocumentField_DropNothingAsync() {
    var schema = await _schemaAsync();

    await Assert.That(schema).DoesNotContain("'Rank');", StringComparison.Ordinal);
    await Assert.That(schema).DoesNotContain("'Status');", StringComparison.Ordinal)
      .Because("a field still in the document keeps its document index");
  }

  [Test]
  public async Task APromotedField_GetsAColumnIndexOfEachKindItDeclaresAsync() {
    var schema = await _schemaAsync();

    await Assert.That(schema).Contains("idx_item_name ON \"\"testapp\"\".wh_per_item (name)");
    await Assert.That(schema).Contains("idx_item_name_fold_trgm ON \"\"testapp\"\".wh_per_item USING gin (\"\"testapp\"\".wh_fold(name) gin_trgm_ops)");
    await Assert.That(schema).Contains("idx_item_code_ci_trgm ON \"\"testapp\"\".wh_per_item USING gin ((lower(code)) gin_trgm_ops)");
    await Assert.That(schema).Contains("idx_item_code_ci ON \"\"testapp\"\".wh_per_item ((lower(code)))");
  }

  [Test]
  public async Task TheDrops_ComeBeforeTheColumnIndexesThatMayShareTheirNamesAsync() {
    var schema = await _schemaAsync();

    var drop = schema.IndexOf("'idx_item_name_fold_trgm', 'Name')", StringComparison.Ordinal);
    var create = schema.IndexOf("CREATE INDEX IF NOT EXISTS idx_item_name_fold_trgm", StringComparison.Ordinal);
    await Assert.That(drop).IsGreaterThan(-1);
    await Assert.That(create).IsGreaterThan(drop)
      .Because("IF NOT EXISTS would otherwise find the document index under the name and build nothing");
  }

  [Test]
  public async Task TheColumnBtree_IsEmittedOnceAsync() {
    var schema = await _schemaAsync();
    var entry = schema[schema.IndexOf("-- testapp.wh_per_item", StringComparison.Ordinal)..];
    entry = entry[..entry.IndexOf("-- testapp.wh_per_item", 1, StringComparison.Ordinal)];

    await Assert.That(entry.Split("idx_item_name ON").Length - 1).IsEqualTo(1);
  }

  [Test]
  public async Task EachBackfillableColumn_IsArmedBeforeItIsAddedAsync() {
    var schema = await _schemaAsync();

    var arm = schema.IndexOf("INSERT INTO \"\"testapp\"\".wh_physical_column_fills (table_name, column_name, json_key, extraction) SELECT format('%I.%I', n.nspname, c.relname), 'rank', 'Rank', $wbfill$(data ->> 'Rank')::integer$wbfill$", StringComparison.Ordinal);
    var add = schema.IndexOf("ADD COLUMN IF NOT EXISTS rank INTEGER", StringComparison.Ordinal);
    await Assert.That(arm).IsGreaterThan(-1);
    await Assert.That(add).IsGreaterThan(arm)
      .Because("only the pass that adds the column arms it, which it can tell only before adding it");
  }

  [Test]
  public async Task IndexNames_IncludeTheNameOfAnIndexOverASupersededCastAsync() {
    var index = new JsonIndexInfo("Day", "Day", JsonIndexCast.Int8, Ordered: true, Substring: false,
      CaseInsensitive: false, Superseded: JsonIndexCast.Int4);

    var names = JsonIndexSql.IndexNames(index, "item").ToList();

    await Assert.That(names).Contains("idx_item_day_json");
    await Assert.That(names).Contains("idx_item_day_bigint_json");
  }

  [Test]
  public async Task DropStatements_NameEachIndexOnceAsync() {
    var index = new JsonIndexInfo("Day", "Day", JsonIndexCast.Int8, Ordered: true, Substring: false,
      CaseInsensitive: false, Superseded: JsonIndexCast.Int4);

    var drops = JsonIndexSql.DropDocumentIndexStatements(index, "\"s\".wh_per_item", "item").ToList();

    await Assert.That(drops).IsEquivalentTo([
      "SELECT \"s\".wh_drop_document_index('\"s\".wh_per_item', 'idx_item_day_json', 'Day');",
      "SELECT \"s\".wh_drop_document_index('\"s\".wh_per_item', 'idx_item_day_bigint_json', 'Day');",
    ]);
  }

  [Test]
  public async Task NothingIsArmed_ForAColumnThatCannotBeFilledFromTheDocumentAsync() {
    var split = new PhysicalFieldInfo("Name", "name", "global::System.String", IsIndexed: false, IsUnique: false,
      MaxLength: null, IsVector: false, VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null,
      VectorIndexLists: null, IsSplit: true);

    await Assert.That(PhysicalColumnSql.Arm("\"s\".wh_per_item", split)).IsNull()
      .Because("a split field has no copy in the document to fill from");
  }
}
