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
/// Report-only is bilateral: while <see cref="StreamIntegrityOptions.RepairMode"/> is
/// <see cref="IntegrityRepairMode.ReportOnly"/> the maintenance cycle discards parked repair rows
/// (re-delivery requests and bundles) through
/// <see cref="IWorkCoordinator.DiscardPendingInboxMessagesAsync"/>; under
/// <see cref="IntegrityRepairMode.AutoRepairCapped"/> it leaves them for the repair path. The sweep is
/// best-effort like every other maintenance step: a failing sweep is logged with its consequence and
/// the cycle continues.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerRepairTrafficSweepTests {
  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    private readonly List<LogEntry> _entries = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_entries) { _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception)); }
    }
    public List<LogEntry> Snapshot() { lock (_entries) { return [.. _entries]; } }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class SweepCoordinator : IWorkCoordinator {
    public Exception? DiscardThrows { get; init; }
    public long Discarded { get; init; }
    public List<IReadOnlyList<string>> DiscardCalls { get; } = [];
    public int MaintenanceCalls;

    public Task<long> DiscardPendingInboxMessagesAsync(
        IReadOnlyList<string> messageTypeNames, CancellationToken cancellationToken = default) {
      lock (DiscardCalls) { DiscardCalls.Add(messageTypeNames); }
      return DiscardThrows is not null ? Task.FromException<long>(DiscardThrows) : Task.FromResult(Discarded);
    }
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref MaintenanceCalls);
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
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

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      SweepCoordinator coord, StreamIntegrityOptions? integrity) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (integrity is not null) {
      services.AddSingleton(Options.Create(integrity));
    }
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
  public async Task ReportOnly_DiscardsParkedRepairRowsAndLogsTheCountAsync() {
    var coord = new SweepCoordinator { Discarded = 3 };
    var (worker, logger) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.DiscardCalls.Count).IsEqualTo(1);
    await Assert.That(coord.DiscardCalls[0]).IsEquivalentTo(RepairTraffic.InboxMessageTypeNames)
      .Because("the sweep names exactly the repair types, never detection traffic");
    var entry = logger.Snapshot().FirstOrDefault(e =>
      e.Level == LogLevel.Information && e.Message.Contains("Discarded 3 parked", StringComparison.Ordinal));
    await Assert.That(entry).IsNotNull().Because("an operator must see repair rows being dropped and why");
    await Assert.That(entry!.Message).Contains("ReportOnly");
  }

  [Test]
  public async Task AutoRepairCapped_LeavesParkedRepairRowsForTheRepairPathAsync() {
    var coord = new SweepCoordinator { Discarded = 3 };
    var (worker, _) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.DiscardCalls).IsEmpty()
      .Because("an opted-in service retries its repair traffic; the sweep must not race the repair path");
  }

  [Test]
  public async Task NoIntegrityOptionsRegistered_SweepsAsTheReportOnlyDefaultAsync() {
    var coord = new SweepCoordinator();
    var (worker, _) = _build(coord, integrity: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.DiscardCalls.Count).IsEqualTo(1)
      .Because("absent options read as report-only; a bundle broadcast by a peer still lands here and must not be folded in");
  }

  [Test]
  public async Task NothingParked_LogsNothingAsync() {
    var coord = new SweepCoordinator { Discarded = 0 };
    var (worker, logger) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.DiscardCalls.Count).IsEqualTo(1);
    await Assert.That(logger.Snapshot().Any(e => e.Message.Contains("parked", StringComparison.Ordinal))).IsFalse()
      .Because("a quiet sweep is the steady state; logging it every cycle is noise");
  }

  [Test]
  public async Task SweepFailing_DoesNotFailTheCycleAndNamesTheConsequenceAsync() {
    var coord = new SweepCoordinator { DiscardThrows = new InvalidOperationException("boom") };
    var (worker, logger) = _build(coord, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly });

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(1)
      .Because("the sweep is best-effort; the rest of the cycle still runs");
    var entry = logger.Snapshot().FirstOrDefault(e =>
      e.Level == LogLevel.Warning && e.Message.Contains("sweep failed", StringComparison.Ordinal));
    await Assert.That(entry).IsNotNull();
    await Assert.That(entry!.Exception).IsNotNull();
    await Assert.That(entry.Message).Contains("next cycle")
      .Because("the log states what failed and what happens to the parked rows as a result");
  }
}
