using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Coverage for one branch the sibling <see cref="SagaHookApplyHelperTests"/> suite never
/// drives: <see cref="SagaHookApplyHelper.TrackHookStarted"/> backfilling
/// <see cref="SagaHookExecution.DisplayName"/> on an EXISTING non-terminal row. Every existing
/// "existing hook" test in that suite passes <c>displayName: null</c>, so the backfill branch has
/// never actually run.
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/Helpers/SagaHookApplyHelper.cs</code-under-test>
public class SagaHookApplyHelperCoverageTests {

  private static readonly DateTimeOffset _ts = DateTimeOffset.Parse("2026-06-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  // TrackHookStarted's DisplayName assignment is the ONLY place in this helper that can ever set
  // an existing row's DisplayName — TrackHookCompleted never touches it. A hook row created
  // without a display name (e.g. the first Started event carried none) has exactly one further
  // chance to pick one up: a redelivered/retried Started event that DOES carry one. If this
  // branch regressed to a no-op, that row's DisplayName would stay blank forever, even though a
  // later event proved a real name was available — a permanent UI gap, not a transient one.
  [Test]
  public async Task TrackHookStarted_ExistingNonTerminalHookWithNoDisplayName_BackfillsItAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Pending, DisplayName = null });

    SagaHookApplyHelper.TrackHookStarted(saga, hookName: "pre-archive", displayName: "Pre-Archive Step", timestamp: _ts);

    await Assert.That(saga.Hooks[0].DisplayName).IsEqualTo("Pre-Archive Step")
      .Because("a redelivered Started event that carries a display name the row never had must backfill it — TrackHookCompleted never gets another chance to");
    await Assert.That(saga.Hooks[0].Status).IsEqualTo(SagaItemState.Running);
  }

  // The mirror case: an existing row that ALREADY has a display name must not have it clobbered
  // by a later Started redelivery — the guard is `hook.DisplayName is null`, not
  // "always overwrite". Losing this would let a later replay with a stale/empty name erase a
  // good one the row already recorded.
  [Test]
  public async Task TrackHookStarted_ExistingHookAlreadyHasDisplayName_DoesNotOverwriteAsync() {
    var saga = new BaseSagaModel();
    saga.Hooks.Add(new SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Pending, DisplayName = "Original Name" });

    SagaHookApplyHelper.TrackHookStarted(saga, hookName: "pre-archive", displayName: "Replayed Name", timestamp: _ts);

    await Assert.That(saga.Hooks[0].DisplayName).IsEqualTo("Original Name")
      .Because("the first display name a row records must survive replay of the same Started event under a different payload");
  }
}
