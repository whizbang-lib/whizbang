// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Coverage round 23 — targets <see cref="StartupReadyService"/>'s private
/// <c>_describePipeline</c> helper as a unit: what an operator sees narrated while
/// <see cref="StartupReadyService.StartedAsync"/> is still blocked waiting on the pipeline itself
/// (as opposed to a registered <see cref="IStartupReadinessContributor"/>, which is narrated by a
/// different describe delegate and already covered elsewhere).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupReadiness.cs</code-under-test>
[Category("Startup")]
public class StartupReadinessCoverageTests {

  private static StartupStepDescriptor _step(string name, bool blocking = true) =>
    new() { Name = name, Blocking = blocking };

  /// <summary>
  /// If <c>_describePipeline</c> stops naming the specific pending BLOCKING step (and its status),
  /// an operator staring at a hung deploy sees only "still waiting on the startup pipeline" with
  /// nothing to bisect against — exactly the silent-hang failure mode issue #493 exists to prevent.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StartedAsync_BlockingStepStillPending_NarratesTheStepNameAndStatusAsync(
      CancellationToken cancellationToken) {
    var state = new StartupPipelineState();
    await state.OnRunStartingAsync(new StartupRunPlan([_step("Migrate")]), cancellationToken);
    var logger = new ReadyServiceLog();
    var clock = new ProbeClock();
    var service = new StartupReadyService(pipelineState: state, signal: new StartupReadySignal(), contributors: [], logger: logger, timeProvider: clock);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Each probe fires only when the clock is moved to it; the narration is awaited, not polled for.
    var started = service.StartedAsync(cts.Token);
    await clock.DriveUntilAsync(logger.Logged("Migrate (Pending)"), cancellationToken);
    // The first narration is at the third probe, and the clock moved only as far as this test moved it.
    await Assert.That(logger.First("Migrate (Pending)")).Contains($"after {(service.WaitProbeInterval * 3).TotalSeconds:F0}s")
      .Because("the probes and the elapsed time both run on the injected clock, never on real time");

    await Assert.That(logger.Entries.Any(e => e.Contains("startup pipeline step(s) Migrate (Pending)"))).IsTrue()
      .Because("the pending BLOCKING step's name and status must appear verbatim in the narration, or a " +
               "hung deploy gives an operator nothing to act on beyond \"still waiting\"");

    await cts.CancelAsync();
    try { await started; } catch (OperationCanceledException) { /* fail-closed, as designed */ }
  }

  /// <summary>
  /// A run driven without ever announcing its plan (an observer sees <c>OnStepStartingAsync</c>
  /// directly, with no preceding <c>OnRunStartingAsync</c>) leaves <c>HasRunStarted</c> true but the
  /// planned-step list empty — readiness then never fires for this run at all. If the "no pending
  /// steps" description regresses (throws, or mislabels this as "no run has started"), an operator
  /// gets a misleading diagnosis for a host that is stuck for a structurally different reason
  /// (missing plan announcement) than "hasn't started" or "a step won't finish".
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StartedAsync_RunStartedWithoutAPlanAnnouncement_NarratesTheEmptyPipelineAsync(
      CancellationToken cancellationToken) {
    var state = new StartupPipelineState();
    await state.OnStepStartingAsync(new StartupStepContext(_step("SomeStep")), cancellationToken);
    var logger = new ReadyServiceLog();
    var clock = new ProbeClock();
    var service = new StartupReadyService(pipelineState: state, signal: new StartupReadySignal(), contributors: [], logger: logger, timeProvider: clock);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var started = service.StartedAsync(cts.Token);
    await clock.DriveUntilAsync(logger.Logged("waiting on the startup pipeline."), cancellationToken);
    // The first narration is at the third probe, and the clock moved only as far as this test moved it.
    await Assert.That(logger.First("waiting on the startup pipeline.")).Contains($"after {(service.WaitProbeInterval * 3).TotalSeconds:F0}s")
      .Because("the probes and the elapsed time both run on the injected clock, never on real time");

    await Assert.That(logger.Entries.Any(e => e.Contains("waiting on the startup pipeline."))).IsTrue()
      .Because("HasRunStarted=true with zero planned steps must describe itself as \"the startup pipeline\" " +
               "(no pending step to name) rather than crashing the narration or mislabeling it as " +
               "\"no run has started yet\" — those are different diagnoses for an operator");

    await cts.CancelAsync();
    try { await started; } catch (OperationCanceledException) { /* fail-closed, as designed */ }
  }
}
