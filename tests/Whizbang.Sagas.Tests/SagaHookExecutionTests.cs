using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the per-hook execution record on <see cref="BaseSagaModel.Hooks"/>.
/// Same Pending → Running → Completed/Failed shape as items, but framework-managed
/// (consumers never construct these directly — <c>BaseSagaService.TryRunHookAsync</c>
/// adds and updates them).
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaHookExecutionTests {

  [Test]
  public async Task Defaults_ArePendingAndEmptyNamesAsync() {
    var hook = new SagaHookExecution();

    await Assert.That(hook.HookName).IsEqualTo(string.Empty);
    await Assert.That(hook.Status).IsEqualTo(SagaItemState.Pending);
    await Assert.That(hook.StartedAt).IsNull();
    await Assert.That(hook.CompletedAt).IsNull();
    await Assert.That(hook.ErrorMessage).IsNull();
  }

  [Test]
  [Arguments(SagaItemState.Pending, false)]
  [Arguments(SagaItemState.Running, false)]
  [Arguments(SagaItemState.Completed, true)]
  [Arguments(SagaItemState.Failed, true)]
  [Arguments(SagaItemState.Skipped, true)]
  public async Task IsTerminal_MirrorsSagaItemStateAsync(SagaItemState state, bool expected) {
    var hook = new SagaHookExecution { Status = state };

    await Assert.That(hook.IsTerminal).IsEqualTo(expected)
      .Because("Hook terminal semantics must match item terminal semantics — Rule-17 saga completion treats unfinished hooks as in-flight work alongside unfinished items.");
  }
}
