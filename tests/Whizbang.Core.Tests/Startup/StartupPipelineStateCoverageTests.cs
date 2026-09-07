using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Coverage round 23 — closes gaps in <see cref="StartupPipelineState"/> itself: the empty-snapshot
/// path before any plan exists, and the run-boundary reset a step notification takes when it arrives
/// with no preceding <c>OnRunStartingAsync</c> plan announcement.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineState.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineStateCoverageTests {

  /// <summary>
  /// The readiness endpoint calls SnapshotSteps() to describe the current run; if this ever threw
  /// or fabricated phantom steps before a plan exists, a status page or health probe hit during the
  /// window before the first OnRunStartingAsync would crash or report steps that were never declared.
  /// </summary>
  [Test]
  public async Task SnapshotSteps_BeforeAnyPlanIsAnnounced_ReturnsEmptyAsync() {
    var state = new StartupPipelineState();

    await Assert.That(state.SnapshotSteps()).IsEmpty()
      .Because("empty before the first run announces its plan — never a fabricated list");
  }

  /// <summary>
  /// A step can start without a preceding plan announcement (an observer wired directly, or a runner
  /// bug that skips OnRunStartingAsync). If the reset here stopped clearing the stale plan bookkeeping
  /// (_hasPlan/_plannedBlocking/_plannedOrder), a later OnStepCompletedAsync could incorrectly treat
  /// the run as ready, or SnapshotSteps could keep describing a plan from a previous run — either way
  /// the readiness endpoint would report a state that does not match what actually happened.
  /// </summary>
  [Test]
  public async Task OnStepStartingAsync_WithoutAPrecedingPlanAnnouncement_ResetsAndNeverBecomesReadyAsync() {
    var state = new StartupPipelineState();
    var descriptor = new StartupStepDescriptor { Name = "Migrate" };

    await state.OnStepStartingAsync(new StartupStepContext(descriptor), CancellationToken.None);

    await Assert.That(state.HasRunStarted).IsTrue()
      .Because("a step starting is itself the start of a run, plan or not");
    await Assert.That(state.SnapshotSteps()).IsEmpty()
      .Because("no plan was ever announced, so there is nothing to list as planned");

    await state.OnStepCompletedAsync(
      new StartupStepResult("Migrate", StartupStepOutcome.Completed, TimeSpan.Zero, Reason: null),
      CancellationToken.None);

    await Assert.That(state.IsReady).IsFalse()
      .Because("readiness is fail-closed when no plan was announced: 'all blocking steps drained' is " +
               "not computable without the plan, so completing every step that ran must never flip " +
               "readiness on and route traffic at a host that never confirmed its blocking work finished");
  }
}
