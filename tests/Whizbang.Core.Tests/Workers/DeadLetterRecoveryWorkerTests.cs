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
    public int GenerationReplayReturn { get; set; }
    public TaskCompletionSource FirstFetchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondFetchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
      return Task.FromResult(true);
    }
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      HoldCalls.Add(deadLetterId); return Task.CompletedTask;
    }
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) {
      PermanentlyFailedCalls.Add(deadLetterId); return Task.CompletedTask;
    }
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
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
      string generation = "test/0.0.1") {
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
      NullLogger<DeadLetterRecoveryWorker>.Instance);
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
}
