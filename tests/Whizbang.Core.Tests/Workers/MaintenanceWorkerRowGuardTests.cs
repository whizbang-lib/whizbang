using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers the row-destruction guard offer path: rows about to be reaped are offered to a
/// registered guard, and its per-row verdict decides whether each one is released for
/// deletion, held for a while, or held indefinitely.
/// </summary>
/// <remarks>
/// This is the last say before perspective rows are deleted, so the mapping from verdict
/// to hold is exactly the part worth pinning. A row with no verdict defaults to a defer,
/// not a proceed — silence must never read as consent to delete.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerRowGuardTests {

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

  private sealed class GuardedModel;

  private sealed class GuardCoordinator : IWorkCoordinator {
    public List<PerspectiveRowDestructionTarget> AboutToReap { get; init; } = [];
    public List<PerspectiveRowRef> Released { get; } = [];
    public List<(PerspectiveRowRef Row, DateTimeOffset Until)> Held { get; } = [];
    public int FailuresRecorded;

    public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
        IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken ct = default)
      => Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>(AboutToReap);

    public Task ReleasePerspectiveRowHoldsAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken ct = default) {
      lock (Released) { Released.AddRange(rows); }
      return Task.CompletedTask;
    }

    public Task HoldPerspectiveRowDestructionAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, DateTimeOffset holdUntil, CancellationToken ct = default) {
      lock (Held) { Held.AddRange(rows.Select(r => (r, holdUntil))); }
      return Task.CompletedTask;
    }

    public Task<int> RecordPerspectiveRowDestructionFailureAsync(
        IReadOnlyCollection<PerspectiveRowRef> rows, TimeSpan retryBackoff, int maxRetries,
        OnDestroyFailure onDestroyFailure, CancellationToken ct = default) {
      Interlocked.Increment(ref FailuresRecorded);
      return Task.FromResult(1);
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

  private sealed class StubGuard(
      Func<PerspectiveRowDestructionTarget, PerspectiveRowDecision?>? verdict = null,
      Exception? beforeThrows = null,
      Exception? afterThrows = null) : IPerspectiveRowDestructionGuard {
    public IReadOnlyCollection<Type> GuardedModels => [typeof(GuardedModel)];
    public int AfterCalls;

    public ValueTask<IReadOnlyDictionary<Guid, PerspectiveRowDecision>> OnBeforeReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> targets, CancellationToken ct = default) {
      if (beforeThrows is not null) {
        return ValueTask.FromException<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(beforeThrows);
      }
      var map = new Dictionary<Guid, PerspectiveRowDecision>();
      foreach (var t in targets) {
        if (verdict?.Invoke(t) is { } d) {
          map[t.RowId] = d;
        }
      }
      return ValueTask.FromResult<IReadOnlyDictionary<Guid, PerspectiveRowDecision>>(map);
    }

    public ValueTask OnAfterReapAsync(
        IReadOnlyList<PerspectiveRowDestructionTarget> released, CancellationToken ct = default) {
      Interlocked.Increment(ref AfterCalls);
      return afterThrows is not null ? ValueTask.FromException(afterThrows) : ValueTask.CompletedTask;
    }
  }

  private static PerspectiveRowDestructionTarget _target(Guid? id = null) => new(
    typeof(GuardedModel).FullName!,
    "wh_per_guarded",
    id ?? Guid.CreateVersion7(),
    null,
    JsonDocument.Parse("{}").RootElement,
    "ttl");

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      GuardCoordinator coord, IPerspectiveRowDestructionGuard? guard) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    if (guard is not null) {
      services.AddSingleton(guard);
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
  public async Task WithNoGuardsRegistered_NoRowsAreOfferedAsync() {
    // Without a guard there is nothing to ask, so the offer path must not even query for
    // candidate rows.
    var coord = new GuardCoordinator { AboutToReap = { _target() } };
    var (worker, _) = _build(coord, guard: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Released).IsEmpty();
    await Assert.That(coord.Held).IsEmpty();
  }

  [Test]
  public async Task ProceedVerdict_ReleasesTheRowForDeletionAsync() {
    var id = Guid.CreateVersion7();
    var coord = new GuardCoordinator { AboutToReap = { _target(id) } };
    var guard = new StubGuard(_ => PerspectiveRowDecision.Proceed());
    var (worker, _) = _build(coord, guard);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Released.Select(r => r.RowId)).Contains(id);
    await Assert.That(guard.AfterCalls).IsEqualTo(1)
      .Because("rows it released were then deleted, so the guard is told");
  }

  [Test]
  public async Task CancelVerdict_HoldsTheRowIndefinitelyAsync() {
    // Cancel is not "try later" — there is no later time at which the answer changes on
    // its own, so the hold is DateTimeOffset.MaxValue.
    var id = Guid.CreateVersion7();
    var coord = new GuardCoordinator { AboutToReap = { _target(id) } };
    var (worker, _) = _build(coord, new StubGuard(_ => PerspectiveRowDecision.Cancel()));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Released).IsEmpty();
    await Assert.That(coord.Held.Any(h => h.Row.RowId == id && h.Until == DateTimeOffset.MaxValue)).IsTrue();
  }

  [Test]
  public async Task DeferVerdict_HoldsTheRowUntilTheRequestedTimeAsync() {
    var id = Guid.CreateVersion7();
    var until = DateTimeOffset.UtcNow.AddHours(3);
    var coord = new GuardCoordinator { AboutToReap = { _target(id) } };
    var (worker, _) = _build(coord, new StubGuard(_ => PerspectiveRowDecision.Defer(until)));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Held.Any(h => h.Row.RowId == id && h.Until == until)).IsTrue();
  }

  [Test]
  public async Task ARowTheGuardSaidNothingAbout_IsDeferredNotDeletedAsync() {
    // The default matters more than the explicit verdicts: a guard that returns an
    // incomplete map must not have its silence read as consent to delete.
    var id = Guid.CreateVersion7();
    var coord = new GuardCoordinator { AboutToReap = { _target(id) } };
    var (worker, _) = _build(coord, new StubGuard(_ => null));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Released).IsEmpty();
    await Assert.That(coord.Held.Any(h => h.Row.RowId == id)).IsTrue();
  }

  [Test]
  public async Task MixedVerdicts_AreEachRoutedSeparatelyAsync() {
    var proceed = _target();
    var cancel = _target();
    var defer = _target();
    var until = DateTimeOffset.UtcNow.AddHours(2);
    var coord = new GuardCoordinator { AboutToReap = { proceed, cancel, defer } };
    var guard = new StubGuard(t =>
      t.RowId == proceed.RowId ? PerspectiveRowDecision.Proceed()
      : t.RowId == cancel.RowId ? PerspectiveRowDecision.Cancel()
      : PerspectiveRowDecision.Defer(until));
    var (worker, _) = _build(coord, guard);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Released.Select(r => r.RowId)).Contains(proceed.RowId);
    await Assert.That(coord.Held.Any(h => h.Row.RowId == cancel.RowId && h.Until == DateTimeOffset.MaxValue)).IsTrue();
    await Assert.That(coord.Held.Any(h => h.Row.RowId == defer.RowId && h.Until == until)).IsTrue();
  }

  [Test]
  public async Task GuardThrowing_RecordsAFailureAndDeletesNothingAsync() {
    // A guard that cannot answer is not permission to delete.
    var coord = new GuardCoordinator { AboutToReap = { _target() } };
    var (worker, logger) = _build(coord, new StubGuard(beforeThrows: new InvalidOperationException("guard failed")));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Released).IsEmpty();
    await Assert.That(coord.FailuresRecorded).IsEqualTo(1);
    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task AfterReapCallbackThrowing_IsLoggedNotPropagatedAsync() {
    // The rows are already gone; failing the cycle would lose the rest of the pass over
    // a notification that cannot be retried usefully.
    var coord = new GuardCoordinator { AboutToReap = { _target() } };
    var guard = new StubGuard(
      _ => PerspectiveRowDecision.Proceed(),
      afterThrows: new InvalidOperationException("after-reap failed"));
    var (worker, logger) = _build(coord, guard);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Exception is InvalidOperationException)).IsTrue();
  }

  [Test]
  public async Task AGuardCancelledDuringShutdown_StopsTheCycleInsteadOfBeingLoggedAsync() {
    // The post-reap hook is best-effort — a throwing observer is logged and ignored, because a
    // guard's bookkeeping must never block destruction. Cancellation is the exception, and the
    // catch that makes it one is FILTERED on the token: an OperationCanceledException with no
    // cancellation behind it is still just a failing observer. Only a real shutdown travels.
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();
    var (worker, logger) = _build(
      new GuardCoordinator { AboutToReap = { _target() } },
      new StubGuard(_ => PerspectiveRowDecision.Proceed(), afterThrows: new OperationCanceledException()));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the rows are already released at this point; continuing runs the rest of the "
             + "cycle on a host that asked to stop");
    await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning)).IsFalse()
      .Because("a shutdown logged as a guard failure is noise on every deploy, and it hides the "
             + "observer errors this log exists to surface");
  }

  [Test]
  public async Task AGuardThrowingCancellationWithNoShutdown_IsTreatedAsAFailingObserverAsync() {
    // The other side of the filter. Without a cancelled token this is just an observer that threw
    // an unfortunate exception type, and swallowing it is correct: destruction already happened,
    // and failing the cycle over bookkeeping would stop the reaper.
    var (worker, logger) = _build(
      new GuardCoordinator { AboutToReap = { _target() } },
      new StubGuard(_ => PerspectiveRowDecision.Proceed(), afterThrows: new OperationCanceledException()));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning)).IsTrue()
      .Because("no shutdown means no reason to stop — the observer failed and that is what the "
             + "wide catch is for");
  }
}
