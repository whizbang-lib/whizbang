// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2508: the end-to-end startup tests' hang detector fails on a hang, never on how busy the machine is. A phase waits on
/// its own signal for as long as it takes; only the whole test running past its deadline is a hang, and that names the
/// phase it stopped in. Driven on a fake clock, so each case is decided by construction.
/// </summary>
[Category("Shard4")]
public class StartupPhasesTests {
  [Test]
  public async Task RunAsync_APhaseSlowerThanAnyOnePhaseWasAllowed_FinishesWhenItsSignalDoesAsync() {
    var time = new FakeTimeProvider();
    var phases = new StartupPhases(time);
    var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var run = phases.RunAsync("second start", () => signal.Task);
    // A loaded server: the phase's signal comes long after the 60 s one phase used to be allowed, well inside the test.
    time.Advance(TimeSpan.FromSeconds(150));
    signal.SetResult();

    await run;
    await Assert.That(run.IsCompletedSuccessfully).IsTrue()
      .Because("a phase that is slow, not hung, finishes when its signal does");
  }

  [Test]
  [Timeout(30000)]
  public async Task RunAsync_PhasesTogetherPastTheTestsDeadline_NameThePhaseThatHungAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var phases = new StartupPhases(time);

    await phases.RunAsync("first start", () => {
      time.Advance(StartupPhases.TestDeadline - TimeSpan.FromSeconds(1));
      return Task.CompletedTask;
    });
    var never = new TaskCompletionSource();
    var hung = phases.RunAsync("second start", () => never.Task, () => "Startup stage: [Converge]");
    time.Advance(TimeSpan.FromSeconds(1));

    // The fake clock has run past the deadline, so the phase has failed; the test's token only bounds this test itself.
    var thrown = await Assert.That(async () => await hung.WaitAsync(cancellationToken)).Throws<TimeoutException>();
    await Assert.That(thrown!.Message).Contains("Phase 'second start'")
      .Because("the deadline is one for the whole test, and the phase it ran out in is named");
    await Assert.That(thrown.Message).Contains("Startup stage: [Converge]")
      .Because("with where it stopped");
  }

  [Test]
  public async Task RunAsync_APhaseCanceledByTheTestItself_IsNotReportedAsAHangAsync() {
    var phases = new StartupPhases(new FakeTimeProvider());

    await Assert.That(async () => await phases.RunAsync("first stop", () => Task.FromCanceled(new CancellationToken(canceled: true))))
      .Throws<OperationCanceledException>()
      .Because("a cancellation the phase's own work raised is the test's, not the hang detector's, and passes through as it is");
  }
}
