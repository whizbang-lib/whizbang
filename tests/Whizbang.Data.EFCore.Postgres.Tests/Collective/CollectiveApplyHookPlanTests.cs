using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// A collective apply hook contributes store columns and cohort predicates that are rendered
/// straight into SQL, so the identifier guard is the boundary between a hook and injected DDL.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveApplyHookPlan.cs</code-under-test>
[Category("Collective")]
[Category("Shard4")]
public class CollectiveApplyHookPlanTests {

  private sealed class OrderModel {
    public string Status { get; set; } = string.Empty;
    public int Total { get; set; }
  }

  private static CollectiveApplyHookPlan<OrderModel> _plan(
      IReadOnlyList<CollectiveStoreColumn>? storeColumns = null,
      bool bumpVersion = false,
      IReadOnlyList<Expression<Func<PerspectiveRow<OrderModel>, bool>>>? andWheres = null,
      Expression<Func<PerspectiveRow<OrderModel>, bool>>? replaceWhere = null)
    => new(
      ModelFieldSetters: [],
      RemovedModelFields: new HashSet<string>(StringComparer.Ordinal),
      StoreColumns: storeColumns ?? [],
      BumpVersion: bumpVersion,
      AndWheres: andWheres ?? [],
      ReplaceWhere: replaceWhere);

  // --- Identifier validation -------------------------------------------------

