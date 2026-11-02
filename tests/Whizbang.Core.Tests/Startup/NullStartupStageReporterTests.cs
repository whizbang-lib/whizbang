// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The reporter of a host that records no stages (#1636): it is never in a stage, and nothing it is told changes that.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/NullStartupStageReporter.cs</code-under-test>
[Category("Startup")]
public class NullStartupStageReporterTests {
  [Test]
  public async Task NothingItIsTold_PutsItInAStageAsync() {
    var reporter = NullStartupStageReporter.Instance;

    await reporter.EnterAsync(StartupStage.Running, "serving");
    await reporter.SkipAsync(StartupStage.Draining, "never");
    await reporter.InstanceRegisteredAsync();
    await ((IStartupStageReporter)reporter).EnterPhaseAsync(Whizbang.Core.RunControl.LifecyclePhase.Paused);

    await Assert.That(reporter.Current).IsNull();
    await Assert.That(NullStartupStageReporter.Instance).IsSameReferenceAs(reporter);
  }
}
