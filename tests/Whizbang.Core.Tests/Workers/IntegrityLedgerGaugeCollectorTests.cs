using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the replacement for per-sighting divergence reports.
///
/// <para>
/// Publishing an event per divergence had no consumer, and each report minted its own stream that
/// no cursor would advance past — so the consumption-gated reaper could never collect them and the
/// tables the work pump scans grew without bound. The state those events described is what the
/// ledger already holds; what was missing was a way to SEE it. These tests assert the ledger
/// actually reaches the gauges, because a gauge that is wired but never fed reports a healthy
/// silence indistinguishable from a healthy system.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public class IntegrityLedgerGaugeCollectorTests {

  private sealed class LedgerCoordinator : IWorkCoordinator {
    public LedgerGaugeSnapshot Snapshot { get; init; } = LedgerGaugeSnapshot.Empty;
    public int MaxAttemptsSeen { get; private set; } = -1;
    public int Calls { get; private set; }

    public Task<LedgerGaugeSnapshot> GetIntegrityLedgerSummaryAsync(
        int maxRepairAttempts, CancellationToken cancellationToken = default) {
      MaxAttemptsSeen = maxRepairAttempts;
      Calls++;
      return Task.FromResult(Snapshot);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) =>
      throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static (IntegrityLedgerGaugeCollector Collector, StreamIntegrityMetrics Metrics, LedgerCoordinator Coord)
      _build(LedgerGaugeSnapshot snapshot, int maxAttempts = 8) {
    var coord = new LedgerCoordinator { Snapshot = snapshot };
    var sp = new ServiceCollection().AddSingleton<IWorkCoordinator>(coord).BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var collector = new IntegrityLedgerGaugeCollector(
      sp.GetRequiredService<IServiceScopeFactory>(),
      metrics,
      Options.Create(new StreamIntegrityOptions { MaxRepairAttemptsPerBucket = maxAttempts }),
      gate);
    return (collector, metrics, coord);
  }

  [Test]
  public async Task PublishesTheLedgerReadingToTheGaugesAsync() {
    var (collector, metrics, coord) = _build(new LedgerGaugeSnapshot {
      UnhealedBuckets = 42,
      RepairExhausted = 7,
      OldestUnhealedAgeSeconds = 9_000,
    });

    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    LedgerGaugeSnapshot? whenSignalled = null;
    collector.CycleCompleted += () => {
      // Sampled inside the handler, so this also pins the ordering: a signal raised before the
      // gauges were written would read Empty here regardless of how continuations are scheduled.
      whenSignalled = metrics.CurrentLedgerGaugesForTest;
      cycled.TrySetResult();
    };

    using var cts = new CancellationTokenSource();
    await collector.StartAsync(cts.Token);
    await cycled.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(whenSignalled!.UnhealedBuckets).IsEqualTo(42)
      .Because("an unfed gauge reads as a healthy system, which is exactly the blindness this replaces");
    await Assert.That(whenSignalled.RepairExhausted).IsEqualTo(7)
      .Because("buckets that stopped asking for repair are the set that needs a human");
    await Assert.That(whenSignalled.OldestUnhealedAgeSeconds).IsEqualTo(9_000)
      .Because("age separates a transient blip from something stuck");

    await cts.CancelAsync();
    try { await collector.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  [Test]
  public async Task PassesTheConfiguredRepairBudgetToTheQueryAsync() {
    // The "exhausted" count is meaningless unless the query is told what the budget IS — a
    // hard-coded default would silently report against the wrong threshold.
    var (collector, _, coord) = _build(LedgerGaugeSnapshot.Empty, maxAttempts: 3);

    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    collector.CycleCompleted += () => cycled.TrySetResult();

    using var cts = new CancellationTokenSource();
    await collector.StartAsync(cts.Token);
    await cycled.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(coord.MaxAttemptsSeen).IsEqualTo(3)
      .Because("the exhausted count must be measured against the operator's configured budget");

    await cts.CancelAsync();
    try { await collector.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  [Test]
  public async Task WithoutACoordinator_ExitsInsteadOfSpinningAsync() {
    // Engines with no coordinator registered have nothing to read; the loop should end rather than
    // wake up forever to ask a question no one can answer.
    var sp = new ServiceCollection().BuildServiceProvider();
    var collector = new IntegrityLedgerGaugeCollector(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StreamIntegrityMetrics(new WhizbangMetrics()),
      Options.Create(new StreamIntegrityOptions()));

    using var cts = new CancellationTokenSource();
    await collector.StartAsync(cts.Token);
    await collector.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(collector.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("no coordinator means no ledger — retrying on a timer forever is pure noise");
  }
}
