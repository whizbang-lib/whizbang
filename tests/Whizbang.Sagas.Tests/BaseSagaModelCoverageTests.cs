using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Coverage for <see cref="BaseSagaModel"/> members the sibling <see cref="BaseSagaModelTests"/>
/// suite never exercises: the plain <see cref="BaseSagaModel.Summary"/> and
/// <see cref="BaseSagaModel.CreatedAt"/> setters, the <see cref="BaseSagaModel.Hooks"/> setter
/// (distinct from its lazy-init getter, which the sibling suite does cover), and
/// <see cref="BaseSagaModel.GetHooks"/>. Pure POCO — no I/O, no database.
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/Models/BaseSagaModel.cs</code-under-test>
public class BaseSagaModelCoverageTests {

  // The Hooks setter (distinct from the lazy-init getter) is what restores hook history when a
  // saga row is deserialized from storage. If it silently no-op'd in favor of the lazy-init
  // default, a resumed saga would believe none of its pre/post-work hooks had ever run and
  // re-execute hooks that already had side effects.
  [Test]
  public async Task Hooks_Setter_ReplacesTheBackingListAsync() {
    var saga = new BaseSagaModel();
    var restored = new List<SagaHookExecution> {
      new() { HookName = "pre-embed", Status = SagaItemState.Completed }
    };

    saga.Hooks = restored;

    // Identity, not just contents: the getter is `get => _hooks ??= [];`, so the risk being
    // tested is that lazy-init hands back a DIFFERENT list than the one assigned. Comparing
    // counts would pass even if the getter substituted a copy, and a copy is what would make a
    // later Add() vanish.
    await Assert.That(saga.Hooks).IsSameReferenceAs(restored)
      .Because("the setter must store the assigned list itself; a lazy-init default substituted "
             + "here would silently drop every hook a resumed saga had already recorded");
    await Assert.That(saga.Hooks[0].HookName).IsEqualTo("pre-embed");
  }

  // GetHooks is the dashboard-facing surface for hook execution history; if it stopped surfacing
  // the live Hooks reference, the UI would show a saga's hook history as permanently empty even
  // though the row recorded real hook executions.
  [Test]
  public async Task GetHooks_ReturnsTheLiveHooksReferenceAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "post-notify" });

    var hooks = saga.GetHooks();

    await Assert.That(hooks.Count).IsEqualTo(1)
      .Because("GetHooks must surface the hook execution the saga actually recorded, not an empty snapshot");
    await Assert.That(hooks[0].HookName).IsEqualTo("post-notify");
  }
}
