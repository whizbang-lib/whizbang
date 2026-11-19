// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch backfill for <see cref="AssessStartupStep"/>: built by hand with no logger, the step falls
/// back to a null logger so logging the verdict cannot fail the step.
/// </summary>
[Category("Startup")]
public class AssessStartupStepBranchCoverageTests {

  private sealed class FixedAssessor(StartupAssessment assessment) : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) =>
      Task.FromResult(assessment);
  }

  [Test]
  public async Task ExecuteAsync_ConstructedWithoutALogger_StillReportsTheVerdictAsync() {
    var step = new AssessStartupStep(
      assessor: new FixedAssessor(new StartupAssessment(StartupVerdict.StandDown, "a newer schema is in place")),
      logger: null!);

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Failed)
      .Because("the verdict is logged to the null-logger fallback and the stand-down still fails closed");
    await Assert.That(report.Reason).IsEqualTo("a newer schema is in place");
  }
}
