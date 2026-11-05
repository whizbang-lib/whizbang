// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Startup;
using Whizbang.Data.EFCore.Postgres.Tests.TestSupport;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgCommitOrderStamperWorker"/> lines that
/// <see cref="PgCommitOrderStamperWorkerIntegrationTests"/>'s full leader-election/stamping
/// scenarios never happen to exercise. A commit-order stamper decides the order events are
/// read back in; if the leader loop can silently die on an unhandled iteration failure, or the
/// wake semaphore's saturation guard is wrong, commit-sequence stamping stalls without any
/// caller ever finding out — the failure mode is a silently-corrupted read order, not a crash.
/// </summary>
[Category("Shard1")]
public class PgCommitOrderStamperWorkerCoverageTests {

  /// <summary>Records the event ids a worker logs.</summary>
  private sealed class EventIdRecordingLogger : ILogger<PgCommitOrderStamperWorker> {
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _ids = new();

    public IReadOnlyCollection<int> EventIds => [.. _ids];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => _ids.Enqueue(eventId.Id);
  }

  /// <summary>
  /// Production impact if this guard regresses: a host whose notification options name no database (no connection
  /// string and no registered data source) would try to elect a stamper leader over a connection it cannot open. The
  /// worker instead says why it is disabled, in a warning an operator can act on, and ends without electing.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_WithNoConnectionStringAndNoDataSource_SaysSoAndEndsWithoutElectingAsync(
      CancellationToken testToken) {
    var logger = new EventIdRecordingLogger();
    var worker = _newWorker(directConnectionString: null, logger: logger);

    await worker.StartAsync(testToken);
    // The body returns on its own here, so the execute task completing is the signal that it ran.
    await worker.ExecuteTask!.WaitAsync(testToken);

    await Assert.That(worker.ExecuteTask.Status).IsEqualTo(TaskStatus.RanToCompletion);
    await Assert.That(logger.EventIds).Contains(DISABLED_NO_CONNECTION_EVENT_ID)
      .Because("the worker must say it is disabled for want of a connection, not end silently");
    await Assert.That(worker.IsLeader).IsFalse();

    await worker.StopAsync(testToken);
  }

  private const int DISABLED_NO_CONNECTION_EVENT_ID = 2;

  /// <summary>
  /// #2044: the stamper leads only through a duty elector; there is no lock of its own to fall back to. With none
  /// registered it says so, in a warning an operator can act on, and ends without leading.
  /// </summary>
  [Test]
  [Timeout(30000)]
  [Arguments(false)]
  [Arguments(true)]
  public async Task ExecuteAsync_WithNoDutyElector_SaysSoAndEndsWithoutLeadingAsync(bool theNullDefault, CancellationToken testToken) {
    // No elector at all, or the framework's null default, which is registered when nothing supplies one.
    var logger = new EventIdRecordingLogger();
    var worker = _newWorker(directConnectionString: "Host=stamper-has-no-elector;Database=none", logger: logger,
      withElector: theNullDefault, elector: NullDutyElector.Instance);

    await worker.StartAsync(testToken);
    await worker.ExecuteTask!.WaitAsync(testToken);

    await Assert.That(worker.ExecuteTask.Status).IsEqualTo(TaskStatus.RanToCompletion);
    await Assert.That(logger.EventIds).Contains(NO_DUTY_ELECTOR_EVENT_ID)
      .Because("a stamper that cannot lead must say why rather than end silently");
    await Assert.That(worker.IsLeader).IsFalse();

    await worker.StopAsync(testToken);
  }

  private const int NO_DUTY_ELECTOR_EVENT_ID = 20;

  /// <summary>
  /// Captures only EventId 6 (<c>LogIterationError</c>) so a test can wait for a deterministic
  /// count of failed leader-election iterations instead of sleeping. Every non-cancellation
  /// exception thrown while attempting to acquire the lock lands here.
  /// </summary>
  private sealed class IterationErrorCapturingLogger : ILogger<PgCommitOrderStamperWorker> {
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

  /// <summary>
  /// #2046: the stamper hears commits and its role's release on the signal bus, never on a channel of its own. A commit
  /// wakes the stamping loop; a release of the stamper's role (and only that role) frees a waiting non-holder to vote,
  /// and the same release delivered twice (the notification, then the durable log's replay) is harmless.
  /// </summary>
  [Test]
  public async Task TheBus_WakesTheStamperOnACommit_AndAWaiterOnItsRolesReleaseAsync() {
    var bus = new Whizbang.Core.Signals.SignalBus([new Whizbang.Core.Signals.InMemorySignalTransport()], []);
    await bus.StartAsync();
    var worker = _newWorker(bus: bus);
    var listening = worker.ListenForWakes();
    await Assert.That(await worker.PendingWakes.WaitAsync(0)).IsTrue().Because("the loop starts with one wake pending");

    await bus.PublishAsync(new Whizbang.Core.Signals.RowsCommittedSignal());
    await Assert.That(worker.PendingWakes.CurrentCount).IsEqualTo(1).Because("a commit wakes the stamping loop");
    await bus.PublishAsync(new Whizbang.Core.Signals.RowsCommittedSignal());
    await Assert.That(worker.PendingWakes.CurrentCount).IsEqualTo(1).Because("wakes coalesce");

    await bus.PublishAsync(new Whizbang.Core.Signals.RoleReleasedSignal("whizbang:some-other-role"));
    await Assert.That(worker.Vacancies.CurrentCount).IsEqualTo(0).Because("another role's release is not the stamper's");
    await bus.PublishAsync(new Whizbang.Core.Signals.RoleReleasedSignal(CommitOrderStamperOptions.ROLE));
    await Assert.That(worker.Vacancies.CurrentCount).IsEqualTo(1).Because("the stamper's release frees a waiter to vote");
    await bus.PublishAsync(new Whizbang.Core.Signals.RoleReleasedSignal(CommitOrderStamperOptions.ROLE));
    await Assert.That(worker.Vacancies.CurrentCount).IsEqualTo(1).Because("a release heard twice is one vacancy");

    listening.Dispose();
    await Assert.That(await worker.PendingWakes.WaitAsync(0)).IsTrue();
    await bus.PublishAsync(new Whizbang.Core.Signals.RowsCommittedSignal());
    await Assert.That(worker.PendingWakes.CurrentCount).IsEqualTo(0).Because("a stopped listener wakes nothing");
  }

  private static PgCommitOrderStamperWorker _newWorker(
      string? directConnectionString = null,
      ILogger<PgCommitOrderStamperWorker>? logger = null,
      bool withElector = true,
      IDutyElector? elector = null,
      Whizbang.Core.Signals.ISignalBus? bus = null) {
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
      bus ?? Whizbang.Core.Signals.NullSignalBus.Instance,
      logger ?? NullLogger<PgCommitOrderStamperWorker>.Instance,
      dutyElector: withElector ? elector ?? new GrantingDutyElector() : null);
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
    // OperationCanceledException, so it lands in the generic exception branch and
    // falls through the shared retry-delay tail rather than breaking/continuing past it.
    // Observing a SECOND logged iteration failure proves the outer loop actually looped back
    // around (fell through to its closing brace and re-checked the stopping token) instead of
    // dying on the first one.
    var logger = new IterationErrorCapturingLogger();
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
