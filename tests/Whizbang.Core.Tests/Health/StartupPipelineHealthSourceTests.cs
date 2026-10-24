// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// The "startup" health component (increment 6): probes report the pipeline's current step and its
/// progress. The mapping is fail-closed (a failed blocking step is Faulted) and disclosure-safe
/// (details name steps — framework-authored — never reasons, because the health endpoint is
/// usually the LEAST protected surface a pod has).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Health/StartupPipelineHealthSource.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineHealthSourceTests {

  private static StartupStepDescriptor _step(string name, bool blocking = true) =>
    new() { Name = name, Blocking = blocking };

  private static async Task<StartupPipelineState> _drivenAsync(
      StartupRunPlan plan, params StartupStepResult[] results) {
    var state = new StartupPipelineState();
    await state.OnRunStartingAsync(plan, CancellationToken.None);
    foreach (var result in results) {
      await state.OnStepStartingAsync(new StartupStepContext(_step(result.Name)), CancellationToken.None);
      await state.OnStepCompletedAsync(result, CancellationToken.None);
    }
    return state;
  }

  private static StartupStepResult _completed(string name) =>
    new(name, StartupStepOutcome.Completed, TimeSpan.FromMilliseconds(2), null);

  [Test]
  public async Task NotStarted_ReportsStartingWithAStatedDetailAsync() {
    var source = new StartupPipelineHealthSource(state: new StartupPipelineState(), readySignal: new StartupReadySignal(), NullStartupStageReporter.Instance);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Starting);
    await Assert.That(health.Detail).IsEqualTo("pipeline not started")
      .Because("not-started is a stated condition on every surface, health included");
  }

  [Test]
  public async Task MigrateRunning_ReportsMigratingWithProgressAsync() {
    var state = await _drivenAsync(
      new StartupRunPlan([_step("Assess"), _step(FrameworkStartupSteps.MIGRATE), _step("Reconcile")]),
      _completed("Assess"));
    await state.OnStepStartingAsync(
      new StartupStepContext(_step(FrameworkStartupSteps.MIGRATE)), CancellationToken.None);
    var source = new StartupPipelineHealthSource(state: state, readySignal: new StartupReadySignal(), NullStartupStageReporter.Instance);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Migrating)
      .Because("Migrating is the state operators reason about most — it gets its own answer");
    await Assert.That(health.Detail).IsEqualTo("Migrate (1/3 steps complete)")
      .Because("the question during a slow boot is 'what is it doing right now' — the detail answers it");
  }

  [Test]
  public async Task FailedBlockingStep_ReportsFaulted_NamingTheStepNeverTheReasonAsync() {
    var state = await _drivenAsync(
      new StartupRunPlan([_step(FrameworkStartupSteps.MIGRATE)]),
      new StartupStepResult(FrameworkStartupSteps.MIGRATE, StartupStepOutcome.Failed,
        TimeSpan.FromMilliseconds(2), "42P01: relation tenant_secrets does not exist"));
    var source = new StartupPipelineHealthSource(state: state, readySignal: new StartupReadySignal(), NullStartupStageReporter.Instance);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Faulted)
      .Because("fail-closed: a failed blocking step means this boot never reports ready, and "
             + "health must say so instead of looking merely slow");
    await Assert.That(health.Detail).Contains("Migrate");
    await Assert.That(health.Detail).DoesNotContain("tenant_secrets")
      .Because("reasons originate in exception messages, and the health endpoint is usually the "
             + "least protected surface a pod has");
  }

  [Test]
  public async Task CompositeReady_WithPostReadyStepsRunning_ReportsReadyAndNamesThemAsync() {
    var state = await _drivenAsync(
      new StartupRunPlan([_step(FrameworkStartupSteps.MIGRATE), _step("Rewrite", blocking: false)]),
      _completed(FrameworkStartupSteps.MIGRATE));
    await state.OnStepStartingAsync(new StartupStepContext(_step("Rewrite", blocking: false)), CancellationToken.None);
    var signal = new StartupReadySignal();
    signal.MarkReady();
    var source = new StartupPipelineHealthSource(state, signal, NullStartupStageReporter.Instance);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Ready)
      .Because("post-ready steps never gate readiness — that is what the band means");
    await Assert.That(health.Detail).Contains("Rewrite")
      .Because("but the detail says which post-ready steps are still going");
  }

  [Test]
  public async Task CompositeReady_AllDone_ReportsProgressAsync() {
    var state = await _drivenAsync(
      new StartupRunPlan([_step(FrameworkStartupSteps.MIGRATE)]),
      _completed(FrameworkStartupSteps.MIGRATE));
    var signal = new StartupReadySignal();
    signal.MarkReady();

    var health = await new StartupPipelineHealthSource(state, signal, NullStartupStageReporter.Instance).ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Ready);
    await Assert.That(health.Detail).IsEqualTo("ready (1/1 steps complete)");
  }

  [Test]
  public async Task BlockingDrained_ButCompositeNotSignalled_ReportsStartingNotReadyAsync() {
    var state = await _drivenAsync(
      new StartupRunPlan([_step(FrameworkStartupSteps.MIGRATE)]),
      _completed(FrameworkStartupSteps.MIGRATE));
    var signal = new StartupReadySignal();   // transports have not subscribed yet
    var source = new StartupPipelineHealthSource(state, signal, NullStartupStageReporter.Instance);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Starting)
      .Because("Ready is the COMPOSITE — the pipeline drained but a readiness contributor has "
             + "not answered, and health must not report fully-up before it has");
  }

  // --- the startup stage (#1636) ---------------------------------------------------------------

  [Test]
  public async Task WithAStageEntered_TheDetailLeadsWithTheStagesLineAsync() {
    var state = await _drivenAsync(
      new StartupRunPlan([_step("Assess"), _step(FrameworkStartupSteps.MIGRATE), _step("Reconcile")]),
      _completed("Assess"));
    await state.OnStepStartingAsync(
      new StartupStepContext(_step(FrameworkStartupSteps.MIGRATE)), CancellationToken.None);
    var stages = new Whizbang.Core.Tests.Startup.RecordingStageReporter {
      Current = new StartupStageSnapshot(StartupStage.Migrate, "migration 42 of 231", false, DateTimeOffset.UnixEpoch),
    };
    var source = new StartupPipelineHealthSource(state, new StartupReadySignal(), stages);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Migrating)
      .Because("the stage adds to the detail; what the state means for a probe is unchanged");
    await Assert.That(health.Detail).IsEqualTo("Stage 3 of 7: Migrate - migration 42 of 231; Migrate (1/3 steps complete)")
      .Because("the startup component reports the same line the instance logs");
  }

  [Test]
  public async Task WithAStageEntered_BeforeThePipelineStarts_TheDetailStillLeadsWithItAsync() {
    var stages = new Whizbang.Core.Tests.Startup.RecordingStageReporter {
      Current = new StartupStageSnapshot(StartupStage.Preflight, "host starting", false, DateTimeOffset.UnixEpoch),
    };
    var source = new StartupPipelineHealthSource(new StartupPipelineState(), new StartupReadySignal(), stages);

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Starting);
    await Assert.That(health.Detail).IsEqualTo("Stage 1 of 7: Preflight - host starting; pipeline not started");
  }

  [Test]
  public async Task WithAReporterButNoStageYet_TheDetailIsThePipelinesAloneAsync() {
    var source = new StartupPipelineHealthSource(
      new StartupPipelineState(), new StartupReadySignal(), new Whizbang.Core.Tests.Startup.RecordingStageReporter());

    var health = await source.ReportAsync(CancellationToken.None);

    await Assert.That(health.Detail).IsEqualTo("pipeline not started");
  }
}
