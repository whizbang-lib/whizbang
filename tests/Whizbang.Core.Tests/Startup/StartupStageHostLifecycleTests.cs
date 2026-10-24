// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The host's own lifecycle marks three stages (#1636): Preflight before any hosted service starts, Draining before
/// any stops, Stopped once all have. The stop-side writes do not take the host's stop token, whose budget may already
/// be spent, so a late stop is still recorded and never turned into an exception.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageHostLifecycle.cs</code-under-test>
[Category("Startup")]
public class StartupStageHostLifecycleTests {
  [Test]
  public async Task Starting_EntersPreflightAsync() {
    var stages = new RecordingStageReporter();
    using var cts = new CancellationTokenSource();

    await new StartupStageHostLifecycle(stages).StartingAsync(cts.Token);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Preflight: host starting"]);
    await Assert.That(stages.Tokens[0]).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Stopping_EntersDraining_EvenWhenTheStopBudgetIsSpentAsync() {
    var stages = new RecordingStageReporter();
    using var spent = new CancellationTokenSource();
    await spent.CancelAsync();

    await new StartupStageHostLifecycle(stages).StoppingAsync(spent.Token);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Draining: host stopping, finishing in-flight work"]);
    await Assert.That(stages.Tokens[0].CanBeCanceled).IsFalse()
      .Because("the write is bounded by the reporter, not by a stop token that may already have expired");
  }

  [Test]
  public async Task Stopped_EntersStopped_EvenWhenTheStopBudgetIsSpentAsync() {
    var stages = new RecordingStageReporter();
    using var spent = new CancellationTokenSource();
    await spent.CancelAsync();

    await new StartupStageHostLifecycle(stages).StoppedAsync(spent.Token);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Stopped: host stopped"]);
    await Assert.That(stages.Tokens[0].CanBeCanceled).IsFalse();
  }

  [Test]
  public async Task TheOtherHostCallbacks_EnterNothingAsync() {
    var stages = new RecordingStageReporter();
    var lifecycle = new StartupStageHostLifecycle(stages);
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
    await Assert.That(() => new StartupStageHostLifecycle(null!)).Throws<ArgumentNullException>();
  }
}
