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
/// Since the startup pipeline landed, the runtime cycle only DETECTS and RECORDS: executing a
/// rewrite takes an ACCESS EXCLUSIVE lock, and mid-traffic was always the wrong window. The
/// post-ready <c>Rewrite</c> step performs the recorded requests under the maintainer duty —
/// see <c>TableRewriteStartupStepTests</c> for the execution semantics.
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

    public Task RequestTableRewriteAsync(string tableName, CancellationToken ct = default) {
      Requested.Add(tableName);
      return Task.CompletedTask;
    }
    public List<string> Requested { get; } = [];

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
  public async Task BloatedTable_EvenWithPermission_IsNeverRewrittenMidTraffic_OnlyRecordedAsync() {
    var coord = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: false) },
    };

    await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Rewritten).IsEmpty()
      .Because("the runtime maintenance cycle stops executing rewrites — an ACCESS EXCLUSIVE lock "
             + "mid-traffic was always the wrong window; the post-ready Rewrite step performs them "
             + "in the window they should have had");
    await Assert.That(coord.Requested).Contains("wh_event_store")
      .Because("detection records the request so the next boot's Rewrite step picks it up "
             + "deterministically instead of depending on the bloat still measuring over threshold");
  }

  [Test]
  public async Task BloatedTable_AlreadyRecorded_IsNotReRecordedAsync() {
    var coord = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: true) },
    };

    await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Rewritten).IsEmpty();
    await Assert.That(coord.Requested).IsEmpty()
      .Because("a request already on the books needs no second recording");
  }
}
