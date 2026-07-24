using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// v0.502 slice C.7 — unit tests for <see cref="DeadLetterRecoveryWorker"/>. Uses an
/// in-memory <see cref="IDeadLetterRecoveryService"/> fake so the test exercises the
/// scan loop + policy decisions without a real database.
///
/// <para>
/// The SQL-level concerns (atomic re-emit, idempotency under concurrent races,
/// generation-replay exactly-once) are locked in
/// <see cref="Whizbang.Data.EFCore.Postgres.Tests.DeadLetterRecoverySqlTests"/>. These
/// tests cover the worker's behavior given an honest service.
/// </para>
/// </summary>
[NotInParallel(Order = 300)]
public class DeadLetterRecoveryWorkerTests {

  private sealed class FakeRecoveryService : IDeadLetterRecoveryService {
    public Queue<List<DeadLetterEntry>> FetchBatches { get; } = new();
    public List<Guid> RecoverCalls { get; } = [];
    public List<Guid> HoldCalls { get; } = [];
    public List<Guid> PermanentlyFailedCalls { get; } = [];
    public List<(Guid Id, DateTimeOffset NextAt)> ScheduleCalls { get; } = [];
    public List<string> ResetForGenerationCalls { get; } = [];
    public bool RecoverShouldThrow { get; set; }
    public bool TerminalTransitionShouldThrow { get; set; }
    public bool ScheduleShouldThrow { get; set; }
    public int GenerationReplayReturn { get; set; }
    public TaskCompletionSource FirstFetchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFetchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Fires when RecoverAsync is invoked — lets a test await the actual recovery (which
    // happens AFTER FetchDueAsync returns and the batch is processed), not just the fetch.
    public TaskCompletionSource RecoverSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fetchCount;

    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
      _fetchCount++;
      if (_fetchCount == 1) { FirstFetchSignal.TrySetResult(); } else if (_fetchCount == 2) { SecondFetchSignal.TrySetResult(); }
      var batch = FetchBatches.Count > 0 ? FetchBatches.Dequeue() : [];
      return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(batch);
    }
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
      if (RecoverShouldThrow) { throw new InvalidOperationException("simulated DB failure"); }
      RecoverCalls.Add(deadLetterId);
      RecoverSignal.TrySetResult();
      return Task.FromResult(true);
    }
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      if (TerminalTransitionShouldThrow) { throw new InvalidOperationException("simulated terminal-set failure"); }
      HoldCalls.Add(deadLetterId); return Task.CompletedTask;
    }
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) {
      if (TerminalTransitionShouldThrow) { throw new InvalidOperationException("simulated terminal-set failure"); }
      PermanentlyFailedCalls.Add(deadLetterId); return Task.CompletedTask;
    }
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      if (ScheduleShouldThrow) { throw new InvalidOperationException("simulated schedule failure"); }
      ScheduleCalls.Add((deadLetterId, nextAt)); return Task.CompletedTask;
    }
    public Task<int> ResetForGenerationAsync(string currentGeneration, CancellationToken ct = default) {
      ResetForGenerationCalls.Add(currentGeneration);
      return Task.FromResult(GenerationReplayReturn);
    }
  }

  private sealed class ImmediateSchemaGate : ISchemaReadyGate {
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void MarkReady() { }
    public bool IsReady => true;
  }

  private sealed class FixedGenerationProvider(string value) : IGenerationProvider {
    public string GetGeneration() => value;
  }

  /// <summary>
  /// Test double for the NOTIFY listener. Tracks subscribe/unsubscribe and can raise a
  /// signal of any category so the worker's <c>_onSignal</c> filter can be exercised.
  /// </summary>
  private sealed class FakeNotificationListener : Whizbang.Core.Notifications.IWorkNotificationListener {
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    public int SubscriberCount { get; private set; }
    public event Action<Whizbang.Core.Notifications.WorkSignalCategory>? OnSignal {
      add { _onSignal += value; SubscriberCount++; }
      remove { _onSignal -= value; SubscriberCount--; }
    }
    public event Action<bool>? OnHealthChanged { add { } remove { } }

    private Action<Whizbang.Core.Notifications.WorkSignalCategory>? _onSignal;

    public void Raise(Whizbang.Core.Notifications.WorkSignalCategory category) => _onSignal?.Invoke(category);
  }

  private static DeadLetterEntry _entry(
      MessageFailureReason reason = MessageFailureReason.Throttled,
      int recoveryAttempts = 0,
      DeadLetterRecoveryStatus status = DeadLetterRecoveryStatus.Pending) {
    return new DeadLetterEntry(
      DeadLetterId: Guid.NewGuid(),
      SourceTable: DeadLetterSourceTable.OUTBOX,
      SourceId: Guid.NewGuid(),
      StreamId: null,
      MessageType: "Test.Event",
      FailureReason: reason,
      AttemptsWhenDlq: 10,
      DeadLetteredAt: DateTimeOffset.UtcNow.AddMinutes(-1),
      RecoveryStatus: status,
      RecoveryAttempts: recoveryAttempts,
      Generation: "test/0.0.1");
  }

  private static (DeadLetterRecoveryWorker Worker, FakeRecoveryService Svc) _newWorker(
      DeadLetterRecoveryOptions? options = null,
      string generation = "test/0.0.1",
      FakeNotificationListener? listener = null) {
    var svc = new FakeRecoveryService();
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(new DefaultDeadLetterRecoveryPolicy(Options.Create(options ?? new DeadLetterRecoveryOptions())));
    var sp = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new ImmediateSchemaGate(),
      Options.Create(options ?? new DeadLetterRecoveryOptions { ScanIntervalMinutes = 1, ScanBatchSize = 50 }),
      new FixedGenerationProvider(generation),
      NullLogger<DeadLetterRecoveryWorker>.Instance,
      metrics: null,
      notificationListener: listener);
    return (worker, svc);
  }

  /// <summary>
  /// v0.502 hotfix — regression lock for InMemory integration tests. The worker is
  /// registered by <see cref="WorkerPipelineExtensions.AddWhizbangWorkers"/> in EVERY
  /// host (including in-memory + unit-test hosts that don't wire a persistence layer),
  /// so it must tolerate the absence of <see cref="IDeadLetterRecoveryService"/> in DI
  /// rather than throwing at startup. Without this lock the InMemory sample CI job
  /// failed with "No service for type 'IDeadLetterRecoveryService' has been registered."
  /// </summary>
  [Test]
  public async Task NoRecoveryServiceRegistered_StartsAndStopsCleanlyAsync() {
    var services = new ServiceCollection();
    // Intentionally NOT registering IDeadLetterRecoveryService — only the policy.
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    var sp = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new ImmediateSchemaGate(),
      Options.Create(new DeadLetterRecoveryOptions { ScanIntervalMinutes = 1, ScanBatchSize = 50 }),
      new FixedGenerationProvider("test/0.0.1"),
      NullLogger<DeadLetterRecoveryWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token); // must NOT throw
    await Task.Yield();
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Worker degraded to a no-op — no scans, no replay scheduled.
    await Assert.That(worker.TotalScans).IsEqualTo(0);
    await Assert.That(worker.TotalGenerationReplays).IsEqualTo(0);
  }

  [Test]
  public async Task PendingEntry_RetryableReason_GetsRecoveredAsync() {
    var svc_options = new DeadLetterRecoveryOptions { ScanIntervalMinutes = 1, ScanBatchSize = 50 };
    var (worker, svc) = _newWorker(svc_options);
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Give the scan body a moment to dispatch the policy decision
    await Task.Delay(100);

    await Assert.That(svc.RecoverCalls).Contains(entry.DeadLetterId)
      .Because("a fresh Throttled row should hit the recover path");
    await Assert.That(worker.TotalRecovered).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExhaustedEntry_HoldReviewPolicy_TransitionsHoldingAsync() {
    // ValidationError → HoldForReview policy (MaxRecoveryAttempts=0, HoldForReviewAfterExhaustion=true).
    // recoveryAttempts >= MaxRecoveryAttempts → mark holding.
    var (worker, svc) = _newWorker();
    var entry = _entry(MessageFailureReason.ValidationError, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.HoldCalls).Contains(entry.DeadLetterId)
      .Because("ValidationError policy is HoldForReview with MaxRecoveryAttempts=0 → terminal Holding");
    await Assert.That(svc.RecoverCalls).IsEmpty()
      .Because("exhausted entries must not enter the recover path");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExhaustedEntry_PermanentFailPolicy_TransitionsPermanentlyFailedAsync() {
    // Throttled → AggressiveRetry (Max=3, HoldForReviewAfterExhaustion=false). When
    // recoveryAttempts is already 3, the worker should mark PermanentlyFailed.
    var (worker, svc) = _newWorker();
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 3);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.PermanentlyFailedCalls).Contains(entry.DeadLetterId);
    await Assert.That(svc.HoldCalls).IsEmpty();
    await Assert.That(svc.RecoverCalls).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task RecoveryAttemptThrows_SchedulesNextAttemptAsync() {
    var (worker, svc) = _newWorker();
    svc.RecoverShouldThrow = true;
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.ScheduleCalls).Count().IsEqualTo(1)
      .Because("a recovery exception should result in next-attempt scheduling with policy cooldown");
    var scheduled = svc.ScheduleCalls[0];
    await Assert.That(scheduled.Id).IsEqualTo(entry.DeadLetterId);
    await Assert.That(scheduled.NextAt).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(20))
      .Because("Throttled policy cooldown is 30 min");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Startup_RunsGenerationReplayOnceAsync() {
    var (worker, svc) = _newWorker(generation: "test/v2");
    svc.GenerationReplayReturn = 7;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.ResetForGenerationCalls).IsEquivalentTo(["test/v2"])
      .Because("startup must call ResetForGenerationAsync exactly once with the current generation");
    await Assert.That(worker.TotalGenerationReplays).IsEqualTo(7);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task DisabledWorker_DoesNotScanAsync() {
    var (worker, svc) = _newWorker(new DeadLetterRecoveryOptions {
      Enabled = false,
      ScanIntervalMinutes = 1,
    });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(500);  // would be enough to fire scans if it were enabled

    await Assert.That(svc.ResetForGenerationCalls).IsEmpty();
    await Assert.That(worker.TotalScans).IsEqualTo(0);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task EntryWithHoldForReviewStatus_IsSkippedByPolicyAsync() {
    // ShouldRecover returns false for HoldForReview rows.
    var (worker, svc) = _newWorker();
    var entry = _entry(MessageFailureReason.Throttled, status: DeadLetterRecoveryStatus.HoldForReview);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.RecoverCalls).IsEmpty()
      .Because("HoldForReview rows must never enter the recovery loop even if fetch returns them");
    await Assert.That(svc.HoldCalls).IsEmpty();
    await Assert.That(svc.PermanentlyFailedCalls).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Slice 7c — when a NOTIFY listener is wired, the worker subscribes its <c>_onSignal</c>
  /// handler during ExecuteAsync and unsubscribes it on StopAsync. A DeadLetterReady signal
  /// releases the wake semaphore so the next scan runs without waiting for the poll backstop.
  /// </summary>
  [Test]
  public async Task NotificationListener_DeadLetterReadySignal_WakesAndRescansAsync() {
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(
      new DeadLetterRecoveryOptions { ScanIntervalMinutes = 60, ScanBatchSize = 50 },
      listener: listener);
    // First scan returns nothing; second scan (after the wake) recovers the entry.
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([]);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Handler must be subscribed while running.
    await Assert.That(listener.SubscriberCount).IsEqualTo(1)
      .Because("ExecuteAsync subscribes the DeadLetterReady handler when a listener is wired");

    // Raising a DeadLetterReady signal releases the wake semaphore, ending the poll wait.
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    // Await the recovery itself, not just the fetch start — RecoverAsync runs after the
    // second FetchDueAsync returns and the batch is processed.
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls).Contains(entry.DeadLetterId)
      .Because("the wake-driven rescan must pick up the newly-ready DLQ row");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // StopAsync unsubscribes the handler.
    await Assert.That(listener.SubscriberCount).IsEqualTo(0)
      .Because("StopAsync removes the OnSignal subscription so the worker leaves no dangling handler");
  }

  /// <summary>
  /// The <c>_onSignal</c> filter ignores non-DeadLetterReady categories — an Outbox signal
  /// must NOT release the wake semaphore. We prove this by confirming the handler is wired,
  /// raising an unrelated category, and observing no second scan is triggered by it (the
  /// first scan already happened; the second only occurs once we raise the correct category).
  /// </summary>
  [Test]
  public async Task NotificationListener_UnrelatedCategory_DoesNotWakeAsync() {
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(
      new DeadLetterRecoveryOptions { ScanIntervalMinutes = 60, ScanBatchSize = 50 },
      listener: listener);
    svc.FetchBatches.Enqueue([]);
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Raise an unrelated category — the filter returns early, no wake.
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.Outbox);

    // Now raise the correct category — this one wakes and triggers the second scan.
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    // Await the recovery itself — RecoverAsync runs after the second FetchDueAsync returns.
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls).Contains(entry.DeadLetterId)
      .Because("only the DeadLetterReady signal drives the rescan; the Outbox signal was filtered out");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// When the terminal-state transition (MarkHolding) throws mid-scan, the worker must catch
  /// it, log, and continue — never propagate. Covers the LogTerminalSetFailed catch arm.
  /// </summary>
  [Test]
  public async Task ExhaustedEntry_TerminalTransitionThrows_SwallowsAndContinuesAsync() {
    var (worker, svc) = _newWorker();
    svc.TerminalTransitionShouldThrow = true;
    // ValidationError → HoldForReview policy with MaxRecoveryAttempts=0 → MarkHolding path.
    var entry = _entry(MessageFailureReason.ValidationError, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    // The throw was swallowed — no hold recorded, no recover attempted, worker still alive.
    await Assert.That(svc.HoldCalls).IsEmpty()
      .Because("MarkHolding threw, so no hold was recorded, but the exception must not escape the scan loop");
    await Assert.That(svc.RecoverCalls).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// When recovery throws AND the follow-up ScheduleNextAttempt ALSO throws, the worker must
  /// catch the inner schedule failure and continue. Covers the nested LogScheduleFailed catch.
  /// </summary>
  [Test]
  public async Task RecoveryThrows_AndScheduleAlsoThrows_SwallowsScheduleFailureAsync() {
    var (worker, svc) = _newWorker();
    svc.RecoverShouldThrow = true;
    svc.ScheduleShouldThrow = true;
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    // Both the recover and the schedule threw — neither was recorded, worker survived.
    await Assert.That(svc.RecoverCalls).IsEmpty();
    await Assert.That(svc.ScheduleCalls).IsEmpty()
      .Because("ScheduleNextAttempt threw, so nothing was recorded, but the inner catch must swallow it");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
