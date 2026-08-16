using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 8: requested table rewrites are a post-ready step — fleet-exclusive (maintainer
/// duty), non-blocking with respect to Ready, deliberately unbounded — instead of an ACCESS
/// EXCLUSIVE lock taken on the runtime maintenance cadence mid-traffic. Execution stays behind
/// the same operator permission, and a request is cleared only after the rewrite demonstrably
/// worked.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/TableRewriteStartupStep.cs</code-under-test>
[Category("Startup")]
public class TableRewriteStartupStepTests {

  private sealed class RewriteCoordinator : IWorkCoordinator {
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    public double? RatioAfterRewrite { get; init; } = 1.0;
    public List<string> Rewritten { get; } = [];
    public List<string> Cleared { get; } = [];

    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<TableRewriteCandidate>>(Candidates);
    public Task<double?> RewriteTableAsync(string tableName, CancellationToken ct = default) {
      Rewritten.Add(tableName);
      return Task.FromResult(RatioAfterRewrite);
    }
    public Task ClearTableRewriteRequestAsync(string tableName, CancellationToken ct = default) {
      Cleared.Add(tableName);
      return Task.CompletedTask;
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
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static TableRewriteStartupStep _stepOver(RewriteCoordinator coordinator, bool allow = true) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var provider = services.BuildServiceProvider();
    return new TableRewriteStartupStep(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { AllowTableRewrite = allow }));
  }

  [Test]
  public async Task Descriptor_DeclaresThePostReadyMaintainerShapeAsync() {
    var descriptor = _stepOver(new RewriteCoordinator()).Descriptor;

    await Assert.That(descriptor.Name).IsEqualTo(FrameworkStartupSteps.REWRITE);
    await Assert.That(descriptor.RequiredCapability).IsEqualTo(StartupDuties.MAINTAINER)
      .Because("one instance rewrites — exclusivity follows the capability the step requires");
    await Assert.That(descriptor.NonHolderBehavior).IsEqualTo(NonHolderBehavior.Skip)
      .Because("nobody blocks on a VACUUM FULL");
    await Assert.That(descriptor.Blocking).IsFalse()
      .Because("deliberately unbounded work must never gate Ready");
    await Assert.That(descriptor.DependsOn).Contains(FrameworkStartupSteps.MIGRATE);
  }

  [Test]
  public async Task WithoutOperatorPermission_SkipsWithTheStatedReasonAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 4.0, Requested: true) },
    };
    var report = await _stepOver(coordinator, allow: false).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(coordinator.Rewritten).IsEmpty()
      .Because("the framework cannot know how large a consumer's table is; taking an ACCESS "
             + "EXCLUSIVE lock unattended must be opted into — same permission, new window");
  }

  [Test]
  public async Task WithNothingOwed_SkipsWithNoRewritesOwedAsync() {
    var report = await _stepOver(new RewriteCoordinator()).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).IsEqualTo("no rewrites owed")
      .Because("'found nothing to do' is a stated fact, distinct from 'not permitted to look'");
  }

  [Test]
  public async Task WithPermission_RewritesAndClearsOnlyConfirmedRequestsAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = {
        new TableRewriteCandidate("wh_event_store", 4.2, Requested: true),
        new TableRewriteCandidate("wh_outbox", 3.5, Requested: false),
      },
      RatioAfterRewrite = 1.0,
    };
    var report = await _stepOver(coordinator).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(coordinator.Rewritten).Contains("wh_event_store");
    await Assert.That(coordinator.Rewritten).Contains("wh_outbox");
    await Assert.That(coordinator.Cleared).Contains("wh_event_store")
      .Because("a migration-recorded request is satisfied once the rewrite is confirmed to have worked");
    await Assert.That(coordinator.Cleared).DoesNotContain("wh_outbox")
      .Because("a detector-found candidate has no recorded request to clear");
    await Assert.That(report.Reason).IsEqualTo("rewrote 2 table(s)");
  }

  [Test]
  public async Task IneffectiveRewrite_LeavesTheRequestQueuedForTheNextBootAsync() {
    var coordinator = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: true) },
      RatioAfterRewrite = 4.2,   // no improvement
    };
    var report = await _stepOver(coordinator).ExecuteAsync(CancellationToken.None);

    await Assert.That(coordinator.Cleared).IsEmpty()
      .Because("clearing a request the rewrite did not satisfy loses the only record that the "
             + "table still owes one");
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(report.Reason).Contains("left queued");
  }
}