  [Test]
  [Arguments("status")]
  [Arguments("_private")]
  [Arguments("col_1")]
  [Arguments("A")]
  public async Task IsValidIdentifier_AcceptsUnquotedPostgresIdentifiersAsync(string column) {
    await Assert.That(CollectiveStoreColumn.IsValidIdentifier(column)).IsTrue();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  public async Task IsValidIdentifier_RejectsMissingNamesAsync(string? column) {
    await Assert.That(CollectiveStoreColumn.IsValidIdentifier(column)).IsFalse();
  }

  [Test]
  public async Task IsValidIdentifier_RejectsNamesOverThePostgresLimitAsync() {
    // Postgres truncates identifiers at 63 bytes; a longer name would silently target a
    // different column than the hook named.
    await Assert.That(CollectiveStoreColumn.IsValidIdentifier(new string('a', 63))).IsTrue();
    await Assert.That(CollectiveStoreColumn.IsValidIdentifier(new string('a', 64))).IsFalse();
  }

  [Test]
  [Arguments("1status")]
  [Arguments("-status")]
  [Arguments(" status")]
  public async Task IsValidIdentifier_RejectsABadFirstCharacterAsync(string column) {
    await Assert.That(CollectiveStoreColumn.IsValidIdentifier(column)).IsFalse();
  }

  [Test]
  [Arguments("sta-tus")]
  [Arguments("status;drop")]
  [Arguments("sta tus")]
  [Arguments("status\"")]
  public async Task IsValidIdentifier_RejectsABadLaterCharacterAsync(string column) {
    await Assert.That(CollectiveStoreColumn.IsValidIdentifier(column)).IsFalse();
  }

  // --- Store column rendering ------------------------------------------------

  [Test]
  public async Task RenderStoreColumnSetTail_WithNoColumns_RendersNothingAsync() {
    await Assert.That(_plan().RenderStoreColumnSetTail()).IsEmpty();
  }

  [Test]
  public async Task RenderStoreColumnSetTail_ParameterisesEachColumnByIndexAsync() {
    // Values go through parameters, never interpolation — the column name is the only
    // part of a hook's contribution that reaches the SQL text.
    var plan = _plan([new CollectiveStoreColumn("status", "shipped"), new CollectiveStoreColumn("total", 42)]);

    var tail = plan.RenderStoreColumnSetTail();

    await Assert.That(tail).IsEqualTo(", \"status\" = @wb_hookcol0, \"total\" = @wb_hookcol1");
  }

  [Test]
  public async Task RenderStoreColumnSetTail_WithBumpVersion_AppendsTheVersionIncrementAsync() {
    var plan = _plan([new CollectiveStoreColumn("status", "shipped")], bumpVersion: true);

    await Assert.That(plan.RenderStoreColumnSetTail()).EndsWith(", version = version + 1");
  }

  [Test]
  public async Task RenderStoreColumnSetTail_BumpVersionAlone_StillRendersAsync() {
    await Assert.That(_plan(bumpVersion: true).RenderStoreColumnSetTail())
        .IsEqualTo(", version = version + 1");
  }

  [Test]
  public async Task RenderStoreColumnSetTail_WithAnInvalidColumn_ThrowsRatherThanEmitSqlAsync() {
    // The guard is the injection boundary: a hook that produces a crafted name must fail
    // loudly rather than have it interpolated into the SET clause.
    var plan = _plan([new CollectiveStoreColumn("status\"; DROP TABLE orders; --", "x")]);

    await Assert.That(plan.RenderStoreColumnSetTail)
        .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task RenderStoreColumnSetTail_NamesTheOffendingColumnAsync() {
    var plan = _plan([new CollectiveStoreColumn("bad-name", "x")]);

    var ex = Assert.Throws<InvalidOperationException>(() => plan.RenderStoreColumnSetTail());

    await Assert.That(ex!.Message).Contains("bad-name");
  }

  // --- Cohort composition ----------------------------------------------------

  [Test]
  public async Task ComposeCohort_WithNothing_ReturnsNullAsync() {
    await Assert.That(_plan().ComposeCohort(null)).IsNull();
  }

  [Test]
  public async Task ComposeCohort_WithOnlyASpecWhere_ReturnsItUnchangedAsync() {
    Expression<Func<PerspectiveRow<OrderModel>, bool>> spec = r => r.Data.Total > 10;

    await Assert.That(_plan().ComposeCohort(spec)).IsSameReferenceAs(spec);
  }

  [Test]
  public async Task ComposeCohort_ReplaceWhere_DisplacesTheSpecWhereAsync() {
    Expression<Func<PerspectiveRow<OrderModel>, bool>> spec = r => r.Data.Total > 10;
    Expression<Func<PerspectiveRow<OrderModel>, bool>> replace = r => r.Data.Status == "shipped";

    await Assert.That(_plan(replaceWhere: replace).ComposeCohort(spec)).IsSameReferenceAs(replace);
  }

  [Test]
  public async Task ComposeCohort_AndsTheSpecWhereWithAContributedPredicateAsync() {
    Expression<Func<PerspectiveRow<OrderModel>, bool>> spec = r => r.Data.Total > 10;
    Expression<Func<PerspectiveRow<OrderModel>, bool>> extra = r => r.Data.Status == "shipped";

    var cohort = _plan(andWheres: [extra]).ComposeCohort(spec);

    await Assert.That(cohort).IsNotNull();
    var compiled = cohort!.Compile();
    await Assert.That(compiled(_row("shipped", 20))).IsTrue();
    await Assert.That(compiled(_row("shipped", 5))).IsFalse();
    await Assert.That(compiled(_row("pending", 20))).IsFalse();
  }

  [Test]
  public async Task ComposeCohort_ChainsSeveralContributedPredicatesAsync() {
    // Each contributed predicate has its own lambda parameter; composing them means
    // rebinding onto one, or the resulting expression would not compile.
    Expression<Func<PerspectiveRow<OrderModel>, bool>> a = r => r.Data.Total > 10;
    Expression<Func<PerspectiveRow<OrderModel>, bool>> b = r => r.Data.Total < 100;
    Expression<Func<PerspectiveRow<OrderModel>, bool>> c = r => r.Data.Status == "shipped";

    var cohort = _plan(andWheres: [a, b, c]).ComposeCohort(null);

    await Assert.That(cohort).IsNotNull();
    var compiled = cohort!.Compile();
    await Assert.That(compiled(_row("shipped", 50))).IsTrue();
    await Assert.That(compiled(_row("shipped", 500))).IsFalse();
    await Assert.That(compiled(_row("pending", 50))).IsFalse();
  }

  [Test]
  public async Task ComposeCohort_WithOnlyContributedPredicates_NeedsNoSpecWhereAsync() {
    Expression<Func<PerspectiveRow<OrderModel>, bool>> only = r => r.Data.Status == "shipped";

    var cohort = _plan(andWheres: [only]).ComposeCohort(null);

    await Assert.That(cohort).IsSameReferenceAs(only);
  }

  private static PerspectiveRow<OrderModel> _row(string status, int total)
    => new() {
      Id = Guid.Empty,
      Data = new OrderModel { Status = status, Total = total },
      Metadata = new Whizbang.Core.Lenses.PerspectiveMetadata(),
      Scope = new Whizbang.Core.Lenses.PerspectiveScope(),
      CreatedAt = default,
      UpdatedAt = default,
      Version = 1,
    };

  [Test]
  public async Task Plan_CarriesTheModelFieldContributionsItWasBuiltWithAsync() {
    // The model-side halves of a hook's contribution ride the same plan as the store
    // columns; the applier reads all four together when it builds the update.
    var removed = new HashSet<string>(StringComparer.Ordinal) { "LegacyField" };
    var plan = new CollectiveApplyHookPlan<OrderModel>(
      ModelFieldSetters: [],
      RemovedModelFields: removed,
      StoreColumns: [new CollectiveStoreColumn("status", "shipped")],
      BumpVersion: true,
      AndWheres: [],
      ReplaceWhere: null);

    await Assert.That(plan.ModelFieldSetters).IsEmpty();
    await Assert.That(plan.RemovedModelFields).Contains("LegacyField");
    await Assert.That(plan.StoreColumns).Count().IsEqualTo(1);
    await Assert.That(plan.BumpVersion).IsTrue();
  }
}
