#pragma warning disable CA1707

using System.Linq.Expressions;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for collective setters and conditions on <c>[PhysicalField]</c> properties: the
/// Dapper SET compiler writes the column as a typed parameter (and the document path too when the storage mode
/// keeps both), a spec that touches only physical-only fields never assigns <c>data</c>, and the shared WHERE
/// compiler sends a condition on a physical property to its column. The models are registered exactly as the
/// generated perspective runner registers them.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectivePhysicalColumnCompilerTests {
  private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = null };

  static CollectivePhysicalColumnCompilerTests() {
    // What the perspective runner's [ModuleInitializer] emits for these models.
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Lane), "lane", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Flag), "flag", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Priority), "prio", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Cells), "cells", FieldStorageMode.Split, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Notes), "notes", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Embedding), "embedding", FieldStorageMode.Split, isVector: true);
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Kind), "kind", FieldStorageMode.Split, scalarType: typeof(int));
    // Registered without a scalar type (a hand registration): the runtime derives the same scalar.
    PerspectivePhysicalFieldRegistry.Register(typeof(SplitModel), nameof(SplitModel.Size), "size", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), nameof(ExtractedModel.Kind), "kind", FieldStorageMode.Extracted, scalarType: typeof(int));
    PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), nameof(ExtractedModel.TextKind), "text_kind", FieldStorageMode.Extracted, columnType: "text");
    PerspectivePhysicalFieldRegistry.Register(typeof(ExtractedModel), nameof(ExtractedModel.Priority), "priority", FieldStorageMode.Extracted);
    PerspectivePhysicalFieldRegistry.Register(typeof(SiblingModel), nameof(SiblingModel.Lane), "lane", FieldStorageMode.Split);
  }

  // ── SET: physical-only fields never assign data ─────────────────────────

  [Test]
  public async Task Compile_PhysicalOnlySetter_AssignsTheColumn_AndNotDataAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, "hot")), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"lane\" = @set_0_lane")
      .Because("A Split field lives only in its column: the statement must not rewrite the document.");
    await Assert.That(compiled.Parameters["set_0_lane"]).IsEqualTo("hot")
      .Because("The column is bound as a typed parameter (the CLR value), not as JSON text.");
  }

  [Test]
  public async Task Compile_TwoPhysicalOnlySetters_AssignBothColumnsAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, "hot").SetProperty(m => m.Priority, 7)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"lane\" = @set_0_lane, \"prio\" = @set_1_priority");
    await Assert.That(compiled.Parameters["set_1_priority"]).IsEqualTo(7);
  }

  [Test]
  public async Task Compile_SamePhysicalFieldTwice_LastSetterWinsAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, "first").SetProperty(m => m.Lane, "second")), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"lane\" = @set_1_lane")
      .Because("Postgres refuses two assignments to one column; the last setter wins, as it does on a document path.");
    await Assert.That(compiled.Parameters["set_1_lane"]).IsEqualTo("second");
  }

  [Test]
  public async Task Compile_PhysicalSetterToNull_BindsNullAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, (string?)null)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"lane\" = @set_0_lane");
    await Assert.That(compiled.Parameters["set_0_lane"]).IsNull();
  }

  // ── SET: kept in both places, and mixed ─────────────────────────────────

  [Test]
  public async Task Compile_ExtractedSetter_AssignsTheColumnAndTheDocumentPathAsync() {
    var compiled = DapperCollectiveSpecCompiler<ExtractedModel>.Compile(
      new ExtractedSpec(s => s.SetProperty(m => m.Priority, 5)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo(
      "data = jsonb_set(data, '{Priority}', @set_0_priority::jsonb), \"priority\" = @set_1_priority")
      .Because("Extracted keeps the field in the document and in the column, and both are written in one statement.");
    await Assert.That(compiled.Parameters["set_0_priority"]).IsEqualTo("5");
    await Assert.That(compiled.Parameters["set_1_priority"]).IsEqualTo(5);
  }

  [Test]
  public async Task Compile_MixedSetters_AssignDocumentAndColumnInOneClauseAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, "hot").SetProperty(m => m.Title, "Moved")), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo(
      "data = jsonb_set(data, '{Title}', @set_1_title::jsonb), \"lane\" = @set_0_lane");
  }

  // ── SET: computed comparisons read the column ───────────────────────────

  [Test]
  public async Task Compile_ComputedOverPhysicalField_ComparesTheColumnNullSafelyAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.IsHot, m => m.Lane == "hot")), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo(
      "data = jsonb_set(data, '{IsHot}', to_jsonb((\"lane\" IS NOT DISTINCT FROM @set_0_ishot)))")
      .Because("A Split field's document value is a placeholder, so the comparison has to read the column.");
    await Assert.That(compiled.Parameters["set_0_ishot"]).IsEqualTo("hot");
  }

  [Test]
  public async Task Compile_ComputedNotEqualOverPhysicalField_UsesIsDistinctFromAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.IsHot, m => m.Lane != "hot")), _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("(\"lane\" IS DISTINCT FROM @set_0_ishot)");
  }

  [Test]
  public async Task Compile_ComputedIntoPhysicalField_AssignsTheBooleanToTheColumnAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Flag, m => m.Title == "x")), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"flag\" = ((data->'Title')::jsonb = @set_0_flag::jsonb)")
      .Because("The target is a column, so the comparison is assigned as a boolean rather than wrapped in to_jsonb.");
    await Assert.That(compiled.Parameters["set_0_flag"]).IsEqualTo("\"x\"");
  }

  // ── SET: hooks ──────────────────────────────────────────────────────────

  [Test]
  public async Task Compile_HookSetterOnPhysicalField_AssignsTheColumnAsync() {
    Expression<Func<SplitModel, string?>> selector = m => m.Lane;
    var hook = new SetPropertyOp(selector, nameof(SplitModel.Lane), "from-hook", typeof(string));

    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Title, "t")), _jsonOptions, hookSetters: [hook]);

    await Assert.That(compiled.SqlFragment).IsEqualTo(
      "data = jsonb_set(data, '{Title}', @set_0_title::jsonb), \"lane\" = @set_1_lane");
    await Assert.That(compiled.Parameters["set_1_lane"]).IsEqualTo("from-hook");
  }

  [Test]
  public async Task Compile_HookRemovesThePhysicalSetter_DropsTheColumnAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, "hot").SetProperty(m => m.Title, "t")), _jsonOptions,
      removedFields: new HashSet<string>(StringComparer.Ordinal) { nameof(SplitModel.Lane) });

    await Assert.That(compiled.SqlFragment).IsEqualTo("data = jsonb_set(data, '{Title}', @set_1_title::jsonb)");
  }

  [Test]
  public async Task Compile_EverySetterRemoved_KeepsAValidSetClauseAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Lane, "hot")), _jsonOptions,
      removedFields: new HashSet<string>(StringComparer.Ordinal) { nameof(SplitModel.Lane) });

    await Assert.That(compiled.SqlFragment).IsEqualTo("data = data")
      .Because("With nothing left to set the statement still needs a SET list; the store-column tail follows it.");
  }

  // ── SET: shapes a column cannot take ────────────────────────────────────

  // ── SET: keyed arrays in a jsonb column ─────────────────────────────────

  [Test]
  public async Task Compile_UpsertElementOnAJsonbColumn_UpsertsInTheColumnAsync() {
    var cell = new Cell { Key = "k" };
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.UpsertElement(m => m.Cells, c => c.Key, cell)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).StartsWith("\"cells\" = (SELECT CASE WHEN jsonb_typeof(wh_s.a) = 'array'")
      .Because("A keyed array backed by a jsonb column is upserted in the column, with the same keyed semantics.");
    await Assert.That(compiled.SqlFragment).EndsWith("FROM (SELECT (\"cells\") AS a) AS wh_s)");
    await Assert.That(compiled.SqlFragment).DoesNotContain("data =");
    await Assert.That(compiled.Parameters["set_0_cells"]).IsEqualTo("{\"Key\":\"k\"}");
  }

  [Test]
  public async Task Compile_TwoUpsertsOnOneJsonbColumn_ComposeInCallOrderAsync() {
    var first = new Cell { Key = "a" };
    var second = new Cell { Key = "b" };
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.UpsertElement(m => m.Cells, c => c.Key, first).UpsertElement(m => m.Cells, c => c.Key, second)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("@set_1_cells::jsonb");
    await Assert.That(compiled.SqlFragment).Contains("FROM (SELECT ((SELECT CASE")
      .Because("The second upsert starts from the list the first one produced, as it does on a document path.");
  }

  [Test]
  public async Task Compile_UpsertElementOnANonJsonbPhysicalColumn_ThrowsNotSupportedAsync() {
    var cell = new Cell { Key = "k" };
    var spec = _split(s => s.UpsertElement(m => m.Notes, c => c.Key, cell));

    await Assert.That(() => DapperCollectiveSpecCompiler<SplitModel>.Compile(spec, _jsonOptions))
      .Throws<NotSupportedException>().WithMessageContaining("jsonb");
  }

  // ── SET: vectors ────────────────────────────────────────────────────────

  [Test]
  public async Task Compile_VectorSetter_BindsTheVectorTextCastToVectorAsync() {
    var embedding = new[] { 1f, 2.5f, -0.125f };
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Embedding, embedding)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"embedding\" = @set_0_embedding::vector");
    await Assert.That(compiled.Parameters["set_0_embedding"]).IsEqualTo("[1,2.5,-0.125]")
      .Because("The Dapper per-event write sends a vector as its text form; the collective sends the same form.");
  }

  [Test]
  public async Task Compile_NullVectorSetter_BindsNullAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Embedding, (float[]?)null)), _jsonOptions);

    await Assert.That(compiled.Parameters["set_0_embedding"]).IsNull();
  }

  [Test]
  public async Task Compile_ComputedOverAVector_ThrowsNotSupportedAsync() {
    var spec = _split(s => s.SetProperty(m => m.IsHot, m => m.Embedding == null));

    await Assert.That(() => DapperCollectiveSpecCompiler<SplitModel>.Compile(spec, _jsonOptions))
      .Throws<NotSupportedException>().WithMessageContaining("[VectorField]");
  }

  // ── SET: enumerations are stored as their underlying number ─────────────

  [Test]
  public async Task Compile_EnumSetter_BindsTheUnderlyingNumberAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Kind, TicketKind.Bug)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).IsEqualTo("\"kind\" = @set_0_kind");
    await Assert.That(compiled.Parameters["set_0_kind"]).IsTypeOf<int>();
    await Assert.That(compiled.Parameters["set_0_kind"]).IsEqualTo(1);
  }

  [Test]
  public async Task Compile_EnumWithoutARegisteredScalarType_DerivesTheSameScalarAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.Size, TicketSize.Large)), _jsonOptions);

    await Assert.That(compiled.Parameters["set_0_size"]).IsEqualTo((short)2);
  }

  [Test]
  public async Task Compile_EnumKeptInBothPlaces_WritesTheNumberToBothAsync() {
    var compiled = DapperCollectiveSpecCompiler<ExtractedModel>.Compile(
      new ExtractedSpec(s => s.SetProperty(m => m.Kind, TicketKind.Bug)), _jsonOptions);

    await Assert.That(compiled.Parameters["set_0_kind"]).IsEqualTo("1");
    await Assert.That(compiled.Parameters["set_1_kind"]).IsEqualTo(1);
  }

  [Test]
  public async Task Compile_EnumInADeclaredColumnType_ThrowsNotSupportedAsync() {
    var spec = new ExtractedSpec(s => s.SetProperty(m => m.TextKind, TicketKind.Bug));

    await Assert.That(() => DapperCollectiveSpecCompiler<ExtractedModel>.Compile(spec, _jsonOptions))
      .Throws<NotSupportedException>().WithMessageContaining("declared as text")
      .Because("An enum in a column whose type the author chose has a stored form the collective cannot know.");
  }

  [Test]
  public async Task Compile_ComputedOverAnEnumColumn_ComparesTheNumberAsync() {
    var compiled = DapperCollectiveSpecCompiler<SplitModel>.Compile(
      _split(s => s.SetProperty(m => m.IsHot, m => m.Kind == TicketKind.Bug)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("(\"kind\" IS NOT DISTINCT FROM @set_0_ishot)");
    await Assert.That(compiled.Parameters["set_0_ishot"]).IsEqualTo(1);
  }

  // ── WHERE: a condition on a physical field reads the column ─────────────

  [Test]
  public async Task Where_EqualityOnPhysicalField_ComparesTheColumnAsync() {
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter = r => r.Data.Priority == 3;

    var result = CollectivePredicateSqlCompiler<SplitModel>.Compile(filter, outerTableName: "wh_per_ticket");

    await Assert.That(result.SqlFragment).IsEqualTo("\"prio\" = @where_priority");
    await Assert.That(result.Parameters["where_priority"]).IsEqualTo(3)
      .Because("A column compares against a typed parameter, not the ->> text a document path yields.");
    await Assert.That(result.ReferencedJsonPaths).IsEmpty()
      .Because("A physical column is indexed by its own declaration, not by an expression index over data.");
  }

  [Test]
  public async Task Where_ContainsOnPhysicalField_EmitsInOverTheColumnAsync() {
    var lanes = new[] { "hot", "cold" };
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter = r => lanes.Contains(r.Data.Lane);

    var result = CollectivePredicateSqlCompiler<SplitModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("\"lane\" IN (@where_lane_0, @where_lane_1)");
    await Assert.That(result.Parameters["where_lane_1"]).IsEqualTo("cold");
  }

  [Test]
  public async Task Where_DocumentFieldBesidePhysicalField_KeepsTheJsonPathAsync() {
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter = r => r.Data.Lane == "hot" && r.Data.Title == "x";

    var result = CollectivePredicateSqlCompiler<SplitModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("(\"lane\" = @where_lane AND data->>'Title' = @where_title)");
  }

  [Test]
  public async Task Where_PhysicalFieldInsideCrossPerspectiveCohort_QualifiesTheSiblingColumnAsync() {
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(SiblingModel)] = "wh_per_sibling" });
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter =
      r => q.Of<SiblingModel>().Any(s => s.Id == r.Id && s.Data.Lane == "hot") && r.Data.Lane == "cold";

    var result = CollectivePredicateSqlCompiler<SplitModel>.Compile(filter, outerTableName: "wh_per_ticket");

    await Assert.That(result.SqlFragment).IsEqualTo(
      "(EXISTS (SELECT 1 FROM wh_per_sibling s WHERE (s.id = wh_per_ticket.id AND s.\"lane\" = @where_lane)) AND \"lane\" = @where_lane_1)");
    await Assert.That(result.Parameters["where_lane"]).IsEqualTo("hot");
    await Assert.That(result.Parameters["where_lane_1"]).IsEqualTo("cold")
      .Because("Two conditions on one property name must bind two parameters; sharing one name let the second value overwrite the first.");
  }

  [Test]
  public async Task Where_VectorPhysicalField_ThrowsNotSupportedAsync() {
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter = r => r.Data.Embedding == null;

    await Assert.That(() => CollectivePredicateSqlCompiler<SplitModel>.Compile(filter))
      .Throws<NotSupportedException>().WithMessageContaining("[VectorField]");
  }

  [Test]
  public async Task Where_EnumPhysicalField_ComparesTheUnderlyingNumberAsync() {
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter = r => r.Data.Kind == TicketKind.Bug;

    var result = CollectivePredicateSqlCompiler<SplitModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("\"kind\" = @where_kind");
    await Assert.That(result.Parameters["where_kind"]).IsEqualTo(1);
  }

  [Test]
  public async Task Where_ContainsOnAnEnumPhysicalField_BindsNumbersAsync() {
    var kinds = new[] { TicketKind.Task, TicketKind.Bug };
    Expression<Func<PerspectiveRow<SplitModel>, bool>> filter = r => kinds.Contains(r.Data.Kind);

    var result = CollectivePredicateSqlCompiler<SplitModel>.Compile(filter);

    await Assert.That(result.SqlFragment).IsEqualTo("\"kind\" IN (@where_kind_0, @where_kind_1)");
    await Assert.That(result.Parameters["where_kind_0"]).IsEqualTo(0);
    await Assert.That(result.Parameters["where_kind_1"]).IsEqualTo(1);
  }

  // ── Shared helpers ──────────────────────────────────────────────────────

  [Test]
  public async Task Quote_DoublesAnEmbeddedQuoteAsync() {
    await Assert.That(CollectivePhysicalColumns.Quote("we\"ird")).IsEqualTo("\"we\"\"ird\"");
  }

  [Test]
  public async Task RenderSetList_NoDocumentAndNoColumns_FallsBackToDataEqualsDataAsync() {
    await Assert.That(CollectivePhysicalColumns.RenderSetList(null, [])).IsEqualTo("data = data");
  }

  // ── Fixtures ────────────────────────────────────────────────────────────

  private enum TicketKind { Task, Bug }

  private enum TicketSize : byte { Small, Medium, Large }

  private sealed class Cell {
    public string Key { get; set; } = "";
  }

  private sealed class SplitModel {
    public string? Lane { get; }
    public int Priority { get; }
    public bool Flag { get; }
    public bool IsHot { get; }
    public string Title { get; } = "";
    public List<Cell>? Cells { get; }
    public List<Cell>? Notes { get; }
    public TicketSize Size { get; }
    public float[]? Embedding { get; }
    public TicketKind Kind { get; }
  }

  private sealed class ExtractedModel {
    public int Priority { get; }
    public TicketKind Kind { get; }
    public TicketKind TextKind { get; }
  }

  private sealed class SiblingModel {
    public string? Lane { get; }
  }

#pragma warning disable CA1859 // tests assert against the interface
  private static ICollectiveSpec<SplitModel> _split(Expression<Action<ICollectiveSetters<SplitModel>>> setters) =>
    new SplitSpec(setters);
#pragma warning restore CA1859

  private sealed record SplitSpec(Expression<Action<ICollectiveSetters<SplitModel>>> Setters) : ICollectiveSpec<SplitModel>;

  private sealed record ExtractedSpec(Expression<Action<ICollectiveSetters<ExtractedModel>>> Setters) : ICollectiveSpec<ExtractedModel>;
}
