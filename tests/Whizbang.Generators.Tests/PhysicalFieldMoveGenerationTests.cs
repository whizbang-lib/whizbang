extern alias shared;
using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using DocumentPropertyDiscovery = shared::Whizbang.Generators.Shared.Models.DocumentPropertyDiscovery;
using PhysicalColumnSql = shared::Whizbang.Generators.Shared.Models.PhysicalColumnSql;
using PhysicalFieldInfo = shared::Whizbang.Generators.Shared.Models.PhysicalFieldInfo;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issues #1021, #1022 and #1010: the schema both drivers generate moves a field's storage without losing its
/// data. A promoted column of any kind the document can fill is armed, added and filled; a Split model's moves
/// sync writes; and the fields the model keeps only in the document are offered for demotion.
/// </summary>
/// <remarks>
/// That the statements move every value exactly, during a deploy and after it, is proven against a real
/// database in SplitPromotionTests, PhysicalFieldDemotionTests and DapperPhysicalFieldMoveTests; these lock
/// what the generators emit and in what order.
/// </remarks>
public class PhysicalFieldMoveGenerationTests {

  private static PhysicalFieldInfo _field(string property, string typeName, string? columnType = null, bool split = false,
      string? enumScalar = null) =>
    new(property, _pascalToSnake(property), typeName, IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null,
      ColumnType: columnType, IsSplit: split, EnumScalarType: enumScalar);

  private static string _pascalToSnake(string name) =>
    string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

  // ── Extraction: every kind the document can fill ─────────────────────────────────────────────────

