using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Unit tests for <see cref="CollectiveInMemoryEvaluator{TModel}"/> — the in-memory twin of the collective
/// SQL apply, used when a stream is folded row-by-row during a perspective replay/rebuild. Pure logic, no DB.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveInMemoryEvaluator.cs</tests>
[Category("Shard2")]
public class CollectiveInMemoryEvaluatorTests {

  private sealed class _model {
    public Guid Id { get; set; }
    public Guid GlobalTemplateId { get; set; }
    public bool IsActive { get; set; }
    public string Name { get; set; } = string.Empty;
  }

  private sealed record _spec(
    Expression<Action<ICollectiveSetters<_model>>> Setters,
    Expression<Func<PerspectiveRow<_model>, bool>>? Where = null) : ICollectiveSpec<_model>;

  // ── Apply: computed property-vs-constant comparison (the single-active flip) ──────────────────

  /// <summary>
  /// The single-active flip: IsActive = (Id == activatedOverlayId). Self-referential — depends only on the
  /// row's own Id and the event's static overlay id — so it re-applies deterministically to one in-memory row.
  /// </summary>
  [Test]
  public async Task Apply_ComputedComparison_SetsTrueOnlyForTheMatchingRowAsync() {
    var activated = Guid.NewGuid();
    var other = Guid.NewGuid();

    var spec = new _spec(Setters: s => s.SetProperty(o => o.IsActive, o => o.Id == activated));

    var matching = new _model { Id = activated, IsActive = false };
    var sibling = new _model { Id = other, IsActive = true };

    CollectiveInMemoryEvaluator<_model>.Apply(spec, matching);
    CollectiveInMemoryEvaluator<_model>.Apply(spec, sibling);

    await Assert.That(matching.IsActive).IsTrue().Because("Id == activated → IsActive true");
    await Assert.That(sibling.IsActive).IsFalse().Because("Id != activated → IsActive false (deactivates the sibling)");
  }

  /// <summary>A constant setter assigns the literal/captured value.</summary>
  [Test]
  public async Task Apply_ConstantSetter_AssignsValueAsync() {
    var spec = new _spec(Setters: s => s.SetProperty(o => o.Name, "archived"));
    var model = new _model { Name = "draft" };

    CollectiveInMemoryEvaluator<_model>.Apply(spec, model);

    await Assert.That(model.Name).IsEqualTo("archived");
  }

  /// <summary>Multiple setters compose; computed values read the pre-apply state (SQL single-statement parity).</summary>
  [Test]
  public async Task Apply_MultipleSetters_ComputedReadsPreApplyStateAsync() {
    var activated = Guid.NewGuid();
    var spec = new _spec(Setters: s => s
      .SetProperty(o => o.Name, "touched")
      .SetProperty(o => o.IsActive, o => o.Id == activated));
    var model = new _model { Id = activated, Name = "orig", IsActive = false };

    CollectiveInMemoryEvaluator<_model>.Apply(spec, model);

    await Assert.That(model.Name).IsEqualTo("touched");
    await Assert.That(model.IsActive).IsTrue();
  }

  // ── Matches: self-referential Where ───────────────────────────────────────────────────────────

  /// <summary>A self-referential Where (row's own column vs the event payload) gates the row in or out.</summary>
  [Test]
  public async Task Matches_SelfReferentialWhere_GatesByRowsOwnColumnAsync() {
    var family = Guid.NewGuid();
    var spec = new _spec(
      Setters: s => s.SetProperty(o => o.IsActive, o => o.Id == Guid.NewGuid()),
      Where: r => r.Data.GlobalTemplateId == family);

    var inFamily = new _model { GlobalTemplateId = family };
    var outOfFamily = new _model { GlobalTemplateId = Guid.NewGuid() };

    await Assert.That(CollectiveInMemoryEvaluator<_model>.Matches(spec, Guid.NewGuid(), inFamily)).IsTrue();
    await Assert.That(CollectiveInMemoryEvaluator<_model>.Matches(spec, Guid.NewGuid(), outOfFamily)).IsFalse();
  }

  /// <summary>A null Where means no per-row refinement (tenant scope enforced upstream) — always matches.</summary>
  [Test]
  public async Task Matches_NullWhere_ReturnsTrueAsync() {
    var spec = new _spec(Setters: s => s.SetProperty(o => o.Name, "x"));
    await Assert.That(CollectiveInMemoryEvaluator<_model>.Matches(spec, Guid.NewGuid(), new _model())).IsTrue();
  }
}
