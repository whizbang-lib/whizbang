// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch coverage for <see cref="StartupPipelineState"/>'s "all planned blocking steps drained"
/// check: a planned blocking step that has STARTED but not finished holds readiness back exactly as
/// one that has not started does.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineState.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineStateBranchCoverageTests {

  private static StartupStepDescriptor _blocking(string name) => new() { Name = name, Blocking = true };

  private static StartupStepResult _completed(string name) =>
    new(name, StartupStepOutcome.Completed, TimeSpan.Zero, Reason: null);

  /// <summary>
  /// Two blocking steps both running: the second finishing first must not report ready while
  /// the first is still Running. Readiness gates traffic, so treating a running blocking step as
  /// drained would route writes at a host whose migration has not finished.
  /// </summary>
  [Test]
  public async Task OnStepCompletedAsync_AnotherPlannedBlockingStepStillRunning_IsNotReadyUntilItFinishesAsync() {
    var state = new StartupPipelineState();
    var first = _blocking("Migrate");
    var second = _blocking("Reconcile");
    await state.OnRunStartingAsync(new StartupRunPlan([first, second]), CancellationToken.None);
    await state.OnStepStartingAsync(new StartupStepContext(first), CancellationToken.None);
    await state.OnStepStartingAsync(new StartupStepContext(second), CancellationToken.None);

    await state.OnStepCompletedAsync(_completed("Reconcile"), CancellationToken.None);

    await Assert.That(state.StatusOf("Migrate")).IsEqualTo(StartupStepStatus.Running);
    await Assert.That(state.IsReady).IsFalse()
      .Because("a planned blocking step that is Running has not drained, whatever its peers have done");

    await state.OnStepCompletedAsync(_completed("Migrate"), CancellationToken.None);

    await Assert.That(state.IsReady).IsTrue()
      .Because("once the running step completes every planned blocking step has drained");
  }

  /// <summary>
  /// The other half of the same check: a planned blocking step with no status at all (never
  /// started) also holds readiness back.
  /// </summary>
  [Test]
  public async Task OnStepCompletedAsync_AnotherPlannedBlockingStepNeverStarted_IsNotReadyAsync() {
    var state = new StartupPipelineState();
    var first = _blocking("Migrate");
    var second = _blocking("Reconcile");
    await state.OnRunStartingAsync(new StartupRunPlan([first, second]), CancellationToken.None);
    await state.OnStepStartingAsync(new StartupStepContext(second), CancellationToken.None);

    await state.OnStepCompletedAsync(_completed("Reconcile"), CancellationToken.None);

    await Assert.That(state.StatusOf("Migrate")).IsEqualTo(StartupStepStatus.Pending);
    await Assert.That(state.IsReady).IsFalse()
      .Because("a planned blocking step that never started has not drained");
  }
}
