using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The stream-integrity convergence gauges are refreshed from the maintenance cycle.
///
/// <para>
/// This started as a dedicated background service on its own 60-second timer, which was the wrong
/// shape: it added another always-on worker acquiring a database connection on a schedule in every
/// host. Small pools feel that immediately — the sample harnesses run with Max Pool Size 2 — and
/// the framework already has a periodic cycle holding a scope and a coordinator. Riding it costs
/// one extra query instead of a new periodic connection.
/// </para>
///
/// <para>
/// What must hold either way: the ledger reading actually reaches the gauges. A gauge that is wired
/// but never fed reports a healthy silence indistinguishable from a healthy system, which is the
/// blindness these replaced report events to fix.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public class MaintenanceWorkerIntegrityGaugeTests {

  private sealed class LedgerCoordinator : IWorkCoordinator {
    public LedgerGaugeSnapshot Snapshot { get; init; } = LedgerGaugeSnapshot.Empty;
    public bool Throw { get; init; }
    /// <summary>Thrown in place of the generic failure, for the cancellation contract.</summary>
    public Exception? ThrowSpecific { get; init; }
    public int MaxAttemptsSeen { get; private set; } = -1;

    public Task<LedgerGaugeSnapshot> GetIntegrityLedgerSummaryAsync(
        int maxRepairAttempts, CancellationToken cancellationToken = default) {
      MaxAttemptsSeen = maxRepairAttempts;
      if (ThrowSpecific is not null) {
        return Task.FromException<LedgerGaugeSnapshot>(ThrowSpecific);
      }
      return Throw
        ? Task.FromException<LedgerGaugeSnapshot>(new InvalidOperationException("ledger unavailable"))
        : Task.FromResult(Snapshot);
    }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static (MaintenanceWorker Worker, StreamIntegrityMetrics Metrics) _build(
      LedgerCoordinator coord, int maxAttempts = 8) {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton(metrics);
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { MaxRepairAttemptsPerBucket = maxAttempts }));
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1, StuckRowSentinelEnabled = false }),
      NullLogger<MaintenanceWorker>.Instance);
    return (worker, metrics);
  }

  [Test]
  public async Task MaintenanceCycle_PublishesTheLedgerReadingToTheGaugesAsync() {
    var (worker, metrics) = _build(new LedgerCoordinator {
      Snapshot = new LedgerGaugeSnapshot {
        UnhealedBuckets = 42,
        RepairExhausted = 7,
        OldestUnhealedAgeSeconds = 9_000,
      },
    });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    var g = metrics.CurrentLedgerGaugesForTest;
    await Assert.That(g.UnhealedBuckets).IsEqualTo(42)
      .Because("an unfed gauge reads as a healthy system — the exact blindness this replaces");
    await Assert.That(g.RepairExhausted).IsEqualTo(7)
      .Because("buckets that have stopped asking for repair are the set needing a human");
    await Assert.That(g.OldestUnhealedAgeSeconds).IsEqualTo(9_000)
      .Because("age separates a transient blip from something stuck");
  }

  [Test]
  public async Task MaintenanceCycle_PassesTheConfiguredRepairBudgetAsync() {
    // The "exhausted" count is meaningless unless the query is told what the budget IS; a
    // hard-coded default would report against the wrong threshold and look plausible.
    var coord = new LedgerCoordinator { Snapshot = LedgerGaugeSnapshot.Empty };
    var (worker, _) = _build(coord, maxAttempts: 3);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaxAttemptsSeen).IsEqualTo(3)
      .Because("the exhausted count must be measured against the operator's configured budget");
  }

  [Test]
  public async Task LedgerReadFailure_DoesNotFailTheMaintenanceCycleAsync() {
    // Reaping and the destruction hooks are correctness work; a metrics read is not. A gauge
    // refresh that could abort the cycle would let an observability detail stop the reaper.
    var (worker, metrics) = _build(new LedgerCoordinator { Throw = true });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(metrics.CurrentLedgerGaugesForTest.UnhealedBuckets).IsEqualTo(0)
      .Because("the reading is simply absent — and the cycle completed rather than throwing");
  }

  [Test]
  public async Task LedgerReadCanceled_StopsTheCycleInsteadOfContinuingAsync() {
    // The companion to the failure case above, and the opposite answer. A metrics read must not
    // abort the cycle when it FAILS — but a canceled read is a stopping host, and the steps that
    // follow include the reap and the sweep, which take locks the completion path needs. The
    // narrow catch above the wide one is what separates the two, and nothing was holding it.
    var (worker, _) = _build(new LedgerCoordinator { ThrowSpecific = new OperationCanceledException() });

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("shutdown has to travel through a best-effort step, or the cycle keeps reaping on "
             + "a host that asked to stop");
  }
}
