using System.Linq.Expressions;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// <c>UpsertElement</c> replayed in memory must leave the model exactly as the SQL path leaves the row:
/// the matching element replaced where it stands, otherwise appended, a missing list created.
/// </summary>
/// <remarks>
/// Replay folds collective events into a model without a database. If the two paths disagreed, a rebuilt
/// read model would differ from the one the live apply produced.
/// </remarks>
[Category("Shard2")]
public class CollectiveInMemoryUpsertElementTests {

  private sealed class Cell {
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
  }

  private sealed class Model {
    public string? Tag { get; set; }
    public List<Cell>? Cells { get; set; } = [];
    public IReadOnlyList<Cell> Frozen { get; set; } = [];
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<Model>>> Setters) : ICollectiveSpec<Model> {
    public Expression<Func<PerspectiveRow<Model>, bool>>? Where => null;
  }

  private static Model _twoCells() => new() {
    Cells = [new Cell { Key = "k1", Value = "v1" }, new Cell { Key = "k2", Value = "v2" }],
  };

  private static List<string> _render(IEnumerable<Cell>? cells) => [.. (cells ?? []).Select(c => $"{c.Key}={c.Value}")];

  [Test]
  public async Task Upsert_ReplacesTheMatchingElement_KeepingOrderAsync() {
    var model = _twoCells();
    var cell = new Cell { Key = "k1", Value = "new" };

    CollectiveInMemoryEvaluator<Model>.Apply(new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key, cell)), model);

    await Assert.That(_render(model.Cells)).IsEquivalentTo(["k1=new", "k2=v2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Upsert_AppendsWhenNoElementHasTheKeyAsync() {
    var model = _twoCells();
    var cell = new Cell { Key = "k3", Value = "v3" };

    CollectiveInMemoryEvaluator<Model>.Apply(new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key, cell)), model);

    await Assert.That(_render(model.Cells)).IsEquivalentTo(["k1=v1", "k2=v2", "k3=v3"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Upsert_OnANullList_CreatesItAsync() {
    var model = new Model { Cells = null };
    var cell = new Cell { Key = "k1", Value = "v1" };

    CollectiveInMemoryEvaluator<Model>.Apply(new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key, cell)), model);

    await Assert.That(_render(model.Cells)).IsEquivalentTo(["k1=v1"]);
  }

  [Test]
  public async Task Upsert_OnAReadOnlyList_ReplacesItWithAWritableCopyAsync() {
    var model = new Model { Frozen = new List<Cell> { new() { Key = "k1", Value = "v1" } }.AsReadOnly() };
    var cell = new Cell { Key = "k1", Value = "new" };

    CollectiveInMemoryEvaluator<Model>.Apply(new Spec(s => s.UpsertElement(m => m.Frozen, c => c.Key, cell)), model);

    await Assert.That(_render(model.Frozen)).IsEquivalentTo(["k1=new"]);
  }

  [Test]
  public async Task Upsert_ComposesWithSetProperty_AndReadsThePreApplyStateAsync() {
    var model = _twoCells();
    model.Tag = "before";
    var cell = new Cell { Key = "k2", Value = "new" };

    CollectiveInMemoryEvaluator<Model>.Apply(
      new Spec(s => s.SetProperty(m => m.Tag, "after").UpsertElement(m => m.Cells, c => c.Key, cell)), model);

    await Assert.That(model.Tag).IsEqualTo("after");
    await Assert.That(_render(model.Cells)).IsEquivalentTo(["k1=v1", "k2=new"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Upsert_ANestedCollectionSelector_IsRefusedAsync() {
    var model = _twoCells();
    var cell = new Cell { Key = "k1", Value = "v1" };

    await Assert.That(() => CollectiveInMemoryEvaluator<Model>.Apply(
        new Spec(s => s.UpsertElement(m => m.Cells!.Take(1), c => c.Key, cell)), model))
      .Throws<NotSupportedException>();
  }
}
