// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The host's own lifecycle marks Preflight before any hosted service starts (#1636), and takes the one run-control path
/// before any stops (#2045): the permit closes, held work is finished or released, roles are released, and the path
/// writes the Stopping and Stopped phases (recorded as the Draining and Stopped stages). The host's stop token bounds
/// every step of the path.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageHostLifecycle.cs</code-under-test>
[Category("Startup")]
public class StartupStageHostLifecycleTests {
  private sealed class RecordingRunController : IWhizbangRunController {
    public List<(RunHaltReason Reason, CancellationToken Token)> Halts { get; } = [];
    public WhizbangRunPermit Permit { get; } = new();
    public RunHaltReason? HaltedFor => Halts.Count == 0 ? null : Halts[^1].Reason;
    public IReadOnlyCollection<RunHaltReason> HaltedReasons => [.. Halts.Select(h => h.Reason).Distinct()];
    public Task HaltAsync(RunHaltReason reason, CancellationToken cancellationToken) {
      Halts.Add((reason, cancellationToken));
      return Task.CompletedTask;
    }
    public Task ResumeAsync(RunHaltReason reason, CancellationToken cancellationToken) => Task.CompletedTask;
  }

  [Test]
  public async Task Starting_EntersPreflightAsync() {
    var stages = new RecordingStageReporter();
    using var cts = new CancellationTokenSource();

    await new StartupStageHostLifecycle(stages, new RecordingRunController()).StartingAsync(cts.Token);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Preflight: host starting"]);
    await Assert.That(stages.Tokens[0]).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Stopping_TakesTheRunControlPathToStop_BoundedByTheHostsStopTokenAsync() {
    // #2045: host shutdown is the same path as a pause and a stand-down, with the reason Stop. The host's stop token
    // bounds every step of it, so releasing work and roles against an unreachable database cannot overrun the host's
    // shutdown budget; the path itself still writes the final phase.
    var stages = new RecordingStageReporter();
    var runControl = new RecordingRunController();
    using var hostStop = new CancellationTokenSource();

    await new StartupStageHostLifecycle(stages, runControl).StoppingAsync(hostStop.Token);

    await Assert.That(runControl.Halts.Select(h => h.Reason)).IsEquivalentTo([RunHaltReason.Stop]);
    await Assert.That(runControl.Halts[0].Token).IsEqualTo(hostStop.Token);
    await Assert.That(stages.Calls).IsEmpty().Because("the path writes Stopping and Stopped; nothing else does");
  }

  [Test]
  public async Task Stopped_WritesNothing_ThePathAlreadyConfirmedTheStopAsync() {
    var stages = new RecordingStageReporter();
    var runControl = new RecordingRunController();

    await new StartupStageHostLifecycle(stages, runControl).StoppedAsync(CancellationToken.None);

    await Assert.That(stages.Calls).IsEmpty();
    await Assert.That(runControl.Halts).IsEmpty();
  }

  [Test]
  public async Task TheOtherHostCallbacks_EnterNothingAsync() {
    var stages = new RecordingStageReporter();
    var lifecycle = new StartupStageHostLifecycle(stages, new RecordingRunController());
    // Method groups, so the call reads as what it is: the host's callback, not a worker started by the test.
    Func<CancellationToken, Task> start = lifecycle.StartAsync;
    Func<CancellationToken, Task> started = lifecycle.StartedAsync;
    Func<CancellationToken, Task> stop = lifecycle.StopAsync;

    await start(CancellationToken.None);
    await started(CancellationToken.None);
    await stop(CancellationToken.None);

    await Assert.That(stages.Calls).IsEmpty();
  }

  [Test]
  public async Task Construct_WithoutAReporter_IsRefusedAsync() {
    await Assert.That(() => new StartupStageHostLifecycle(null!, new RecordingRunController())).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageHostLifecycle(new RecordingStageReporter(), null!)).Throws<ArgumentNullException>();
  }
}
