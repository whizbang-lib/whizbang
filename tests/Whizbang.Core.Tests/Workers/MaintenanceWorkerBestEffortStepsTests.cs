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
/// Covers the best-effort steps folded into the maintenance cycle — the debug-retention
/// sync and the ancient-pointer prune.
/// </summary>
/// <remarks>
/// These ride an existing scope and cadence rather than each acquiring their own
/// connection on a timer, which is why they are here at all. The trade only holds if none
/// of them can fail the cycle they ride in, so each is separately caught. Nothing was
/// testing that.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerBestEffortStepsTests {

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    private readonly List<LogEntry> _entries = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception)); }
    }
    public List<LogEntry> Snapshot() { lock (_entries) { return [.. _entries]; } }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class StepCoordinator : IWorkCoordinator {
    public Exception? RetentionSyncThrows { get; init; }
    public Exception? PointerPruneThrows { get; init; }
    public long PointersPruned { get; init; }
    public int RetentionSyncCalls;
    public bool? LastDebugMode { get; private set; }

    public Task SyncDebugRetentionSettingAsync(bool debugMode, CancellationToken ct = default) {
      Interlocked.Increment(ref RetentionSyncCalls);
      LastDebugMode = debugMode;
      return RetentionSyncThrows is not null ? Task.FromException(RetentionSyncThrows) : Task.CompletedTask;
    }

    public Task<EphemeralPointerPruneResult> PruneAncientEphemeralPointersAsync(CancellationToken ct = default)
      => PointerPruneThrows is not null
        ? Task.FromException<EphemeralPointerPruneResult>(PointerPruneThrows)
        : Task.FromResult(new EphemeralPointerPruneResult(PointersPruned, "ok"));

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

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(StepCoordinator coord) {
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
  public async Task DebugRetention_IsSyncedEveryCycleAsync() {
    // Synced per cycle rather than at startup so a configuration change takes effect
    // without a restart, and so a stale value cannot outlive the option that set it.
    var coord = new StepCoordinator();
    var (worker, _) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);
    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.RetentionSyncCalls).IsEqualTo(2);
    await Assert.That(coord.LastDebugMode).IsNotNull();
  }

  [Test]
  public async Task DebugRetentionSyncFailing_DoesNotFailTheCycleAsync() {
    // The sweep decides from the stored setting, so a failed write just means it keeps
    // the previous value — not a reason to lose the whole cycle.
    var coord = new StepCoordinator {
      RetentionSyncThrows = new InvalidOperationException("settings write failed"),
    };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task PointerPrune_ReportsWhatItRemovedAsync() {
    var coord = new StepCoordinator { PointersPruned = 42 };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot()).IsNotEmpty();
  }

  [Test]
  public async Task PointerPrune_RemovingNothing_IsNotReportedAsync() {
    // Zero is the steady state; logging it every cycle would bury the passes that moved.
    var coord = new StepCoordinator { PointersPruned = 0 };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e =>
      e.Message.Contains("prune", StringComparison.OrdinalIgnoreCase))).IsFalse();
  }

  [Test]
  public async Task PointerPruneFailing_DoesNotFailTheCycleAsync() {
    var coord = new StepCoordinator {
      PointerPruneThrows = new InvalidOperationException("prune failed"),
    };
    var (worker, logger) = _build(coord);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }
}
