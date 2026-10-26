// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The <c>VACUUM FULL</c> strategy behind <see cref="ITableRewriter"/>: today's rewrite, unchanged. It runs
/// only with the operator's permission, delegates the rewrite itself to the work coordinator (which owns
/// the connection, the table-name check and the re-measure), and is what <c>AddWhizbangWorkers</c> wires
/// the <c>Rewrite</c> step to.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/VacuumFullTableRewriter.cs</code-under-test>
[Category("Startup")]
public sealed class VacuumFullTableRewriterTests {

  private sealed class RewriteOnlyCoordinator : IWorkCoordinator {
    public List<(string Table, CancellationToken Token)> Calls { get; } = [];
    public double? RatioAfterRewrite { get; init; }

    public Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) {
      Calls.Add((tableName, cancellationToken));
      return Task.FromResult(RatioAfterRewrite);
    }

    // Unused surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static VacuumFullTableRewriter _rewriter(bool allow) =>
    new(Options.Create(new MaintenanceWorkerOptions { AllowTableRewrite = allow }));

  [Test]
  public async Task Strategy_IsVacuumFullAsync() {
    var strategy = _rewriter(allow: true).Strategy;

    await Assert.That(strategy).IsEqualTo(TableRewriteStrategies.VACUUM_FULL);
    await Assert.That(strategy).IsEqualTo("VacuumFull")
      .Because("the name is the configuration value an operator writes; renaming it breaks their setting");
  }

  [Test]
  public async Task CanRun_FollowsTheOperatorPermissionAsync() {
    await Assert.That(_rewriter(allow: true).CanRun).IsTrue();
    await Assert.That(_rewriter(allow: false).CanRun).IsFalse()
      .Because("an ACCESS EXCLUSIVE lock taken unattended must be opted into");
  }

  [Test]
  public async Task CannotRunReason_NamesTheSwitchThatHoldsItBackAsync() {
    await Assert.That(_rewriter(allow: false).CannotRunReason)
      .IsEqualTo("table rewrites not permitted (MaintenanceWorkerOptions.AllowTableRewrite)");
  }

  [Test]
  public async Task RewriteAsync_DelegatesToTheCoordinatorAndReturnsItsRatioAsync() {
    var coordinator = new RewriteOnlyCoordinator { RatioAfterRewrite = 1.25 };
    using var cts = new CancellationTokenSource();

    var ratio = await _rewriter(allow: true).RewriteAsync(coordinator, "wh_outbox", cts.Token);

    await Assert.That(ratio).IsEqualTo(1.25);
    await Assert.That(coordinator.Calls).Count().IsEqualTo(1);
    await Assert.That(coordinator.Calls[0].Table).IsEqualTo("wh_outbox");
    await Assert.That(coordinator.Calls[0].Token).IsEqualTo(cts.Token)
      .Because("a shutdown during a long VACUUM FULL must reach the command");
  }

  [Test]
  public async Task RewriteAsync_PassesANotRewrittenResultThroughAsync() {
    var coordinator = new RewriteOnlyCoordinator { RatioAfterRewrite = null };

    var ratio = await _rewriter(allow: true).RewriteAsync(coordinator, "wh_outbox", CancellationToken.None);

    await Assert.That(ratio).IsNull()
      .Because("null is how the coordinator says it did not rewrite; turning it into a number would clear the request");
  }

  [Test]
  public async Task Guards_RejectMissingArgumentsAsync() {
    await Assert.That(() => new VacuumFullTableRewriter(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _rewriter(allow: true).RewriteAsync(null!, "wh_outbox", CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheVacuumFullRewriterOnceAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbangWorkers();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var rewriters = provider.GetServices<ITableRewriter>().ToList();

    await Assert.That(rewriters).Count().IsEqualTo(1)
      .Because("AddWhizbangWorkers is idempotent; a second call must not register a second rewriter");
    await Assert.That(rewriters[0]).IsTypeOf<VacuumFullTableRewriter>();
  }

  [Test]
  public async Task AddWhizbangWorkers_TheRewriteStepRunsTheVacuumFullStrategyAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var step = provider.GetServices<IStartupStep>().Single(s => s.Descriptor.Name == FrameworkStartupSteps.REWRITE);
    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason)
      .IsEqualTo("Auto detects and reports only: VACUUM FULL is not permitted (Whizbang:Workers:Maintenance:AllowTableRewrite = false)")
      .Because("by default the step resolves Auto, which rewrites with VacuumFull only when permitted; any other reason "
             + "means it resolved another strategy");
    await Assert.That(step.Descriptor.DependsOn).IsEquivalentTo([FrameworkStartupSteps.MIGRATE]);
  }

  [Test]
  public async Task AddWhizbangWorkers_WithCapabilities_TheRewriteStepRunsAfterTheCapabilitiesStepAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:AllowTableRewrite"] = "true",
    }).Build());
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangCapabilities();
    await using var provider = services.BuildServiceProvider();

    var step = provider.GetServices<IStartupStep>().Single(s => s.Descriptor.Name == FrameworkStartupSteps.REWRITE);
    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(step.Descriptor.DependsOn).Contains(CapabilitiesStartupStep.STEP_NAME);
    await Assert.That(report.Reason).IsEqualTo("no work coordinator registered")
      .Because("Auto with AllowTableRewrite resolves VacuumFull, which runs and finds no coordinator in this container");
  }
}
