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

  /// <summary>
  /// A gate that never opens and announces the arrival of a waiter. <see cref="Entered"/> is the
  /// deterministic "ExecuteAsync is parked at the barrier" signal: on .NET 10 the base class
  /// dispatches ExecuteAsync with <c>Task.Run</c>, so <c>StartAsync</c> returning proves only
  /// that the body was scheduled. The infinite delay then observes the stopping token exactly as
  /// the real gate's wait does.
  /// </summary>
  private sealed class BlockingGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
  }

  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      FakeCoordinator coord,
      ISchemaReadyGate gate,
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
  public async Task ExecuteAsync_CanceledWhileWaitingOnTheSchemaGate_StopsWithoutSweepingAsync() {
    // Shutdown during startup must not be treated as an error, and must not run a sweep
    // against a schema that was never confirmed ready.
    //
    // The gate reports when the body actually parks on it, and the test cancels only after that.
    // Canceling straight after StartAsync would race the thread pool: a work item dequeued after
    // the token is already canceled never invokes ExecuteAsync at all, and every assertion below
    // would then be satisfied by a worker that simply never ran.
    var coord = new FakeCoordinator();
    var gate = new BlockingGate();  // deliberately never marked ready
    var (worker, logger) = _build(coord, gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await gate.Entered.WaitAsync(_wait);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    List<LogEntry> entries;
    lock (logger.Entries) {
      entries = [.. logger.Entries];
    }

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(0)
      .Because("a sweep issued before the gate opened runs DDL-dependent work against a schema "
             + "nobody confirmed exists");
    await Assert.That(entries.Any(e => e.Level == LogLevel.Error)).IsFalse()
      .Because("stopping during migrations is an ordinary deploy, not a crash to report");
    await Assert.That(entries.Any(e => e.Message.Contains("stopped", StringComparison.Ordinal))).IsFalse()
      .Because("the canceled wait must take the early return; reaching the loop's exit log means "
             + "the catch fell through into the maintenance loop instead of ending the run");
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("a canceled gate wait must settle the hosted service rather than hang shutdown");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("the cancellation belongs to shutdown, not to a fault");
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
  public async Task RunMaintenanceOnceAsync_SettlednessProbeCanceled_PropagatesAsync() {
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
