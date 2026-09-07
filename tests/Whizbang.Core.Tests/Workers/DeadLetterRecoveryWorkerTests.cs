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

  private sealed class FakeRecoveryService : IDeadLetterRecoveryService, IWorkCoordinator {
    public Queue<List<DeadLetterEntry>> FetchBatches { get; } = new();
    public List<Guid> RecoverCalls { get; } = [];
    public List<Guid> HoldCalls { get; } = [];
    public List<Guid> PermanentlyFailedCalls { get; } = [];
    public List<(Guid Id, DateTimeOffset NextAt)> ScheduleCalls { get; } = [];
    public List<string> ResetForGenerationCalls { get; } = [];
    public bool RecoverShouldThrow { get; set; }
    // recover_dead_letter returned false: the row was already terminal or another worker's
    // atomic UPDATE claimed it first. Defaults false so every existing test keeps seeing the
    // "always succeeds unless it throws" behavior.
    public bool RecoverShouldReturnFalse { get; set; }
    public bool TerminalTransitionShouldThrow { get; set; }
    public bool ScheduleShouldThrow { get; set; }
    public bool ResetForGenerationShouldThrow { get; set; }
    public bool CountServiceBacklogShouldThrow { get; set; }
    public bool DiscardShouldThrow { get; set; }
    public int GenerationReplayReturn { get; set; }
    public TaskCompletionSource FirstFetchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFetchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Fires when RecoverAsync is invoked — lets a test await the actual recovery (which
    // happens AFTER FetchDueAsync returns and the batch is processed), not just the fetch.
    public TaskCompletionSource RecoverSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fetchCount;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource> _fetchSignals = new();

    /// <summary>Completes once the Nth (1-based) fetch has happened, so a test can drive successive
    /// scans deterministically instead of waiting on the poll interval.</summary>
    public Task FetchSignal(int ordinal) =>
      _fetchSignals.GetOrAdd(ordinal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public System.Collections.Concurrent.ConcurrentQueue<int> FetchedBatchSizes { get; } = new();

    /// <summary>Fails the first scan so a test can check the loop outlives one.</summary>
    public bool FetchThrowsOnFirstCall { get; set; }
    // Blocks a fetch call indefinitely (respecting ct) so a test can cancel while a scan is
    // genuinely in flight, rather than racing a sleep against the loop. Defaults false so
    // every existing test's fetch returns immediately, unchanged.
    public bool BlockFetchUntilCanceled { get; set; }
    public TaskCompletionSource FetchStartedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
      FetchedBatchSizes.Enqueue(maxCount);
      _fetchCount++;
      _fetchSignals.GetOrAdd(_fetchCount, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
      if (_fetchCount == 1) { FirstFetchSignal.TrySetResult(); } else if (_fetchCount == 2) { SecondFetchSignal.TrySetResult(); }
      if (FetchThrowsOnFirstCall && _fetchCount == 1) {
        throw new InvalidOperationException("simulated scan failure");
      }
      if (BlockFetchUntilCanceled) {
        FetchStartedSignal.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
      }
      var batch = FetchBatches.Count > 0 ? FetchBatches.Dequeue() : [];
      return batch;
    }
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
      if (RecoverShouldThrow) { throw new InvalidOperationException("simulated DB failure"); }
      RecoverCalls.Add(deadLetterId);
      RecoverSignal.TrySetResult();
      return Task.FromResult(!RecoverShouldReturnFalse);
    }
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      if (TerminalTransitionShouldThrow) { throw new InvalidOperationException("simulated terminal-set failure"); }
      HoldCalls.Add(deadLetterId); return Task.CompletedTask;
    }
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) {
      if (TerminalTransitionShouldThrow) { throw new InvalidOperationException("simulated terminal-set failure"); }
      PermanentlyFailedCalls.Add(deadLetterId); return Task.CompletedTask;
    }
    public List<(Guid Id, string Note)> DiscardCalls { get; } = [];
    public Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) {
      if (DiscardShouldThrow) { throw new InvalidOperationException("simulated discard failure"); }
      DiscardCalls.Add((deadLetterId, note)); return Task.CompletedTask;
    }
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      if (ScheduleShouldThrow) { throw new InvalidOperationException("simulated schedule failure"); }
      ScheduleCalls.Add((deadLetterId, nextAt)); return Task.CompletedTask;
    }
    public ServiceBacklog? Backlog { get; set; }
    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken ct = default) {
      if (CountServiceBacklogShouldThrow) { throw new InvalidOperationException("simulated backlog-count failure"); }
      return ValueTask.FromResult(Backlog);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default)
      => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    // Campaign surface (P1) — inert defaults; campaign behavior is locked by
    // DeadLetterCanaryCampaignTests with its dedicated scripted fake.
    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>([]);
    public Task<int> RecordStacksAsync(IReadOnlyList<(Guid, Whizbang.Core.DeadLetters.StackIdentity)> entries, CancellationToken ct = default) => Task.FromResult(entries.Count);
    public Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<HeldCohort>>([]);
    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(new CanaryVerdict(CanaryVerdictKind.Pass, 0, 0, 0));
    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) =>
      Task.FromResult(0);
    public List<string> PassedFingerprints { get; set; } = [];
    public List<string> PassedFingerprintQueries { get; } = [];
    public Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) {
      PassedFingerprintQueries.Add(generation);
      return Task.FromResult<IReadOnlyList<string>>([.. PassedFingerprints]);
    }

    public Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) {
      if (ResetForGenerationShouldThrow) { throw new InvalidOperationException("simulated generation-replay failure"); }
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
      DeadLetterRecoveryStatus status = DeadLetterRecoveryStatus.Pending,
      string? fingerprint = null) {
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
      Generation: "test/0.0.1",
      ErrorFingerprint: fingerprint);
  }

  [Test]
  public async Task ExhaustedEntry_FingerprintPassedThisGeneration_RetriesInsteadOfHoldingAsync() {
    // Issue #681: MaxAttemptsExceeded → ConservativeRetry (Max=1, HoldForReviewAfterExhaustion).
    // The row is exhausted — but its fingerprint's canary campaign PASSED on this generation.
    // The verdict is standing evidence that the cohort is safe on this build: the row must be
    // re-driven, not quarantined. (Holding it was the accumulate-forever half of #681 — the
    // one-shot release retired the campaign while the scan kept holding 200 rows a cycle.)
    var (worker, svc) = _newWorker();
    svc.PassedFingerprints = ["fp-passed"];
    var entry = _entry(MessageFailureReason.MaxAttemptsExceeded, recoveryAttempts: 1, fingerprint: "fp-passed");
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls).Contains(entry.DeadLetterId)
      .Because("a Pass verdict for the current generation grants the fresh attempt the "
             + "exhaustion check would otherwise deny");
    await Assert.That(svc.HoldCalls).IsEmpty()
      .Because("re-holding a proven-safe cohort inverts the canary's purpose");
    await Assert.That(svc.PassedFingerprintQueries).Contains("test/0.0.1")
      .Because("the verdict is generation-scoped: evidence about THIS build only");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExhaustedEntry_FingerprintNotPassed_StillHoldsAsync() {
    // Control for the bypass: no Pass verdict → the exhaustion quarantine stands unchanged.
    var (worker, svc) = _newWorker();
    var entry = _entry(MessageFailureReason.MaxAttemptsExceeded, recoveryAttempts: 1, fingerprint: "fp-unproven");
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.HoldCalls).Contains(entry.DeadLetterId)
      .Because("without a Pass verdict the exhaustion hold is the correct quarantine");
    await Assert.That(svc.RecoverCalls).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExhaustedEntry_NoFingerprint_HoldsWithoutQueryingVerdictsAsync() {
    // A row with no fingerprint has no cohort and no campaign — the bypass must not even
    // ask, or every legacy unfingerprinted row costs a query per scan.
    var (worker, svc) = _newWorker();
    var entry = _entry(MessageFailureReason.MaxAttemptsExceeded, recoveryAttempts: 1, fingerprint: null);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.HoldCalls).Contains(entry.DeadLetterId);
    await Assert.That(svc.PassedFingerprintQueries).IsEmpty()
      .Because("no fingerprint, no lookup — the fast path must stay fast");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task DisabledSubsystemEntry_PoisonRow_IsDiscardedNotHeldAsync() {
    // Issue #684: PoisonRedeliveryLoop policy is ("HoldForReview", MaxAttempts: 0) — the
    // exhaustion check re-holds the row BEFORE any dispatch, so the inbox-gate discard
    // (#664) can never see it. A dead letter for a DISABLED subsystem must be settled by
    // the recovery worker itself, ahead of the exhaustion check, or it is undisposable
    // forever (observed live: a released cohort of checkpoint rows cycled straight back
    // to Held with zero dispatches).
    var (worker, svc) = _newWorker(
      integrity: new Whizbang.Core.Messaging.StreamIntegrityOptions { CheckpointsEnabled = false });
    var entry = _entry(MessageFailureReason.PoisonRedeliveryLoop, recoveryAttempts: 0)
      with { MessageType = "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core" };
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.DiscardCalls.Select(d => d.Id)).Contains(entry.DeadLetterId)
      .Because("the subsystem is off: the message has no meaning, and settling it is the "
             + "only disposal a before-dispatch quarantine can ever reach");
    await Assert.That(svc.HoldCalls).IsEmpty()
      .Because("holding garbage for review recreates the invisible-inventory problem");
    await Assert.That(svc.RecoverCalls).IsEmpty()
      .Because("re-driving a disabled subsystem's message is never the answer — the "
             + "policy comment on reason 18 is right about that");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task DisabledSubsystemEntry_SubsystemEnabled_QuarantinesNormallyAsync() {
    // Control: checkpoints ON — the same row follows the reason-18 policy unchanged.
    var (worker, svc) = _newWorker(
      integrity: new Whizbang.Core.Messaging.StreamIntegrityOptions { CheckpointsEnabled = true });
    var entry = _entry(MessageFailureReason.PoisonRedeliveryLoop, recoveryAttempts: 0)
      with { MessageType = "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core" };
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.HoldCalls).Contains(entry.DeadLetterId)
      .Because("an ENABLED subsystem's poison row is real evidence for an operator");
    await Assert.That(svc.DiscardCalls).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task TheCounters_StartAtZeroAndAreReadableAsync() {
    // These are the worker's only external account of what it has done — an operator reads them to
    // tell "the sweep is running and finding nothing" apart from "the sweep is not running". They
    // were never read in a test, so nothing held them to being observable at all.
    var (worker, _) = _newWorker();

    await Assert.That(worker.TotalScans).IsEqualTo(0L);
    await Assert.That(worker.TotalRecovered).IsEqualTo(0L);
    await Assert.That(worker.TotalHeld).IsEqualTo(0L);
    await Assert.That(worker.TotalPermanentlyFailed).IsEqualTo(0L)
      .Because("a counter that cannot be read is indistinguishable from a sweep that never ran");
  }

  [Test]
  public async Task ShutdownBeforeTheSchemaIsReady_ExitsQuietlyAsync() {
    // The worker parks on the schema gate before its first sweep. A pod stopped while still
    // waiting has no DLQ table to scan, so the exit must be silent rather than an error on every
    // fast restart.
    var svc = new FakeRecoveryService();
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    var sp = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new NeverReadySchemaGate(),
      Options.Create(new DeadLetterRecoveryOptions { ScanIntervalMinutes = 1, ScanBatchSize = 50 }),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGenerationProvider("test/0.0.1"),
      NullLogger<DeadLetterRecoveryWorker>.Instance,
      metrics: null,
      notificationListener: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.TotalScans).IsEqualTo(0L)
      .Because("nothing may be scanned before the schema exists — a sweep against a missing table "
             + "is what the gate is there to prevent");
  }

  /// <summary>A schema gate that never opens, for the shutdown-while-waiting path.</summary>
  private sealed class NeverReadySchemaGate : ISchemaReadyGate {
    public bool IsReady => false;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken)
      => Task.Delay(Timeout.Infinite, cancellationToken);
  }

  private static (DeadLetterRecoveryWorker Worker, FakeRecoveryService Svc) _newWorker(
      DeadLetterRecoveryOptions? options = null,
      string generation = "test/0.0.1",
      FakeNotificationListener? listener = null,
      Whizbang.Core.Messaging.StreamIntegrityOptions? integrity = null) {
    var svc = new FakeRecoveryService();
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(new DefaultDeadLetterRecoveryPolicy(Options.Create(options ?? new DeadLetterRecoveryOptions())));
    var sp = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new ImmediateSchemaGate(),
      Options.Create(options ?? new DeadLetterRecoveryOptions { ScanIntervalMinutes = 1, ScanBatchSize = 50 }),
      Options.Create(integrity ?? new Whizbang.Core.Messaging.StreamIntegrityOptions()),
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
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGenerationProvider("test/0.0.1"),
      NullLogger<DeadLetterRecoveryWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token); // must NOT throw
    await Task.Yield();
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    // Worker degrades to a LOUD no-op: the scan loop stays alive (a silent early return
    // here once made a mis-wired production host indistinguishable from a healthy quiet one
    // for a day), but nothing is replayed and nothing is recovered.
    await Assert.That(worker.TotalGenerationReplays).IsEqualTo(0);
    await Assert.That(worker.TotalRecovered).IsEqualTo(0);
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

  [Test]
  public async Task RecoveryThatKeepsRecreatingItsOwnDeadLetters_TripsTheLoopBreakerAsync() {
    // Every batch is dead-lettered AFTER the scan that will observe it, which is the shape of
    // recovery re-driving a message that fails and lands back as a brand-new row. The per-row
    // MaxRecoveryAttempts check cannot see this, because each row really is on its first attempt.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      ScanBatchSize = 50,
      LoopBreakerConsecutiveCycles = 2,
      EnableGenerationReplay = false,
    };
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(options, listener: listener);

    static DeadLetterEntry Fresh() => new(
      DeadLetterId: Guid.NewGuid(),
      SourceTable: DeadLetterSourceTable.OUTBOX,
      SourceId: Guid.NewGuid(),
      StreamId: null,
      MessageType: "Test.Event",
      FailureReason: MessageFailureReason.Throttled,
      AttemptsWhenDlq: 10,
      // Ahead of any scan start in this test: the row did not exist when the last scan began.
      DeadLetteredAt: DateTimeOffset.UtcNow.AddMinutes(5),
      RecoveryStatus: DeadLetterRecoveryStatus.Pending,
      RecoveryAttempts: 0,
      Generation: "test/0.0.1");

    for (var i = 0; i < 6; i++) { svc.FetchBatches.Enqueue([Fresh(), Fresh()]); }

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Scan 1 establishes the baseline and must NOT trip: nothing to compare against yet.
    await svc.FetchSignal(1).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(worker.TotalLoopBreakerTrips).IsEqualTo(0);

    // Scans 2 and 3 each see a wholly fresh batch; the second consecutive one trips.
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(2).WaitAsync(TimeSpan.FromSeconds(5));
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(3).WaitAsync(TimeSpan.FromSeconds(5));

    // A fetch signal fires as the cycle STARTS gathering rows; the breaker decision is taken
    // afterwards, while that batch is processed. Waiting on fetch 3 therefore says nothing about
    // whether cycle 3 has reached its decision, and under load the assertion below wins the race
    // and reads a trip count of 0. Cycle 4 cannot fetch until cycle 3 has returned, so its fetch
    // is the signal that the third cycle -- and its breaker decision -- is genuinely complete.
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(4).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(worker.TotalLoopBreakerTrips).IsEqualTo(1)
      .Because("the second consecutive wholly-fresh batch is the signal that recovery is feeding "
             + "itself, and tripping once is what stops the cycle from running forever");
    await Assert.That(worker.IsLoopBreakerOpen).IsTrue();

    // And it stops recovering. The rows keep coming -- batches are still queued -- so a recovery
    // count that does not move can only be the open breaker holding it back, not an empty queue.
    var recoveredAfterTrip = svc.RecoverCalls.Count;
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(5).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(svc.RecoverCalls.Count).IsEqualTo(recoveredAfterTrip)
      .Because("with the breaker open the worker must leave the rows alone; recovering them is "
             + "what would re-create the dead letters it just decided it was looping on");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task GenuineBacklogOfOldDeadLetters_DoesNotTripTheBreakerAsync() {
    // The case the breaker must never harm: a real backlog from an outage. Every row predates the
    // scan, so draining it is the worker doing its job however many cycles it takes.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      ScanBatchSize = 50,
      LoopBreakerConsecutiveCycles = 2,
      EnableGenerationReplay = false,
    };
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(options, listener: listener);

    for (var i = 0; i < 3; i++) { svc.FetchBatches.Enqueue([_entry(), _entry()]); }

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await svc.FetchSignal(1).WaitAsync(TimeSpan.FromSeconds(5));
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(2).WaitAsync(TimeSpan.FromSeconds(5));
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(3).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(worker.TotalLoopBreakerTrips).IsEqualTo(0);
    await Assert.That(worker.IsLoopBreakerOpen).IsFalse();
    await Assert.That(svc.RecoverCalls.Count).IsGreaterThanOrEqualTo(2);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }


  [Test]
  public async Task RecoveryDefersWhileTheServiceIsStillDrainingAsync() {
    // Re-driving a dead letter puts work back onto the same queues it failed on. Doing that while
    // the service is still draining is how a recovery becomes a second storm — the exact shape that
    // required disabling recovery by configuration in a live deployment. With arbitration, waiting
    // is structural: recovery holds the highest housekeeping rank but still yields to a busy
    // service, so it resumes on its own once the queues are clear.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      ScanBatchSize = 50,
      EnableGenerationReplay = false,
    };
    var svc = new FakeRecoveryService { Backlog = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 } };
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IWorkCoordinator>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(options)));
    var sp = services.BuildServiceProvider();
    var housekeeping = new HousekeepingCoordinator();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new ImmediateSchemaGate(), Options.Create(options),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGenerationProvider("test/0.0.1"), NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: null, housekeeping: housekeeping);
    svc.FetchBatches.Enqueue([_entry()]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await Task.Delay(250);

    await Assert.That(svc.RecoverCalls).IsEmpty()
      .Because("a busy service must not have dead letters re-driven into its queues");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task RecoveryTakesTheSlotAheadOfIntegrityAsync() {
    // The ranking that matters: the dead-letter table often CONTAINS what integrity would detect as
    // a gap and ask an origin to redeliver. Recovering locally first removes the reason to ask.
    var housekeeping = new HousekeepingCoordinator();
    var settled = new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 };

    var dlq = housekeeping.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, settled);
    var integrity = housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, null);

    await Assert.That(dlq.Granted).IsTrue();
    await Assert.That(integrity.Granted).IsFalse();
    await Assert.That(integrity.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning);
  }


  [Test]
  public async Task WaitForIdleFalse_RecoversEvenWhileBusyAsync() {
    // The explicit opt-DOWN: an operator who values recovery latency over interactive throughput
    // turns the idle gate off and gets the scan-cadence behavior. The default stays idle-gated,
    // because the default has to be the one that cannot storm.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      ScanBatchSize = 50,
      EnableGenerationReplay = false,
      WaitForIdle = false,
    };
    var svc = new FakeRecoveryService { Backlog = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 } };
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IWorkCoordinator>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(options)));
    var sp = services.BuildServiceProvider();
    var housekeeping = new HousekeepingCoordinator();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new ImmediateSchemaGate(), Options.Create(options),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGenerationProvider("test/0.0.1"), NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: null, housekeeping: housekeeping);
    svc.FetchBatches.Enqueue([_entry()]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("WaitForIdle=false is the deliberate opt-down to scan-cadence recovery");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }


  [Test]
  public async Task ForcedThroughWhileBusy_ScansNarrowAsync() {
    // #669: the bounded-deferral escape exists so a never-settled service still heals — but
    // being forced through the gate means the service is VISIBLY busy, so the pass must
    // trickle (PressuredScanBatchSize), never flood the queues recovery is yielding to.
    // MaxConsecutiveDeferrals=0 forces the escape on the very first scan.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      ScanBatchSize = 200,
      PressuredScanBatchSize = 25,
      EnableGenerationReplay = false,
      WaitForIdle = true,
    };
    var svc = new FakeRecoveryService { Backlog = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 } };
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IWorkCoordinator>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(options)));
    var sp = services.BuildServiceProvider();
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 0 });
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new ImmediateSchemaGate(), Options.Create(options),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGenerationProvider("test/0.0.1"), NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: null, housekeeping: housekeeping);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(TimeSpan.FromSeconds(30));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.FetchedBatchSizes.TryDequeue(out var first)).IsTrue();
    await Assert.That(first).IsEqualTo(25)
      .Because("a forced pass is recovery running AGAINST a busy service's interest — it "
             + "earns a trickle, and the full batch waits for genuine settledness");
  }

  [Test]
  public async Task AdaptiveBatch_SettledScans_RampFromFloorTowardCeilingAsync() {
    // The settled-path scan batch is sized by the AIMD controller (AdaptiveStreamBatch): it
    // starts at MinScanBatchSize and grows by ScanBatchIncreaseStep on each clean, saturated
    // scan up to ScanBatchSize. A high ceiling is safe precisely because the batch ramps into
    // it instead of bursting cold.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      WaitForIdle = false,
      EnableGenerationReplay = false,
      AdaptiveScanBatchEnabled = true,
      MinScanBatchSize = 2,          // floor / starting width
      ScanBatchIncreaseStep = 3,     // additive growth per clean saturated scan
      ScanBatchSize = 100,           // ceiling
    };
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(options, listener: listener);

    // Scan 1 requests the floor (2). Saturate it so the controller grows: return 2 recoverable rows.
    svc.FetchBatches.Enqueue([_entry(), _entry()]);
    // Scan 2 should request floor+step (5). Saturate again (return 5).
    svc.FetchBatches.Enqueue([_entry(), _entry(), _entry(), _entry(), _entry()]);
    // Scan 3 just needs to happen so we can read the post-growth requested width.
    svc.FetchBatches.Enqueue([_entry()]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wake the worker between scans instead of waiting on the minute backstop — a DeadLetterReady
    // signal completes the wake race so the next scan runs immediately.
    await svc.FetchSignal(1).WaitAsync(TimeSpan.FromSeconds(30));
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(2).WaitAsync(TimeSpan.FromSeconds(30));
    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.FetchSignal(3).WaitAsync(TimeSpan.FromSeconds(30));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    var sizes = svc.FetchedBatchSizes.ToArray();
    await Assert.That(sizes[0]).IsEqualTo(2)
      .Because("a freshly started worker has no drain feedback yet, so it begins at the floor");
    await Assert.That(sizes[1]).IsEqualTo(5)
      .Because("scan 1 returned a full, clean batch (saturated, zero churn) — the controller "
             + "grows by exactly one additive step");
    await Assert.That(sizes[2]).IsEqualTo(8)
      .Because("a second clean saturated scan grows by another step — the batch ramps toward "
             + "the ceiling rather than bursting to it");
  }

  [Test]
  public async Task AdaptiveBatch_Disabled_UsesFixedScanBatchSizeAsync() {
    // Legacy escape hatch: with adaptivity off, every settled scan requests the fixed ScanBatchSize.
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      WaitForIdle = false,
      EnableGenerationReplay = false,
      AdaptiveScanBatchEnabled = false,
      ScanBatchSize = 137,
    };
    var (worker, svc) = _newWorker(options);
    svc.FetchBatches.Enqueue([_entry()]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(TimeSpan.FromSeconds(30));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.FetchedBatchSizes.TryDequeue(out var first)).IsTrue();
    await Assert.That(first).IsEqualTo(137)
      .Because("with the controller off, the fixed ScanBatchSize is used every settled scan");
  }

  [Test]
  [Timeout(30000)]
  public async Task ScanThatThrows_DoesNotEndRecoveryAsync(CancellationToken testToken) {
    // A scan reads the dead-letter table, so a transient database fault is expected rather than
    // exceptional. If it escaped the loop the worker would stop for the remaining life of the
    // process and dead letters would simply stop being retried -- with nothing failing, because
    // a queue nobody is draining looks exactly like a queue with nothing in it.
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(listener: listener);
    svc.FetchThrowsOnFirstCall = true;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      await svc.FetchSignal(1).WaitAsync(TimeSpan.FromSeconds(10), testToken);

      // Wake it rather than waiting out the scan interval, the same way the other tests here
      // drive successive scans.
      listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);

      // The scan AFTER the failed one. Only a loop that survived performs it.
      await svc.FetchSignal(2).WaitAsync(TimeSpan.FromSeconds(10), testToken);
    } finally {
      await cts.CancelAsync();
      try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    await Assert.That(svc.FetchedBatchSizes.Count).IsGreaterThanOrEqualTo(2)
      .Because("the scan after a failed one still has to run; one bad read is not a reason to "
             + "stop retrying dead letters for the life of the process");
  }

  [Test]
  public async Task RecoveryThrows_ZeroCooldownPolicy_SchedulesImmediateRetryAsync() {
    // Line 99: _exponentialCooldown returns TimeSpan.Zero when the policy's configured base
    // cooldown is already zero or negative (LeaseExpired's built-in policy is explicitly
    // Cooldown=TimeSpan.Zero — "retry immediately"). If exponential backoff manufactured a
    // delay here anyway, an operator who deliberately configured a zero-cooldown policy for a
    // transient failure would silently lose that immediacy.
    var (worker, svc) = _newWorker();
    svc.RecoverShouldThrow = true;
    var entry = _entry(MessageFailureReason.LeaseExpired, recoveryAttempts: 2);
    svc.FetchBatches.Enqueue([entry]);

    var before = DateTimeOffset.UtcNow;
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.ScheduleCalls).Count().IsEqualTo(1)
      .Because("a recovery exception must still schedule a next attempt, even under a zero-cooldown policy");
    var scheduled = svc.ScheduleCalls[0];
    await Assert.That(scheduled.NextAt).IsLessThan(before.AddSeconds(5))
      .Because("a zero-cooldown policy means retry immediately; exponential backoff must not "
             + "manufacture a delay the operator did not configure — a real delay here would be "
             + "many minutes out, not a couple of seconds");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers lines 183-184: the generation-replay sweep's catch. A failed sweep must degrade to a
  /// logged error affecting only that startup sweep — never kill the worker before it reaches its
  /// scan loop, or one bad ResetForGenerationAsync call would stop all dead-letter recovery for
  /// the life of the process.
  /// </summary>
  [Test]
  public async Task GenerationReplaySweepThrows_LogsAndStillRunsTheScanLoopAsync() {
    var (worker, svc) = _newWorker();
    svc.ResetForGenerationShouldThrow = true;
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls).Contains(entry.DeadLetterId)
      .Because("the generation-replay sweep threw, but the scan loop that follows it must still "
             + "run and recover due rows");
    await Assert.That(worker.TotalGenerationReplays).IsEqualTo(0)
      .Because("the sweep threw before recording anything, so nothing was scheduled by it");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers line 211: the OperationCanceledException catch around _scanOnceAsync, which breaks
  /// the loop. A shutdown while a scan's fetch is genuinely in flight must end the loop quietly.
  /// The fetch is blocked on a real await (Task.Delay(Infinite, ct)), and the test waits on a
  /// signal proving the fetch call has actually started before canceling — never a sleep.
  /// </summary>
  [Test]
  public async Task CanceledWhileAScanIsFetching_StopsTheLoopCleanlyAsync() {
    var (worker, svc) = _newWorker();
    svc.BlockFetchUntilCanceled = true;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchStartedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // StopAsync (BackgroundService, .NET) awaits the executing task to completion, so by the
    // time it returns the whole loop has genuinely exited — this is not a race against a timer.
    await Assert.That(svc.FetchedBatchSizes.Count).IsEqualTo(1)
      .Because("the loop must stop at the in-flight scan and never start a second one after "
             + "cancellation is observed");
  }

  /// <summary>
  /// Covers lines 426-427: CountServiceBacklogAsync failing under WaitForIdle=true. A gate that
  /// cannot measure settledness must default to proceeding (Verdict.ProceedUnmeasured, same as an
  /// unwired coordinator) rather than silently disabling recovery for the rest of the process.
  /// </summary>
  [Test]
  public async Task BacklogCountThrows_ProceedsUnmeasuredAndStillRecoversAsync() {
    var options = new DeadLetterRecoveryOptions {
      ScanIntervalMinutes = 1,
      ScanBatchSize = 50,
      EnableGenerationReplay = false,
      WaitForIdle = true,
    };
    var svc = new FakeRecoveryService { CountServiceBacklogShouldThrow = true };
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IWorkCoordinator>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(options)));
    var sp = services.BuildServiceProvider();
    var housekeeping = new HousekeepingCoordinator();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new ImmediateSchemaGate(), Options.Create(options),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGenerationProvider("test/0.0.1"), NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: null, housekeeping: housekeeping);
    svc.FetchBatches.Enqueue([_entry()]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls.Count).IsGreaterThanOrEqualTo(1)
      .Because("a backlog-count failure must default to unmeasured-proceed, not a silent deadlock "
             + "that never recovers anything because settledness can never be confirmed");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers lines 565-566: LogTerminalSetFailed around MarkDiscardedAsync in the
  /// disabled-subsystem discard branch. A settle failure (DB hiccup) must be swallowed and
  /// logged, leaving the row due for the next scan's retry — and, critically, must not stall the
  /// loop from processing the rest of the queue.
  /// </summary>
  [Test]
  public async Task DisabledSubsystemEntry_DiscardThrows_SwallowsAndKeepsScanningAsync() {
    var listener = new FakeNotificationListener();
    var (worker, svc) = _newWorker(
      new DeadLetterRecoveryOptions { ScanIntervalMinutes = 60, ScanBatchSize = 50 },
      listener: listener,
      integrity: new Whizbang.Core.Messaging.StreamIntegrityOptions { CheckpointsEnabled = false });
    svc.DiscardShouldThrow = true;
    var poisonEntry = _entry(MessageFailureReason.PoisonRedeliveryLoop, recoveryAttempts: 0)
      with { MessageType = "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core" };
    var recoverableEntry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([poisonEntry]);
    svc.FetchBatches.Enqueue([recoverableEntry]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.DiscardCalls).IsEmpty()
      .Because("MarkDiscardedAsync threw, so the row was not actually settled this cycle");

    listener.Raise(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
    await svc.RecoverSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(svc.RecoverCalls).Contains(recoverableEntry.DeadLetterId)
      .Because("the discard failure must not stall the loop — the next scan still processes new work");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers line 610: the empty else when RecoverAsync returns false — recover_dead_letter's
  /// atomic UPDATE lost the race to another worker, or the row was already terminal. This is a
  /// normal outcome, not an error: no retry is scheduled, no terminal transition happens, and the
  /// rest of the batch still gets processed.
  /// </summary>
  [Test]
  public async Task RecoverAsync_ReturnsFalse_MovesOnWithoutRetryOrAlarmAsync() {
    var (worker, svc) = _newWorker();
    svc.RecoverShouldReturnFalse = true;
    var raced = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    var other = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([raced, other]);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FirstFetchSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    await Assert.That(svc.RecoverCalls).IsEquivalentTo([raced.DeadLetterId, other.DeadLetterId])
      .Because("losing the race is a normal outcome — recovery is still attempted for every due "
             + "row, and a false result for one must not stop the rest of the batch");
    await Assert.That(svc.ScheduleCalls).IsEmpty()
      .Because("a lost race is not a failure, so nothing is rescheduled");
    await Assert.That(svc.HoldCalls).IsEmpty();
    await Assert.That(svc.PermanentlyFailedCalls).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

}
