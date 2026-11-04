// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
    /// <summary>Completes on the second sweep: the loop came back around after the first one.</summary>
    public TaskCompletionSource SecondMaintenance { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaintenanceCalls;
    public Exception? MaintenanceThrows { get; init; }
    public Exception? BacklogThrows { get; init; }

    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref MaintenanceCalls) >= 2) {
        SecondMaintenance.TrySetResult();
      }
      FirstMaintenance.TrySetResult();
      return MaintenanceThrows is not null
        ? Task.FromException<IReadOnlyList<MaintenanceResult>>(MaintenanceThrows)
        : Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default)
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
      HousekeepingCoordinator? housekeeping = null,
      Func<TimeSpan, CancellationToken, Task>? delay = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger,
      metrics: null,
      housekeeping: housekeeping) {
      DelayAsync = delay ?? MaintenanceWorker.RealDelayAsync,
    };
    return (worker, logger);
  }

  [Test]
  public async Task ExecuteAsync_SweepThrows_LogsAsRetryableAndKeepsTheLoopAliveAsync() {
    // One bad sweep must not kill maintenance for the life of the process. Proven by the loop reaching its wait after
    // the failure and then running a second tick, each observed on its own signal, with no real interval waited out.
    var coord = new FakeCoordinator { MaintenanceThrows = new InvalidOperationException("sweep blew up") };
    var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var waits = 0;
    var (worker, logger) = _build(coord, delay: (_, ct) => {
      if (Interlocked.Increment(ref waits) > 1) {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);   // after the released wait, idle until stopped
      }
      waiting.TrySetResult();
      return release.Task.WaitAsync(ct);
    });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // The bounds below are hang guards only: each wait is on the transition itself.
    await logger.FirstFailureLogged.Task.WaitAsync(TimeSpan.FromSeconds(60));
    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60));
    release.TrySetResult();
    await coord.SecondMaintenance.Task.WaitAsync(TimeSpan.FromSeconds(60));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    List<LogEntry> failures;
    lock (logger.Entries) {
      failures = [.. logger.Entries.Where(e => e.Exception is not null)];
    }

    // Warning, not Error: a failed tick is retryable and the loop says so on the next interval.
    await Assert.That(failures.Any(e =>
      e.Exception is InvalidOperationException && e.Level == LogLevel.Warning)).IsTrue();
    await Assert.That(coord.MaintenanceCalls).IsGreaterThanOrEqualTo(2)
      .Because("the loop survived the failed sweep and ran the next tick");
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_SettlednessProbeFails_SweepsAnywayAsync() {
    // Unmeasured is not busy. Treating a failed backlog read as "busy" would let one
    // broken query disable cleanup for the life of the process.
    var coord = new FakeCoordinator { BacklogThrows = new InvalidOperationException("probe blew up") };
    var (worker, logger) = _build(coord, new HousekeepingCoordinator());

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsGreaterThanOrEqualTo(1);
    await Assert.That(logger.Entries.Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_SettlednessProbeCanceled_PropagatesAsync() {
    // Cancellation is shutdown, not a probe failure: it must surface rather than be
    // swallowed into "unmeasured" and trigger a sweep during teardown.
    var coord = new FakeCoordinator { BacklogThrows = new OperationCanceledException() };
    var (worker, _) = _build(coord, new HousekeepingCoordinator());

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
        .ThrowsExactly<OperationCanceledException>();

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(0);
  }

  [Test]
  public async Task RunMaintenanceOnceAsync_WithoutHousekeeping_SweepsDirectlyAsync() {
    // A host constructing the worker directly gets prior behaviour; a missing
    // collaborator must never silently switch maintenance off.
    var coord = new FakeCoordinator();
    var (worker, _) = _build(coord, housekeeping: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.MaintenanceCalls).IsEqualTo(1);
  }
}
