using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for four <see cref="PgCommitOrderStamperWorker"/> lines that
/// <see cref="PgCommitOrderStamperWorkerIntegrationTests"/>'s full leader-election/stamping
/// scenarios never happen to exercise. A commit-order stamper decides the order events are
/// read back in; if the leader loop can silently die on an unhandled iteration failure, or the
/// wake semaphore's saturation guard is wrong, commit-sequence stamping stalls without any
/// caller ever finding out — the failure mode is a silently-corrupted read order, not a crash.
/// </summary>
[Category("Shard1")]
public class PgCommitOrderStamperWorkerCoverageTests {

  private sealed class _noOpSharedNotifyConnection : ISharedNotifyConnection {
    public IDisposable Subscribe(INotifySubscription subscription) => new _noOpDisposable();
    private sealed class _noOpDisposable : IDisposable {
      public void Dispose() { }
    }
  }

  private sealed class _throwingSchemaReadyGate : Whizbang.Core.Workers.ISchemaReadyGate {
    public bool IsReady => false;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken) =>
      Task.FromException(new OperationCanceledException("schema never became ready"));
  }

  /// <summary>
  /// Captures only EventId 6 (<c>LogIterationError</c>) so a test can wait for a deterministic
  /// count of failed leader-election iterations instead of sleeping. Every non-cancellation
  /// exception thrown while attempting to acquire the lock lands here.
  /// </summary>
  private sealed class _iterationErrorCapturingLogger : ILogger<PgCommitOrderStamperWorker> {
    private const int ITERATION_ERROR_EVENT_ID = 6;
    private readonly Lock _gate = new();
    private int _count;
    private readonly TaskCompletionSource _sawTwo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (eventId.Id != ITERATION_ERROR_EVENT_ID) {
        return;
      }
      lock (_gate) {
        _count++;
        if (_count >= 2) {
          _sawTwo.TrySetResult();
        }
      }
    }

    /// <summary>Completes once a SECOND iteration-error has been logged — proof the leader loop
    /// looped back around after the first failure instead of dying or hanging on it.</summary>
    public Task WaitForTwoIterationErrorsAsync(TimeSpan timeout) => _sawTwo.Task.WaitAsync(timeout);
  }

  private static PgCommitOrderStamperWorker _newWorker(
      string? directConnectionString = null,
      ILogger<PgCommitOrderStamperWorker>? logger = null,
      Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null) {
    var notificationOptions = new WhizbangNotificationOptions {
      DirectConnectionString = directConnectionString,
    };
    var stamperOptions = new CommitOrderStamperOptions {
      LeaderElectionRetry = TimeSpan.FromMilliseconds(20),
      PollingInterval = TimeSpan.FromMilliseconds(20),
    };
    var config = new ConfigurationBuilder().Build();
    return new PgCommitOrderStamperWorker(
      Options.Create(notificationOptions),
      Options.Create(stamperOptions),
      config,
      new _noOpSharedNotifyConnection(),
      logger ?? NullLogger<PgCommitOrderStamperWorker>.Instance,
      schemaReadyGate: schemaReadyGate);
  }

  /// <summary>
  /// Production impact if this getter regresses: operators watching stamping throughput via
  /// <see cref="PgCommitOrderStamperWorker.TotalStamped"/> would silently see a wrong (or
  /// stuck) number, masking a stalled stamper behind an apparently-healthy dashboard.
  /// </summary>
  [Test]
  public async Task TotalStamped_OnAFreshWorker_IsZeroAsync() {
    var worker = _newWorker();

    await Assert.That(worker.TotalStamped).IsEqualTo(0)
      .Because("nothing has stamped yet — a fresh worker's cumulative counter must read zero, not garbage or a stale value");
  }

  /// <summary>
  /// Production impact if this guard regresses: a cancellation raised while the stamper is still
  /// waiting for the schema to finish migrating would otherwise crash the worker (or worse, be
  /// swallowed by the host as a startup failure) instead of shutting down cleanly like every
  /// other cooperative-cancellation path in this loop.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_SchemaGateThrowsOperationCanceled_ShutsDownCleanlyAsync() {
    var worker = _newWorker(schemaReadyGate: new _throwingSchemaReadyGate());

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("the worker must finish (not hang) once the schema-ready wait is canceled");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a canceled schema-ready wait is a clean-shutdown signal, not a fault the host should see as a crash");
    await Assert.That(worker.IsLeader).IsFalse()
      .Because("the worker returned before ever attempting leader election");

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Production impact if this guard regresses: an unhandled <see cref="SemaphoreFullException"/>
  /// from an overlapping NOTIFY wake would tear down the entire leader loop (BackgroundService
  /// failures are fatal to the host), turning "two commits arrived close together" into a total
  /// stamping outage.
  /// </summary>
  [Test]
  public async Task Wake_CalledOnAFreshWorker_SwallowsTheAlreadySaturatedSemaphoreAsync() {
    // The wake semaphore is constructed with initialCount: 1, maxCount: 1 — already saturated —
    // so the very first Wake() call hits the SemaphoreFullException catch without any setup.
    var worker = _newWorker();

    worker.Wake();
    worker.Wake();

    // Reaching here without an unhandled SemaphoreFullException IS the assertion: overlapping
    // wakes must collapse into a single pending signal, not propagate an exception to the caller.
    await Assert.That(worker.IsLeader).IsFalse();
  }

  /// <summary>
  /// Production impact if this fall-through regresses: a leader-election iteration that fails
  /// with a real (non-cancellation) error — e.g. a transient connection failure — must retry
  /// rather than permanently give up. Losing this path would turn a transient Postgres blip into
  /// a stamper that never elects a leader again for the life of the process.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_IterationFailsWithoutCancellation_LoopsBackAndRetriesAsync() {
    // A string with no "key=value" pairs at all fails ADO.NET's own connection-string tokenizer
    // (shared by every DbConnectionStringBuilder-based provider, Npgsql included) the instant
    // `new NpgsqlConnection(...)` parses it — synchronous, in-process, no network round-trip, no
    // dependency on how a sandbox handles loopback sockets. That parse failure is NOT
    // OperationCanceledException, so it lands in the generic `catch (Exception ex)` branch and
    // falls through the shared retry-delay tail rather than breaking/continuing past it.
    // Observing a SECOND logged iteration failure proves the outer loop actually looped back
    // around (fell through to its closing brace and re-checked the stopping token) instead of
    // dying on the first one.
    var logger = new _iterationErrorCapturingLogger();
    var worker = _newWorker(
      directConnectionString: "this is not a valid connection string",
      logger: logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await logger.WaitForTwoIterationErrorsAsync(TimeSpan.FromSeconds(15));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("canceling after the loop has already retried at least once must still shut the worker down cleanly");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a connection that never succeeds must not crash the worker — it must keep retrying until canceled");

    await worker.StopAsync(CancellationToken.None);
  }
}
