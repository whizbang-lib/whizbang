using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers the perspective-row sweep inside the maintenance cycle: the TTL reap, the
/// row-cap reap behind its own claim, the settled apply-path fold behind its claim, and
/// the after-reap guard callbacks.
/// </summary>
/// <remarks>
/// Each of the three reclaim passes is separately claimed so replicas do not duplicate
/// work, and each is separately failable. The tests exercise the claim gates and the
/// failure handling rather than the SQL, which lives in the driver suites.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerRowSweepTests {

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    public List<LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Entries) { Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception)); }
    }
    public List<LogEntry> Snapshot() { lock (Entries) { return [.. Entries]; } }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class RowSweepCoordinator : IWorkCoordinator {
    public bool GrantRowCapSweep { get; init; } = true;
    public bool GrantSettledFold { get; init; } = true;
    public int TtlRows { get; init; }
    public int CapRows { get; init; }
    public int FoldedPaths { get; init; }
    public Exception? TtlReapThrows { get; init; }

    public int CapReapCalls;
    public int FoldCalls;

    public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(
        int batchSize = 5000, CancellationToken ct = default)
      => TtlReapThrows is not null
        ? Task.FromException<PerspectiveRowReapResult>(TtlReapThrows)
        : Task.FromResult(new PerspectiveRowReapResult(TtlRows, "ok"));

    public Task<bool> TryClaimRowCapSweepAsync(TimeSpan claimWindow, CancellationToken ct = default)
      => Task.FromResult(GrantRowCapSweep);

    public Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(
        int batchSize = 5000, CancellationToken ct = default) {
      Interlocked.Increment(ref CapReapCalls);
      return Task.FromResult(new PerspectiveRowReapResult(CapRows, "ok"));
    }

    public Task<bool> TryClaimSettledFoldSweepAsync(TimeSpan claimWindow, CancellationToken ct = default)
      => Task.FromResult(GrantSettledFold);

    public Task<int> FoldSettledApplyPathsAsync(
        TimeSpan idleWindow, int limit = 1000, CancellationToken ct = default) {
      Interlocked.Increment(ref FoldCalls);
      return Task.FromResult(FoldedPaths);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(RowSweepCoordinator coord) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger);
    return (worker, logger);
  }

  [Test]
  public async Task RowCapSweep_IsSkippedWhenAnotherReplicaHoldsTheClaimAsync() {
    // The cap sweep is claimed separately from the TTL reap so replicas do not both
    // scan every enrolled table in the same window.
    var coord = new RowSweepCoordinator { GrantRowCapSweep = false, TtlRows = 3 };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.CapReapCalls).IsEqualTo(0);
  }

  [Test]
  public async Task RowCapSweep_RunsWhenTheClaimIsGrantedAsync() {
    var coord = new RowSweepCoordinator { GrantRowCapSweep = true, CapRows = 7 };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.CapReapCalls).IsEqualTo(1);
  }

  [Test]
  public async Task SettledFold_IsSkippedWhenAnotherReplicaHoldsTheClaimAsync() {
    var coord = new RowSweepCoordinator { GrantSettledFold = false };
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.FoldCalls).IsEqualTo(0);
  }

  [Test]
  public async Task SettledFold_RunsAndReportsWhatItFoldedAsync() {
    var coord = new RowSweepCoordinator { GrantSettledFold = true, FoldedPaths = 12 };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.FoldCalls).IsEqualTo(1);
    await Assert.That(logger.Snapshot()).IsNotEmpty();
  }

  [Test]
  public async Task SettledFold_FoldingNothing_IsNotReportedAsync() {
    // A fold that moved no rows is the steady state; logging it every cycle would bury
    // the passes that did something.
    var coord = new RowSweepCoordinator { GrantSettledFold = true, FoldedPaths = 0 };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.FoldCalls).IsEqualTo(1);
    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("fold", StringComparison.OrdinalIgnoreCase)))
      .IsFalse();
  }

  [Test]
  public async Task RowSweep_ReportsWhenEitherReapMovedRowsAsync() {
    var coord = new RowSweepCoordinator { TtlRows = 5, CapRows = 0 };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot()).IsNotEmpty();
  }

  [Test]
  public async Task RowSweep_ReapingNothing_IsNotReportedAsync() {
    var coord = new RowSweepCoordinator { TtlRows = 0, CapRows = 0 };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e =>
      e.Message.Contains("ttl", StringComparison.OrdinalIgnoreCase))).IsFalse();
  }

  [Test]
  public async Task RowSweep_WhenTheTtlReapFails_TheCycleStillCompletesAsync() {
    // Row reclamation is housekeeping. A failed reap must not take down the cycle that
    // also sweeps offload claims and records bloat in the same tick.
    var coord = new RowSweepCoordinator { TtlReapThrows = new InvalidOperationException("reap failed") };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }
}
