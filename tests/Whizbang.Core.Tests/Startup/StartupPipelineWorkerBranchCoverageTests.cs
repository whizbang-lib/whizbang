// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch coverage for <see cref="StartupPipelineWorker"/>'s constructor: a worker built without a
/// logger falls back to a no-op logger, so the abandoned-run backstop still runs and still keeps
/// the host up.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineHosting.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineWorkerBranchCoverageTests {

  private sealed class UnorderableStep : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Dependent",
      DependsOn = ["NoSuchStepWasEverDeclared"],
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken)
      => new(new StartupStepReport(StartupStepOutcome.Completed));
  }

  /// <summary>
  /// The backstop logs the abandoned run at Critical. With no logger supplied, that log call is
  /// made against the fallback, and the run's failure must still be absorbed rather than
  /// escape ExecuteAsync and stop the host.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task Worker_WithNullLogger_StillAbsorbsAnAbandonedRunAsync(CancellationToken cancellationToken) {
    // A step depending on a name nothing declares cannot be ordered, so the run throws before any step.
    var runner = new StartupPipelineRunner(steps: [new UnorderableStep()], observers: [], dutyElector: NullDutyElector.Instance);
    using var worker = new StartupPipelineWorker(runner, logger: null!);

    await worker.StartAsync(cancellationToken);
    var execute = worker.ExecuteTask!;
    await execute.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(execute.IsCompletedSuccessfully).IsTrue()
      .Because("the run threw from order resolution; the backstop must log it through the fallback "
             + "logger and complete normally, never fault and stop the host");

    await worker.StopAsync(cancellationToken);
  }
}