  [Test]
  public async Task AnEnumeration_IsReadAsTheNumberItsColumnAndDocumentBothHoldAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(_field("Kind", "global::TestApp.Kind", enumScalar: "System.Int32")))
      .IsEqualTo("(data ->> 'Kind')::integer");
    await Assert.That(PhysicalColumnSql.Extraction(_field("Kind", "global::TestApp.Kind?", enumScalar: "System.Int64")))
      .IsEqualTo("(data ->> 'Kind')::bigint");
  }

  [Test]
  public async Task AJsonbColumn_TakesTheDocumentValueAsItIsAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(_field("Tags", "global::System.Collections.Generic.List<global::TestApp.Tag>", "jsonb")))
      .IsEqualTo("NULLIF(data -> 'Tags', 'null'::jsonb)");
    await Assert.That(PhysicalColumnSql.Extraction(_field("Tags", "global::TestApp.Tags", "JSON")))
      .IsEqualTo("NULLIF(data -> 'Tags', 'null'::jsonb)::json");
  }

  [Test]
  [Arguments("global::System.Guid[]", "uuid[]", "wh_e.v::uuid")]
  [Arguments("global::System.Collections.Generic.List<global::System.Guid>", "uuid[]", "wh_e.v::uuid")]
  [Arguments("global::System.Collections.Generic.IReadOnlyList<string>?", "text[]", "SELECT wh_e.v FROM")]
  [Arguments("global::System.Collections.Generic.List<global::System.DateTimeOffset>", "timestamptz[]", "(TIMESTAMPTZ 'epoch' + wh_e.v::bigint")]
  public async Task AnArray_IsBuiltElementByElementInDocumentOrderAsync(string typeName, string columnType, string element) {
    var extraction = PhysicalColumnSql.Extraction(_field("Ids", typeName, columnType));

    await Assert.That(extraction).StartsWith("CASE WHEN jsonb_typeof(data -> 'Ids') <> 'null' THEN ARRAY(SELECT ")
      .Because("a JSON null is a null array, and not an error");
    await Assert.That(extraction).Contains(element);
    await Assert.That(extraction).EndsWith($"FROM jsonb_array_elements_text(data -> 'Ids') WITH ORDINALITY AS wh_e(v, n) ORDER BY wh_e.n)::{columnType} END");
  }

  [Test]
  [Arguments("global::System.Collections.Generic.List<global::TestApp.Tag>", "jsonb[]")]
  [Arguments("global::System.Collections.Generic.Dictionary<string, int>", "text[]")]
  [Arguments("global::TestApp.Tags", "text[]")]
  public async Task AnArrayOfWhatTheDocumentCannotRead_IsNotFilledAsync(string typeName, string columnType) {
    await Assert.That(PhysicalColumnSql.Extraction(_field("Ids", typeName, columnType))).IsNull();
  }

  [Test]
  public async Task AScalarUnderATypeTheAuthorChose_IsCastToThatTypeAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(_field("Code", "string", "citext")))
      .IsEqualTo("((data ->> 'Code'))::citext");
    await Assert.That(PhysicalColumnSql.Extraction(_field("Amount", "global::System.Decimal", "numeric(12,2)")))
      .IsEqualTo("((data ->> 'Amount')::numeric)::numeric(12,2)");
  }

  [Test]
  public async Task ADateOrUnknownTypeUnderATypeTheAuthorChose_IsNotFilledAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(_field("At", "global::System.DateTimeOffset", "timestamp"))).IsNull()
      .Because("the cast of an instant to a type of the author's choosing depends on the session, not the value");
    await Assert.That(PhysicalColumnSql.Extraction(_field("Shape", "global::TestApp.Shape", "point"))).IsNull();
  }

  [Test]
  public async Task ASplitField_IsFilledFromTheDocumentThePreviousReleaseWroteAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(_field("Name", "string", split: true))).IsEqualTo("(data ->> 'Name')");
  }

  [Test]
  public async Task NoField_HasNoExtractionAsync() {
    await Assert.That(PhysicalColumnSql.Extraction(null!)).IsNull();
  }

  // ── Arm, sync, demote ────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ASplitField_IsArmedWithItsWritesSyncedAsync() {
    await Assert.That(PhysicalColumnSql.Arm("\"s\".wh_per_item", _field("Name", "string", split: true)))
      .IsEqualTo("SELECT \"s\".wh_arm_physical_column('\"s\".wh_per_item', 'name', 'Name', $wbfill$(data ->> 'Name')$wbfill$, true);");
  }

  [Test]
  public async Task AnUnqualifiedTable_CallsTheFunctionsOnTheSearchPathAsync() {
    await Assert.That(PhysicalColumnSql.Arm("wh_per_item", _field("Rank", "int")))
      .IsEqualTo("SELECT wh_arm_physical_column('wh_per_item', 'rank', 'Rank', $wbfill$(data ->> 'Rank')::integer$wbfill$, false);");
    await Assert.That(PhysicalColumnSql.SyncMoves("wh_per_item")).IsEqualTo("SELECT wh_sync_physical_moves('wh_per_item');");
  }

  [Test]
  public async Task Demote_OffersEachDocumentFieldUnderItsDefaultColumnNameAsync() {
    var sql = PhysicalColumnSql.Demote("\"s\".wh_per_item", ["Score", "Tags", "LastSeenAt"], [_field("Name", "string")]);

    await Assert.That(sql).IsEqualTo(
      "SELECT * FROM \"s\".wh_demote_physical_columns('\"s\".wh_per_item', "
      + "'{\"last_seen_at\": \"LastSeenAt\", \"score\": \"Score\", \"tags\": \"Tags\"}'::jsonb, ARRAY['name']::text[]);")
      .Because("the columns the promoted fields own now are passed, so a recorded column one of them took over is never demoted");
  }

  [Test]
  public async Task Demote_NeverOffersAFrameworkColumnOrOneAPromotedFieldOwnsAsync() {
    var owned = _field("Rank", "int") with { ColumnName = "Score" };

    var candidates = PhysicalColumnSql.DemotionCandidates(
      ["Id", "Data", "Version", "CreatedAt", "ExpiresAt", "Score", "Note", "Note", "bad-name", ""], [owned]);

    await Assert.That(candidates.Select(c => c.Column)).IsEquivalentTo(["note"])
      .Because("a column every table has, one a promoted field still owns, a repeat and a name that is not an identifier are not candidates");
  }

  [Test]
  public async Task Demote_WithNoCandidate_EmitsNothingAsync() {
    await Assert.That(PhysicalColumnSql.Demote("t", ["Id", "Version"], [])).IsNull();
    await Assert.That(PhysicalColumnSql.Demote("t", ["Score"], null!)).EndsWith("'::jsonb, ARRAY[]::text[]);")
      .Because("a model with nothing promoted owns no column");
    await Assert.That(PhysicalColumnSql.DemotionCandidates(null!, null!)).IsEmpty();
  }

  // ── Discovery of the fields kept only in the document ────────────────────────────────────────────

  [Test]
  public async Task DocumentProperties_AreTheReadablePublicPropertiesNotPromotedAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      using System;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class BaseModel {
        public string Inherited { get; set; } = "";
        public virtual string Shadowed { get; set; } = "";
      }

      public class Model : BaseModel {
        public Guid Id { get; set; }
        [PhysicalField] public int Rank { get; set; }
        [VectorField(3)] public float[]? Embedding { get; set; }
        public int Score { get; set; }
        public override string Shadowed { get; set; } = "";
        public static int Static { get; set; }
        internal int Hidden { get; set; }
        public int WriteOnly { set { } }
        public int this[int i] => i;
      }
      """);

    var names = DocumentPropertyDiscovery.From(compilation.GetTypeByMetadataName("TestApp.Model"));

    await Assert.That(names).IsEquivalentTo(["Id", "Score", "Shadowed", "Inherited"]);
    await Assert.That(DocumentPropertyDiscovery.From(null)).IsEmpty();
  }

  // ── What each generator emits, and in what order ─────────────────────────────────────────────────

  private const string MODELS = """
    using System;
    using System.Collections.Generic;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    public enum Lane { Backlog = 0, Doing = 1 }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record TicketModel {
      [StreamId] public Guid Id { get; init; }
      [PhysicalField] public Lane Lane { get; init; }
      [PhysicalField(ColumnType = "uuid[]")] public Guid[] Watchers { get; init; } = [];
      public int Score { get; init; }
    }

    public class TicketPerspective : IPerspectiveFor<TicketModel, TestEvent> {
      public TicketModel Apply(TicketModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  public async Task EFCore_ArmsAndAddsEveryColumn_ThenSyncs_ThenFills_ThenDemotesAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODELS);
    var sql = result.GeneratedSources.First(s => s.HintName.EndsWith("_SchemaExtensions.g.cs", StringComparison.Ordinal))
      .SourceText.ToString();

    var armLane = sql.IndexOf("wh_arm_physical_column('\"\"testapp\"\".wh_per_ticket', 'lane', 'Lane', $wbfill$(data ->> 'Lane')::integer$wbfill$, true);", StringComparison.Ordinal);
    // The schema is emitted more than once (the fallback script and the per-perspective entries); each search
    // continues from the one before it, so the order is the order within one emission. The SQL is run through
    // ExecuteSqlRaw, so a literal brace is doubled.
    var addWatchers = sql.IndexOf("wh_per_ticket ADD COLUMN IF NOT EXISTS watchers uuid[];", Math.Max(armLane, 0), StringComparison.Ordinal);
    var sync = sql.IndexOf("SELECT \"\"testapp\"\".wh_sync_physical_moves('\"\"testapp\"\".wh_per_ticket');", Math.Max(addWatchers, 0), StringComparison.Ordinal);
    var fill = sql.IndexOf("SET watchers = CASE WHEN jsonb_typeof(data -> 'Watchers') <> 'null' THEN ARRAY(SELECT wh_e.v::uuid", Math.Max(sync, 0), StringComparison.Ordinal);
    var demote = sql.IndexOf("wh_demote_physical_columns('\"\"testapp\"\".wh_per_ticket', '{{\"\"score\"\": \"\"Score\"\"}}'::jsonb, ARRAY['lane', 'watchers']::text[]);", Math.Max(fill, 0), StringComparison.Ordinal);

    await Assert.That(armLane).IsGreaterThan(-1);
    await Assert.That(addWatchers).IsGreaterThan(armLane);
    await Assert.That(sync).IsGreaterThan(addWatchers)
      .Because("the triggers go on once every column exists and before any is filled");
    await Assert.That(fill).IsGreaterThan(sync);
    await Assert.That(demote).IsGreaterThan(fill);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Dapper_ArmsAndAddsBeforeTheTable_ThenSyncsFillsAndDemotesAfterItAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODELS);
    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs")!;
    var entry = sql[sql.IndexOf("Entries", StringComparison.Ordinal)..];

    var arm = entry.IndexOf("SELECT wh_arm_physical_column('wh_per_ticket_perspective', 'lane', 'Lane', $wbfill$(data ->> 'Lane')::integer$wbfill$, true);", StringComparison.Ordinal);
    var add = entry.IndexOf("ALTER TABLE IF EXISTS wh_per_ticket_perspective ADD COLUMN IF NOT EXISTS watchers uuid[];", StringComparison.Ordinal);
    var create = entry.IndexOf("CREATE TABLE IF NOT EXISTS wh_per_ticket_perspective", StringComparison.Ordinal);
    var sync = entry.IndexOf("SELECT wh_sync_physical_moves('wh_per_ticket_perspective');", StringComparison.Ordinal);
    var fill = entry.IndexOf("UPDATE wh_per_ticket_perspective SET lane = (data ->> 'Lane')::integer", StringComparison.Ordinal);
    var demote = entry.IndexOf("SELECT * FROM wh_demote_physical_columns('wh_per_ticket_perspective', '{\"\"score\"\": \"\"Score\"\"}'::jsonb, ARRAY['lane', 'watchers']::text[]);", StringComparison.Ordinal);

    await Assert.That(arm).IsGreaterThan(-1);
    await Assert.That(add).IsGreaterThan(arm);
    await Assert.That(create).IsGreaterThan(add)
      .Because("a column-copy swap creates the column on the new table, so only the table the previous release left can show it missing");
    await Assert.That(sync).IsGreaterThan(create);
    await Assert.That(fill).IsGreaterThan(sync);
    await Assert.That(demote).IsGreaterThan(fill);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Dapper_AnExtractedModel_SyncsNoWritesAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODELS.Replace(
      "[PerspectiveStorage(FieldStorageMode.Split)]", "[PerspectiveStorage(FieldStorageMode.Extracted)]", StringComparison.Ordinal));
    var sql = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs")!;

    await Assert.That(sql).Contains("wh_arm_physical_column('wh_per_ticket_perspective', 'lane', 'Lane', $wbfill$(data ->> 'Lane')::integer$wbfill$, false);");
    await Assert.That(sql).DoesNotContain("wh_sync_physical_moves")
      .Because("both releases read an Extracted field from the document, so a write cannot be read in the wrong place");
  }
}
