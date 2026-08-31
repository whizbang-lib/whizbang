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
/// Covers the MaintenanceWorker loop's own failure handling — the paths around the
/// sweep rather than inside it: shutting down while the schema gate is still closed,
/// a sweep that throws, and a settledness probe that fails.
/// </summary>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerLifecycleTests {

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    public List<LogEntry> Entries { get; } = [];

    /// <summary>Completes when the loop first logs a failure carrying an exception, so a
    /// test can wait on the handler having actually run instead of racing cancellation
    /// against it. Note the level is Warning, not Error — the worker treats a failed tick
    /// as retryable and says so on the next interval.</summary>
    public TaskCompletionSource FirstFailureLogged { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
      }
      if (exception is not null) {
        FirstFailureLogged.TrySetResult();
      }
    }

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Implements the six abstract members of IWorkCoordinator and overrides only what a
  /// test needs; every other capability keeps its inherited default.
  /// </summary>
  private sealed class FakeCoordinator : IWorkCoordinator {
    public TaskCompletionSource FirstMaintenance { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaintenanceCalls;
    public Exception? MaintenanceThrows { get; init; }
    public Exception? BacklogThrows { get; init; }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken ct = default) {
      Interlocked.Increment(ref MaintenanceCalls);
      FirstMaintenance.TrySetResult();
      return MaintenanceThrows is not null
        ? Task.FromException<IReadOnlyList<MaintenanceResult>>(MaintenanceThrows)
        : Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken ct = default)
      => BacklogThrows is not null
        ? ValueTask.FromException<ServiceBacklog?>(BacklogThrows)
        : ValueTask.FromResult<ServiceBacklog?>(new ServiceBacklog());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
        => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
  }

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      FakeCoordinator coord,
      SchemaReadyGate gate,
      HousekeepingCoordinator? housekeeping = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger,
      metrics: null,
      housekeeping: housekeeping);
    return (worker, logger);
  }

  [Test]
  public async Task ExecuteAsync_CancelledWhileWaitingOnTheSchemaGate_StopsWithoutSweepingAsync() {
    // Shutdown during startup must not be treated as an error, and must not run a sweep
    // against a schema that was never confirmed ready.
    var coord = new FakeCoordinator();
    var gate = new SchemaReadyGate();  // deliberately never marked ready
    var (worker, logger) = _build(coord, gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(0);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error)).IsFalse();
  }

  [Test]
  public async Task ExecuteAsync_SweepThrows_LogsAsRetryableAndKeepsTheLoopAliveAsync() {
    // One bad sweep must not kill maintenance for the life of the process.
    var coord = new FakeCoordinator { MaintenanceThrows = new InvalidOperationException("sweep blew up") };
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var (worker, logger) = _build(coord, gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await logger.FirstFailureLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    List<LogEntry> failures;
    lock (logger.Entries) {
      failures = logger.Entries.Where(e => e.Exception is not null).ToList();
    }

    // Warning, not Error: a failed tick is retryable and the loop says so on the next interval.
    await Assert.That(failures.Any(e =>
      e.Exception is InvalidOperationException && e.Level == LogLevel.Warning)).IsTrue();
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_SettlednessProbeFails_SweepsAnywayAsync() {
    // Unmeasured is not busy. Treating a failed backlog read as "busy" would let one
    // broken query disable cleanup for the life of the process.
    var coord = new FakeCoordinator { BacklogThrows = new InvalidOperationException("probe blew up") };
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var (worker, logger) = _build(coord, gate, new HousekeepingCoordinator());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.Entries.Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_SettlednessProbeCancelled_PropagatesAsync() {
    // Cancellation is shutdown, not a probe failure: it must surface rather than be
    // swallowed into "unmeasured" and trigger a sweep during teardown.
    var coord = new FakeCoordinator { BacklogThrows = new OperationCanceledException() };
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var (worker, _) = _build(coord, gate, new HousekeepingCoordinator());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
        .ThrowsExactly<OperationCanceledException>();

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(0);
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_WithoutHousekeeping_SweepsDirectlyAsync() {
    // A host constructing the worker directly gets prior behaviour; a missing
    // collaborator must never silently switch maintenance off.
    var coord = new FakeCoordinator();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var (worker, _) = _build(coord, gate, housekeeping: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(1);
  }
}
