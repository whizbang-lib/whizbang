using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Covers <see cref="TableRewriteStartupStep.ExecuteAsync"/>'s "no work coordinator registered"
/// branch — every scenario in <c>TableRewriteStartupStepTests</c> registers an
/// <see cref="IWorkCoordinator"/> via its shared <c>_stepOver</c> helper, so the partial-host case
/// (permission granted, but nothing to rewrite against) is never reached there.
/// </summary>
[Category("Startup")]
public class TableRewriteStartupStepCoverageTests {

  /// <summary>
  /// If this early return were dropped, a host with the operator permission granted but no
  /// IWorkCoordinator registered (a partial composition, or one assembled in an unusual order)
  /// would NullReferenceException out of the step instead of reporting a legible Skipped reason —
  /// turning a benign "nothing to rewrite against here" into a startup crash.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_PermittedButNoWorkCoordinatorRegistered_SkipsWithTheStatedReasonAsync() {
    var services = new ServiceCollection(); // deliberately no IWorkCoordinator registration
    var provider = services.BuildServiceProvider();
    var step = new TableRewriteStartupStep(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { AllowTableRewrite = true }));

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo("no work coordinator registered")
      .Because("a missing coordinator is a composition gap, not a rewrite failure — the step must "
             + "say exactly that rather than throwing or reporting a misleading Failed/Completed");
  }
}
