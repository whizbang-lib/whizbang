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

    /// <summary>When set, the bloat scan fails instead of returning candidates.</summary>
    public Exception? ScanThrows { get; init; }

    /// <summary>When set, recording a rewrite request fails for every candidate.</summary>
    public Exception? RequestThrows { get; init; }

    public Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(CancellationToken ct = default)
      => ScanThrows is not null
        ? Task.FromException<IReadOnlyList<TableRewriteCandidate>>(ScanThrows)
        : Task.FromResult<IReadOnlyList<TableRewriteCandidate>>(Candidates);

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
      return RequestThrows is not null ? Task.FromException(RequestThrows) : Task.CompletedTask;
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

  // --- Failure paths ---------------------------------------------------------
  // Bloat detection is advisory. Neither a failed scan nor a failed recording may take
  // the maintenance cycle down with it — the sweep that actually reclaims rows runs in
  // the same tick, and losing it to a statistics query would be a poor trade.

  [Test]
  public async Task BloatScanFails_MaintenanceCycleStillCompletesAsync() {
    var coord = new RewriteCoordinator {
      ScanThrows = new InvalidOperationException("pg_stat unavailable"),
    };

    await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Requested).IsEmpty();
  }

  [Test]
  public async Task RecordingARewriteRequestFails_OtherCandidatesAreStillRecordedAsync() {
    // One table failing to record must not skip the rest of the candidate list.
    var coord = new RewriteCoordinator {
      Candidates = {
        new TableRewriteCandidate("wh_event_store", 4.2, Requested: false),
        new TableRewriteCandidate("wh_inbox", 3.9, Requested: false),
      },
      RequestThrows = new InvalidOperationException("write failed"),
    };

    await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Requested).Contains("wh_event_store");
    await Assert.That(coord.Requested).Contains("wh_inbox")
      .Because("the loop continues past a failed recording rather than abandoning the scan");
  }

  [Test]
  public async Task CanceledMidCandidates_StopsWithoutRecordingFurtherTablesAsync() {
    var coord = new RewriteCoordinator {
      Candidates = { new TableRewriteCandidate("wh_event_store", 4.2, Requested: false) },
    };
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    try {
      await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(cts.Token);
    } catch (OperationCanceledException) {
      // Shutdown mid-cycle is expected; the assertion below is what matters.
    }

    await Assert.That(coord.Requested).IsEmpty();
  }

  [Test]
  public async Task BloatScanCanceled_StopsTheCycleInsteadOfContinuingAsync() {
    // The companion to BloatScanFails_MaintenanceCycleStillCompletes. A pg_stat read that fails is
    // no reason to lose the reap that shares the tick — bloat reporting is advisory. A canceled
    // read is a stopping host, and the reap and sweep that follow take the locks the completion
    // path needs.
    var coord = new RewriteCoordinator { ScanThrows = new OperationCanceledException() };

    await Assert.That(async () =>
        await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("advisory work may fail without stopping the cycle, but it may not keep the cycle "
             + "running after the host has been asked to stop");
    await Assert.That(coord.Requested).IsEmpty();
  }

  [Test]
  public async Task RecordingARewriteRequestCanceled_StopsInsteadOfMovingToTheNextTableAsync() {
    // The companion to RecordingARewriteRequestFails_OtherCandidatesAreStillRecorded. One table
    // failing to record must not skip the rest of the list; a cancellation ends the pass, and the
    // candidates that were not reached are re-detected on the next scan — bloat does not go away
    // on its own.
    var coord = new RewriteCoordinator {
      Candidates = {
        new TableRewriteCandidate("wh_event_store", 4.2, Requested: false),
        new TableRewriteCandidate("wh_inbox", 3.9, Requested: false),
      },
      RequestThrows = new OperationCanceledException(),
    };

    await Assert.That(async () =>
        await _buildWorker(coord, allowRewrite: true).RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>();
    await Assert.That(coord.Requested).Count().IsEqualTo(1)
      .Because("the pass stops where the cancellation was seen rather than working through the "
             + "rest of the candidate list on a stopping host");
  }
}
