// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Testing.Lifecycle;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The host's own lifecycle marks Preflight before any hosted service starts (#1636), and before any stops it completes
/// the one exit path (#2126), which takes the one run-control path (#2045): the permit closes, held work is finished or
/// released, roles are released, the Stopping and Stopped phases are written, and the exit is logged, recorded and given
/// its exit code. The host's stop token bounds every step.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageHostLifecycle.cs</code-under-test>
[Category("Startup")]
public class StartupStageHostLifecycleTests {
  [Test]
  public async Task Starting_EntersPreflight_AndTellsTheExitPathTheHostStartedAsync() {
    var stages = new RecordingStageReporter();
    var exit = new RecordingInstanceExit();
    using var cts = new CancellationTokenSource();

    await new StartupStageHostLifecycle(stages, exit).StartingAsync(cts.Token);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Preflight: host starting"]);
    await Assert.That(stages.Tokens[0]).IsEqualTo(cts.Token);
    await Assert.That(exit.HostStartingCalls).IsEqualTo(1).Because("uptime is measured from the host's start");
  }

  [Test]
  public async Task Stopping_CompletesTheExitPath_OnTheHostsStopTokenAsync() {
    // #2126: every exit ends in the host's stop, and the exit path halts through the run-control path on its token.
    var stages = new RecordingStageReporter();
    var exit = new RecordingInstanceExit();
    using var hostStop = new CancellationTokenSource();

    await new StartupStageHostLifecycle(stages, exit).StoppingAsync(hostStop.Token);

    await Assert.That(exit.HostStoppingCalls).IsEqualTo(1);
    await Assert.That(stages.Calls).IsEmpty().Because("the path writes Stopping and Stopped; nothing else does");
  }

  [Test]
  public async Task Stopped_WritesNothing_ThePathAlreadyConfirmedTheStopAsync() {
    var stages = new RecordingStageReporter();
    var exit = new RecordingInstanceExit();

    await new StartupStageHostLifecycle(stages, exit).StoppedAsync(CancellationToken.None);

    await Assert.That(stages.Calls).IsEmpty();
    await Assert.That(exit.HostStoppingCalls).IsEqualTo(0);
  }

  [Test]
  public async Task TheOtherHostCallbacks_EnterNothingAsync() {
    var stages = new RecordingStageReporter();
    var lifecycle = new StartupStageHostLifecycle(stages, new RecordingInstanceExit());
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
  public async Task Construct_WithoutAReporterOrAnExitPath_IsRefusedAsync() {
    await Assert.That(() => new StartupStageHostLifecycle(null!, new RecordingInstanceExit())).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageHostLifecycle(new RecordingStageReporter(), null!)).Throws<ArgumentNullException>();
  }
}
