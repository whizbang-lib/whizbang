using System.Linq.Expressions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;
using Cell = Whizbang.Data.EFCore.Postgres.Tests.Collective.CollectiveDispatcherEFCoreIntegrationTests.Cell;
using CellsModel = Whizbang.Data.EFCore.Postgres.Tests.Collective.CollectiveDispatcherEFCoreIntegrationTests.CellsModel;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// The upsert expression both Postgres adapters share, and how the EF Core rewriter hands an
/// <c>UpsertElement</c> to it. The behavior is proven against a real database in the dispatcher
/// integration tests; these lock the shape and the refusals.
/// </summary>
[Category("Shard2")]
public class CollectiveElementUpsertSqlTests {

  [Test]
  public async Task ValueSql_ReplacesByKeyInOrder_AppendsOtherwise_AndTreatsANonArrayAsEmptyAsync() {
    var sql = CollectiveElementUpsertSql.ValueSql("Cells", "FieldId", "@p0::jsonb");

    await Assert.That(sql).Contains("jsonb_typeof(wh_s.a) = 'array'");
    await Assert.That(sql).Contains("wh_e.v->'FieldId' = (@p0::jsonb)->'FieldId'");
    await Assert.That(sql).Contains("WITH ORDINALITY AS wh_e(v, i)");
    await Assert.That(sql).Contains("ORDER BY wh_e.i");
    await Assert.That(sql).Contains("wh_s.a || jsonb_build_array(@p0::jsonb)");
    await Assert.That(sql).Contains("ELSE jsonb_build_array(@p0::jsonb) END");
    await Assert.That(sql).EndsWith("FROM (SELECT (data->'Cells') AS a) AS wh_s)")
      .Because("with no earlier setter on the property, the upsert reads the row's stored array, once");
  }

  [Test]
  public async Task ValueSql_FromAnEarlierValue_ReadsItOnce_InsteadOfTheStoredArrayAsync() {
    var first = CollectiveElementUpsertSql.ValueSql("Cells", "FieldId", "@p0::jsonb");

    var second = CollectiveElementUpsertSql.ValueSql("Cells", "FieldId", "@p1::jsonb", source: first);

    await Assert.That(second).EndsWith($"FROM (SELECT ({first}) AS a) AS wh_s)");
    await Assert.That(second.Split(first).Length - 1).IsEqualTo(1)
      .Because("the earlier value is read once, so a chain of upserts grows linearly, not by repeating it");
  }

  [Test]
  [Arguments("Cells'; DROP TABLE x; --", "FieldId")]
  [Arguments("Cells", "Field Id")]
  [Arguments("", "FieldId")]
  public async Task ValueSql_RefusesAnythingButAPlainIdentifierAsync(string array, string key) {
    await Assert.That(() => CollectiveElementUpsertSql.ValueSql(array, key, "@p0::jsonb")).Throws<ArgumentException>();
  }

  [Test]
  public async Task ValueSql_RefusesABlankElementAsync() {
    await Assert.That(() => CollectiveElementUpsertSql.ValueSql("Cells", "FieldId", " ")).Throws<ArgumentException>();
  }

  [Test]
  public async Task Rewriter_UpsertElement_CarriesTheArray_TheKey_AndTheSerializedElementAsync() {
    var cell = new Cell { Key = "k1", Value = "v1" };
    Expression<Action<ICollectiveSetters<CellsModel>>> setters = s => s.UpsertElement(m => m.Cells, c => c.Key, cell);

    var assignment = CollectiveSettersRewriter.CollectAssignments(setters).Single();

    await Assert.That(assignment.PathName).IsEqualTo("Cells");
    await Assert.That(assignment.ElementKey).IsEqualTo("Key");
    await Assert.That(assignment.JsonValue).IsEqualTo("""{"Key":"k1","Value":"v1"}""")
      .Because("the element is serialized once, exactly as the writer stores it");
    await Assert.That(assignment.IsNull).IsFalse();
  }

  [Test]
  public async Task Rewriter_UpsertElement_ANestedKey_IsRefusedAsync() {
    var cell = new Cell { Key = "k1", Value = "v1" };
    Expression<Action<ICollectiveSetters<CellsModel>>> setters = s => s.UpsertElement(m => m.Cells, c => c.Key.Length, cell);

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(setters)).Throws<NotSupportedException>();
  }

  [Test]
  public async Task Rewriter_UpsertElement_ANullElement_IsRefusedAsync() {
    Cell? cell = null;
    Expression<Action<ICollectiveSetters<CellsModel>>> setters = s => s.UpsertElement(m => m.Cells, c => c.Key, cell!);

    await Assert.That(() => CollectiveSettersRewriter.CollectAssignments(setters)).Throws<ArgumentException>();
  }
}
