using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the maintenance step that reclaims space a table holds but cannot use.
///
/// <para>
/// Churn leaves free space inside pages that autovacuum returns to the free space map but never to
/// the OS, so the file stays large and every scan keeps paying for the empty pages; a dropped
/// column leaves bytes autovacuum can never reclaim at all. Only a rewrite recovers either, and a
/// rewrite takes an ACCESS EXCLUSIVE lock for its duration.
/// </para>
///
/// <para>
/// Two properties matter, and they fail independently: the framework must not take that lock
/// unless the operator permitted it, and it must not clear a recorded request unless the rewrite
/// demonstrably worked — otherwise an interrupted or ineffective rewrite is silently forgotten and
/// the table stays bloated with nothing left to say so.
/// </para>
/// </summary>
/// <docs>operations/observability/metrics#table-statistics</docs>
public class MaintenanceWorkerTableRewriteTests {

  private sealed class RewriteCoordinator : IWorkCoordinator {
    public List<TableRewriteCandidate> Candidates { get; init; } = [];
    /// <summary>Ratio reported after a rewrite; null means the rewrite could not be performed.</summary>
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

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

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
  }

  private static MaintenanceWorker _buildWorker(RewriteCoordinator coord, bool allowRewrite) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions {
        IntervalMinutes = 1,
        StuckRowSentinelEnabled = false,
        AllowTableRewrite = allowRewrite,
      }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task BloatedTable_WithoutOperatorPermission_IsReportedButNotRewrittenAsync() {
    var coord = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_outbox", 4.04, Requested: false) },
    };

    await _buildWorker(coord, allowRewrite: false).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Rewritten).IsEmpty()
      .Because("a rewrite holds an ACCESS EXCLUSIVE lock for its duration and the framework cannot "
               + "know how large a consumer's table is; taking that lock unattended must be opted into");
  }

  [Test]
  public async Task BloatedTable_WithPermission_IsRewrittenAndTheRequestClearedAsync() {
    var coord = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: true) },
      RatioAfterRewrite = 1.0,
    };

    await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Rewritten).Contains("wh_event_store");
    await Assert.That(coord.Cleared).Contains("wh_event_store")
      .Because("a migration-recorded request is satisfied once the rewrite is confirmed to have worked");
  }

  [Test]
  public async Task RewriteThatDoesNotReduceTheRatio_LeavesTheRequestQueuedAsync() {
    var coord = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: true) },
      RatioAfterRewrite = 4.2,   // no improvement
    };

    await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Rewritten).Contains("wh_event_store");
    await Assert.That(coord.Cleared).IsEmpty()
      .Because("clearing a request the rewrite did not satisfy loses the only record that the table "
               + "still owes one — it must stay queued for the next cycle");
  }
}
