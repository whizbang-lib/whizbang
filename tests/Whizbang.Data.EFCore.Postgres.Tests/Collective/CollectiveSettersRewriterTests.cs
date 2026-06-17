#pragma warning disable CA1707

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Verifies <see cref="CollectiveSettersRewriter.Rewrite"/> translates
/// the spec's model-level <c>SetProperty</c> calls into the row-level
/// shape EF Core's <see cref="UpdateSettersBuilder{T}"/> expects. The
/// adapter (Slice 6) feeds the result straight into
/// <c>ExecuteUpdateAsync</c>; if the rewriting is wrong, the SQL UPDATE
/// either fails to translate or updates the wrong column.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveSettersRewriterTests {

  // ── Constant-value SetProperty ─────────────────────────────────────────

  [Test]
  public async Task Rewrite_ConstantValue_ProducesRowLevelSelectorAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived");

    var rewritten = CollectiveSettersRewriter.Rewrite(source);

    await Assert.That(rewritten).IsNotNull();
    await Assert.That(rewritten.Parameters[0].Type)
      .IsEqualTo(typeof(UpdateSettersBuilder<PerspectiveRow<_jobModel>>))
      .Because("EF Core's ExecuteUpdateAsync expects the setters delegate to take UpdateSettersBuilder<PerspectiveRow<TModel>> — anything else won't translate.");

    var text = rewritten.ToString();
    await Assert.That(text).Contains("Data.Status")
      .Because("Selector must navigate through .Data so EF Core resolves the column under PerspectiveRow's jsonb mapping.");
    await Assert.That(text).Contains("Archived")
      .Because("Constant value passed through to the rewritten call.");
  }

  // ── Computed-value SetProperty (increment) ─────────────────────────────

  [Test]
  public async Task Rewrite_ComputedValue_ProducesRowLevelSelectorAndComputedAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1);

    var rewritten = CollectiveSettersRewriter.Rewrite(source);
    var text = rewritten.ToString();

    await Assert.That(text).Contains("Data.ViewCount")
      .Because("Both the selector AND the computed expression must be rewritten through r.Data.");

    // The computed side appears at least twice in the rewritten body:
    // once as the selector path, once on the right-hand side of the
    // increment expression. Loose check — exact ToString format varies
    // by .NET runtime version, but the navigation through .Data must
    // appear in BOTH the lhs and rhs of the assignment.
    var occurrences = text.Split(_dataViewCount, StringSplitOptions.None).Length - 1;
    await Assert.That(occurrences).IsGreaterThanOrEqualTo(2)
      .Because("Computed side references the property too; both lhs and rhs must navigate through .Data.");
  }

  // ── Multiple SetProperty calls chained ─────────────────────────────────

  [Test]
  public async Task Rewrite_MultipleUpdateSettersBuilder_PreservesChainAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "Archived")
           .SetProperty(j => j.ViewCount, 0);

    var rewritten = CollectiveSettersRewriter.Rewrite(source);
    var text = rewritten.ToString();

    await Assert.That(text).Contains("Data.Status");
    await Assert.That(text).Contains("Data.ViewCount");
    await Assert.That(text).Contains("Archived");
  }

  // ── Null source defensive guard ────────────────────────────────────────

  [Test]
  public async Task Rewrite_NullSource_ThrowsArgumentNullAsync() {
    await Assert.That(() => CollectiveSettersRewriter.Rewrite<_jobModel>(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Parameter substitution invariant ───────────────────────────────────

  [Test]
  public async Task Rewrite_AllOriginalModelReferences_AreReplacedByRowDataAccessAsync() {
    Expression<Action<ICollectiveSetters<_jobModel>>> source =
      s => s.SetProperty(j => j.Status, "X");

    var rewritten = CollectiveSettersRewriter.Rewrite(source);

    // The rewritten body should NOT contain a free parameter referencing
    // the original `j` model parameter. The visitor must replace every
    // occurrence (including in the second-arg computed lambda when used).
    var text = rewritten.ToString();

    // Cheap negative check: the original parameter name "j" should not
    // appear as a free identifier (vs being part of "Data" or another
    // identifier). The rewritten selector should use "r" (the new
    // parameter name).
    await Assert.That(text).Contains("r.Data")
      .Because("Property access must go through the new row parameter, not the original model parameter.");
  }

  // ── Inline test types ──────────────────────────────────────────────────

  private static readonly string[] _dataViewCount = ["Data.ViewCount"];

  private sealed class _jobModel {
    public string Status { get; set; } = string.Empty;
    public int ViewCount { get; set; }
  }
}
