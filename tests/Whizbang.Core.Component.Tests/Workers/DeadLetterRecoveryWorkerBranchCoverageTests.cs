// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Condition outcomes of <see cref="DeadLetterRecoveryWorker"/> that no other test takes: the
/// options guard, every metric emission with a metrics sink wired (the existing suites construct
/// the worker without one), the loop breaker's stay-open-until-restart cooldown, a deferral with no
/// backlog measurement, shutdown arriving between two rows of one batch, and a pressured pass
/// feeding the adaptive scan-batch controller as churn.
/// </summary>
/// <remarks>
/// Every scan here is driven through the real loop and awaited on a signal the loop body itself
/// emits (<see cref="DeadLetterRecoveryWorker.OnScanCompleted"/>, a fetch, a recovery), never on
/// <c>StartAsync</c> returning: since .NET 10 that only schedules the body.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs</code-under-test>
[Category("Workers")]
public sealed class DeadLetterRecoveryWorkerBranchCoverageTests {
  private const string GENERATION = "build/7.7.7";

  // Completion signals that resolve in milliseconds when healthy; the ceiling only turns a genuine
  // hang into a failure instead of a stuck run.
  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

  // ==========================================================================
  // Constructor guard
  // ==========================================================================

  private sealed class NullValueOptions : IOptions<DeadLetterRecoveryOptions> {
    public DeadLetterRecoveryOptions Value => null!;
  }

  private static DeadLetterRecoveryWorker _construct(IOptions<DeadLetterRecoveryOptions>? options) {
    var sp = new ServiceCollection().BuildServiceProvider();
    return new DeadLetterRecoveryWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: new ImmediateSchemaGate(),
      options: options!,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      generationProvider: new FixedGenerationProvider(GENERATION),
      logger: NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: new NoOpWorkNotificationListener());
  }

  // Line 56, `options?.Value` taking its null side: a host that resolved no options wrapper at all.
  [Test]
  public async Task Constructor_NullOptions_ThrowsNamingOptionsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = _construct(null);
      await Task.CompletedTask;
    });

    await Assert.That(ex?.ParamName).IsEqualTo("options")
      .Because("a missing options wrapper must fail at construction, naming the parameter, rather than "
             + "surface later as a null dereference inside the scan loop");
  }

  // Line 56, `?? throw` taking its throw side with a non-null wrapper: the wrapper exists but carries
  // no value, which is what a broken options binding produces.
  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsNamingOptionsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = _construct(new NullValueOptions());
      await Task.CompletedTask;
    });

    await Assert.That(ex?.ParamName).IsEqualTo("options")
      .Because("an options wrapper with no value is as unusable as no wrapper, and must be refused the same way");
  }

  // ==========================================================================
  // Metric emissions with a sink wired
  // ==========================================================================

  // Line 206: the generation-replay sweep scheduled rows and a metrics sink is wired.
  [Test]
  public async Task GenerationReplay_WithMetrics_CountsScheduledRowsTaggedWithTheGenerationAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService { GenerationReplayReturn = 4 };
    var options = _options();
    options.EnableGenerationReplay = true;
    options.AutoCanaryOnNewGeneration = false;
    var rig = _build(options, svc, metrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(rig.Worker.TotalGenerationReplays).IsEqualTo(4L);
    await Assert.That(_sum(meters, "whizbang.dead_letters.generation_replay_scheduled", "generation", GENERATION))
      .IsEqualTo(4d)
      .Because("the rows a deploy re-offered are the operator's evidence that the sweep ran, and the "
             + "generation tag is what ties them to the build that re-offered them");
  }

  // Lines 350 and 357: both terminal transitions with a metrics sink wired, each tagged with the rule
  // that exhausted the row and the reason it was dead-lettered.
  [Test]
  public async Task ExhaustedRows_WithMetrics_CountHeldAndPermanentlyFailedTaggedByPolicyAndReasonAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService();
    // ConservativeRetry (budget 1, hold for review) and AggressiveRetry (budget 3, permanent fail).
    var held = _entry(MessageFailureReason.MaxAttemptsExceeded, recoveryAttempts: 1);
    var failed = _entry(MessageFailureReason.Throttled, recoveryAttempts: 3);
    svc.FetchBatches.Enqueue([held, failed]);
    var rig = _build(_options(), svc, metrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(svc.HoldCalls.ToArray()).Contains(held.DeadLetterId);
    await Assert.That(svc.PermanentlyFailedCalls.ToArray()).Contains(failed.DeadLetterId);
    await Assert.That(_sum(meters, "whizbang.dead_letters.held",
        "policy_name", "ConservativeRetry", "reason", nameof(MessageFailureReason.MaxAttemptsExceeded)))
      .IsEqualTo(1d)
      .Because("a row parked for review is a decision waiting on an operator; the counter is how they find out");
    await Assert.That(_sum(meters, "whizbang.dead_letters.permanently_failed",
        "policy_name", "AggressiveRetry", "reason", nameof(MessageFailureReason.Throttled)))
      .IsEqualTo(1d)
      .Because("a row given up on must be counted under the rule that gave up on it");
  }

  // Lines 646 and 654: a successful recovery with a metrics sink wired.
  [Test]
  public async Task RecoveredRow_WithMetrics_CountsTheAttemptAndTheRecoveryAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService();
    var entry = _entry(MessageFailureReason.Throttled, recoveryAttempts: 0);
    svc.FetchBatches.Enqueue([entry]);
    var rig = _build(_options(), svc, metrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(svc.RecoverCalls.ToArray()).Contains(entry.DeadLetterId);
    await Assert.That(_sum(meters, "whizbang.dead_letters.recovery_attempts", "reason", nameof(MessageFailureReason.Throttled)))
      .IsEqualTo(1d)
      .Because("every dispatched recovery attempt counts under the reason it is recovering from");
    await Assert.That(_sum(meters, "whizbang.dead_letters.recovered", DeadLetterMetrics.SOURCE_TABLE_TAG, DeadLetterSourceTable.OUTBOX))
      .IsEqualTo(1d)
      .Because("a recovered row counts under the table it was re-emitted into");
  }

  // Lines 391, 396 and 402: one scan resolves a Pass, a Fail and a Mixed campaign with metrics wired.
  [Test]
  public async Task CampaignVerdicts_WithMetrics_CountEachVerdictUnderItsCohortAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService {
      Cohorts = [new("fp-pass", 10, 1), new("fp-fail", 10, 1), new("fp-mixed", 10, 1)],
    };
    svc.Verdicts["fp-pass"] = new CanaryVerdict(CanaryVerdictKind.Pass, 3, 0, 0);
    svc.Verdicts["fp-fail"] = new CanaryVerdict(CanaryVerdictKind.Fail, 0, 3, 0);
    svc.Verdicts["fp-mixed"] = new CanaryVerdict(CanaryVerdictKind.Mixed, 2, 1, 0);
    var options = _options();
    options.RetryHeldOnStartup = RetryHeldOnStartupMode.Canary;
    var rig = _build(options, svc, metrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(_sum(meters, "whizbang.dead_letters.cohort_verdicts", "cohort", "fp-pass", "verdict", "Pass"))
      .IsEqualTo(1d);
    await Assert.That(_sum(meters, "whizbang.dead_letters.cohort_verdicts", "cohort", "fp-fail", "verdict", "Fail"))
      .IsEqualTo(1d);
    await Assert.That(_sum(meters, "whizbang.dead_letters.cohort_verdicts", "cohort", "fp-mixed", "verdict", "Mixed"))
      .IsEqualTo(1d)
      .Because("each campaign's verdict is the dashboard's record of what the canary concluded about that "
             + "cohort, and it must land under the verdict actually reached, not a neighbor's");
  }

  // Lines 428 and 432: a trickle wave that stayed out and one that washed back, with metrics wired.
  [Test]
  public async Task TrickleWaves_WithMetrics_CountCleanAndHaltedWavesUnderTheirCohortsAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService {
      Cohorts = [new("fp-clean", 100, 1), new("fp-dirty", 100, 1)],
    };
    svc.Verdicts["fp-clean"] = new CanaryVerdict(CanaryVerdictKind.Mixed, 2, 1, 0);
    svc.Verdicts["fp-dirty"] = new CanaryVerdict(CanaryVerdictKind.Mixed, 2, 1, 0);
    svc.TrickleWaveReturns["fp-clean"] = new Queue<int>([7, 14]);
    svc.TrickleWaveReturns["fp-dirty"] = new Queue<int>([7]);
    svc.WaveRequarantines["fp-dirty"] = 3;
    var options = _options();
    options.RetryHeldOnStartup = RetryHeldOnStartupMode.Canary;
    var rig = _build(options, svc, metrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    // One scan: both campaigns resolve Mixed and release a first wave, then the trickle pass that
    // follows in the same scan finds the clean cohort's wave stayed out and the dirty one washed back.
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(_sum(meters, "whizbang.dead_letters.release_waves", "cohort", "fp-clean", "outcome", "clean"))
      .IsEqualTo(1d);
    await Assert.That(_sum(meters, "whizbang.dead_letters.release_waves", "cohort", "fp-dirty", "outcome", "halted"))
      .IsEqualTo(1d)
      .Because("a halted trickle is an operator decision point and must be counted as halted");
    await Assert.That(_sum(meters, "whizbang.dead_letters.release_waves", "cohort", "fp-clean", "outcome", "halted"))
      .IsEqualTo(0d);
    await Assert.That(_sum(meters, "whizbang.dead_letters.release_waves", "cohort", "fp-dirty", "outcome", "clean"))
      .IsEqualTo(0d)
      .Because("a wave that washed back must never read as clean, or the dashboard shows progress that did not happen");
  }

  // Line 506: the stack backfill recorded never-before-seen stacks with metrics wired.
  [Test]
  public async Task StackBackfill_WithMetrics_CountsTheNewStacksTheStoreReportsAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService {
      Unstacked = [new(Guid.NewGuid(), "System.InvalidOperationException: x\n   at A.B.<M>d__1.MoveNext()")],
      NewStacksReturn = 1,
    };
    var options = _options();
    options.StackHistoryRetentionDays = 0;
    var rig = _build(options, svc, metrics: dlqMetrics);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(svc.RecordStacksCalls).IsEqualTo(1);
    await Assert.That(_sum(meters, "whizbang.dead_letters.new_stacks")).IsEqualTo(1d)
      .Because("a never-before-seen stack right after a deploy is the new-failure-mode alarm; the count is "
             + "what the store reported as new, not the size of the batch");
  }

  // Lines 518 and 519: the stack-history prune removed rows with both metric sinks wired.
  [Test]
  public async Task StackHistoryPrune_WithMetrics_CountsPrunedRowsAndRollsThemUpUnderMaintenanceAsync() {
    using var factory = new TestMeterFactory();
    var whizbangMetrics = new WhizbangMetrics(factory);
    var dlqMetrics = new DeadLetterMetrics(whizbangMetrics);
    var rollup = new HousekeepingMetrics(whizbangMetrics, idleTracker: null);
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var svc = new ScriptedRecoveryService { PruneReturn = 4 };
    var options = _options();
    options.StackHistoryRetentionDays = 30;
    var rig = _build(options, svc, metrics: dlqMetrics, rollup: rollup);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(_sum(meters, "whizbang.dead_letters.stack_history_pruned")).IsEqualTo(4d)
      .Because("the dedicated counter reports exactly the rows the prune removed");
    await Assert.That(_sum(meters, "whizbang.housekeeping.items", "activity", nameof(HousekeepingCoordinator.Activity.Maintenance)))
      .IsEqualTo(4d)
      .Because("pruning is cleanup, so the cross-activity rollup attributes it to Maintenance");
    await Assert.That(_sum(meters, "whizbang.housekeeping.items", "activity", nameof(HousekeepingCoordinator.Activity.DeadLetterRecovery)))
      .IsEqualTo(0d)
      .Because("pruned history is not re-driven dead letters, and must not inflate recovery's volume");
  }

  // Line 690: a granted housekeeping slot with the rollup wired records the rows re-driven this cycle.
  [Test]
  public async Task GrantedScan_WithRollup_RecordsRecoveredRowsAndReleasesTheSlotAsync() {
    using var factory = new TestMeterFactory();
    var rollup = new HousekeepingMetrics(new WhizbangMetrics(factory), idleTracker: null);
    using var meters = new MetricAssertionHelper(factory.CreatedMeters.ToArray());
    var housekeeping = new HousekeepingCoordinator();
    var svc = new ScriptedRecoveryService();
    svc.FetchBatches.Enqueue([_entry(), _entry()]);
    var options = _options();
    options.WaitForIdle = true;
    // No IWorkCoordinator registered: the backlog is unmeasured, which the arbiter grants.
    var rig = _build(options, svc, housekeeping: housekeeping, rollup: rollup);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(svc.RecoverCalls.Count).IsEqualTo(2);
    await Assert.That(_sum(meters, "whizbang.housekeeping.items", "activity", nameof(HousekeepingCoordinator.Activity.DeadLetterRecovery)))
      .IsEqualTo(2d)
      .Because("the rollup is the operator's one chart of what housekeeping actually did, and recovery's "
             + "share is the rows it re-drove while holding the slot");
    var next = housekeeping.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, null);
    await Assert.That(next.Granted).IsTrue()
      .Because("the scan must release the slot it held, or recovery and every lower-ranked activity stop for good");
  }

  // ==========================================================================
  // Control-flow outcomes
  // ==========================================================================

  // Line 277: cooldown 0 keeps a tripped breaker open no matter how much time passes.
  [Test]
  public async Task TrippedLoopBreaker_ZeroCooldown_StaysOpenUntilRestartAsync() {
    var options = _options();
    options.ScanIntervalMinutes = 60 * 24 * 7;   // the backstop never fires inside this test
    options.LoopBreakerConsecutiveCycles = 2;
    options.LoopBreakerCooldownMinutes = 0;
    var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
    var svc = new ScriptedRecoveryService();

    static DeadLetterEntry Fresh() => _entry() with {
      // Ahead of every scan start before the clock is advanced: the row did not exist when the
      // previous scan began, which is the self-inflicted shape the breaker measures.
      DeadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };
    for (var i = 0; i < 6; i++) { svc.FetchBatches.Enqueue([Fresh(), Fresh()]); }
    var rig = _build(options, svc, timeProvider: clock);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    // Scan 1 is the baseline; scans 2 and 3 are wholly fresh, and the second of them trips.
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    rig.Bell.Ring();
    await rig.Scans.Completed(2).WaitAsync(_timeout);
    rig.Bell.Ring();
    await rig.Scans.Completed(3).WaitAsync(_timeout);
    await Assert.That(rig.Worker.IsLoopBreakerOpen).IsTrue();
    var recoveredAtTrip = svc.RecoverCalls.Count;

    // A day passes. With a positive cooldown the next scan would close the breaker; with zero it must not.
    clock.Advance(TimeSpan.FromDays(1));
    rig.Bell.Ring();
    await rig.Scans.Completed(4).WaitAsync(_timeout);
    rig.Bell.Ring();
    await rig.Scans.Completed(5).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(rig.Worker.IsLoopBreakerOpen).IsTrue()
      .Because("cooldown 0 is the deliberate stay-open-until-restart choice, for a deployment whose operator "
             + "wants to look before recovery runs again");
    await Assert.That(rig.Worker.TotalLoopBreakerTrips).IsEqualTo(1L);
    await Assert.That(svc.RecoverCalls.Count).IsEqualTo(recoveredAtTrip)
      .Because("due rows kept arriving, so an unchanged recovery count can only be the open breaker holding them back");
  }

  // Line 467: a deferred scan with no backlog measurement logs -1 for the inbox count.
  [Test]
  public async Task DeferredScan_WithoutBacklogMeasurement_LogsUnmeasuredInboxRowsAndFetchesNothingAsync() {
    var housekeeping = new HousekeepingCoordinator();
    // Something else already holds the recovery slot, so this worker's request is refused even
    // though, with no IWorkCoordinator registered, there is no backlog reading at all.
    var holder = housekeeping.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, null);
    await Assert.That(holder.Granted).IsTrue();
    var logger = new CapturingLogger();
    var svc = new ScriptedRecoveryService();
    svc.FetchBatches.Enqueue([_entry()]);
    var options = _options();
    options.WaitForIdle = true;
    var rig = _build(options, svc, housekeeping: housekeeping, logger: logger);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    var deferred = logger.Entries.Where(e => e.EventId == 15).ToList();
    await Assert.That(deferred.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(deferred[0].State["InboxRows"]).IsEqualTo((object?)(-1L))
      .Because("an unmeasured backlog must read as -1, distinguishable from a measured empty inbox (0)");
    await Assert.That(deferred[0].State["Reason"]).IsEqualTo((object?)HousekeepingCoordinator.Verdict.AlreadyRunning);
    await Assert.That(svc.FetchCount).IsEqualTo(0)
      .Because("a deferred scan must not touch the dead-letter table at all");
  }

  // Line 597: shutdown arrives while a batch is being processed; the next row is left alone.
  [Test]
  public async Task ShutdownMidBatch_LeavesTheRemainingRowsUntouchedAsync() {
    var svc = new ScriptedRecoveryService();
    var first = _entry();
    var second = _entry();
    svc.FetchBatches.Enqueue([first, second]);
    var rig = _build(_options(), svc);

    using var cts = new CancellationTokenSource();
    // StartAsync links the worker's stopping token to this source, so canceling it from inside the
    // first recovery is a shutdown landing between the batch's two rows.
    svc.OnRecover = _ => cts.Cancel();
    await rig.Worker.StartAsync(cts.Token);
    await svc.RecoverSignal.WaitAsync(_timeout);
    await rig.Worker.ExecuteTask!.WaitAsync(_timeout)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await rig.Worker.StopAsync(CancellationToken.None);

    var recovered = svc.RecoverCalls.ToArray();
    await Assert.That(recovered.Length).IsEqualTo(1);
    await Assert.That(recovered[0]).IsEqualTo(first.DeadLetterId)
      .Because("once shutdown is requested the scan stops between rows instead of re-driving work the "
             + "host is no longer running to process");
    await Assert.That(svc.HoldCalls.ToArray()).IsEmpty();
    await Assert.That(svc.PermanentlyFailedCalls.ToArray()).IsEmpty();
    await Assert.That(svc.ScheduleCalls.ToArray()).IsEmpty()
      .Because("the untouched row stays due for the next process; nothing may be recorded against it");
    await Assert.That(rig.Worker.ExecuteTask.IsFaulted).IsFalse();
  }

  // Line 604: the disabled-subsystem discard settles a row even when the store completes asynchronously.
  [Test]
  public async Task DisabledSubsystemRow_StoreCompletesAsynchronously_IsDiscardedNotHeldAsync() {
    var svc = new ScriptedRecoveryService { DiscardCompletesAsynchronously = true };
    var entry = _entry(MessageFailureReason.PoisonRedeliveryLoop, recoveryAttempts: 0)
      with { MessageType = "Whizbang.Core.Messaging.IntegrityCheckpoint, Whizbang.Core" };
    var plain = _entry();
    svc.FetchBatches.Enqueue([entry, plain]);
    var rig = _build(_options(), svc, integrity: new StreamIntegrityOptions { CheckpointsEnabled = false });

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    await Assert.That(svc.DiscardCalls.ToArray()).Contains(entry.DeadLetterId)
      .Because("a disabled subsystem's message is settled by the recovery scan itself");
    await Assert.That(svc.HoldCalls.ToArray()).IsEmpty();
    var recovered = svc.RecoverCalls.ToArray();
    await Assert.That(recovered.Length).IsEqualTo(1);
    await Assert.That(recovered[0]).IsEqualTo(plain.DeadLetterId)
      .Because("the discarded row is never re-driven, and the scan carries on with the rest of the batch");
  }

  // Line 680, `pressured ? entries.Count : 0` taking its pressured side with the adaptive controller on.
  [Test]
  public async Task PressuredPass_FeedsTheAdaptiveControllerAsChurn_AndHalvesTheNextSettledBatchAsync() {
    var options = _options();
    options.WaitForIdle = true;
    options.AdaptiveScanBatchEnabled = true;
    options.MinScanBatchSize = 2;
    options.ScanBatchIncreaseStep = 10;
    options.ScanBatchSize = 100;
    options.PressuredScanBatchSize = 5;
    var settled = new ServiceBacklog();
    var busy = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 };
    var svc = new ScriptedRecoveryService { Backlog = settled };
    svc.FetchBatches.Enqueue([_entry(), _entry()]);                                // scan 1: settled, saturates the floor
    svc.FetchBatches.Enqueue([_entry(), _entry(), _entry(), _entry(), _entry()]);  // scan 2: forced through while busy
    svc.FetchBatches.Enqueue([_entry()]);                                          // scan 3: settled again
    var housekeeping = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings {
      MaxConsecutiveDeferrals = 0,
      SettledCooldown = TimeSpan.Zero,
    });
    var rig = _build(options, svc, housekeeping: housekeeping, registerCoordinator: true);

    using var cts = new CancellationTokenSource();
    await rig.Worker.StartAsync(cts.Token);
    await rig.Scans.Completed(1).WaitAsync(_timeout);
    svc.Backlog = busy;
    rig.Bell.Ring();
    await rig.Scans.Completed(2).WaitAsync(_timeout);
    svc.Backlog = settled;
    rig.Bell.Ring();
    await svc.FetchSignal(3).WaitAsync(_timeout);
    await _stopAsync(rig.Worker, cts);

    var sizes = svc.FetchedBatchSizes.ToArray();
    await Assert.That(sizes[0]).IsEqualTo(2)
      .Because("a fresh controller starts at the floor");
    await Assert.That(sizes[1]).IsEqualTo(5)
      .Because("a pass forced through a busy service keeps the narrow pressured width");
    await Assert.That(sizes[2]).IsEqualTo(6)
      .Because("scan 1 grew the controller to 12; the forced pass is full churn, which halves it to 6. Read as "
             + "clean it would have grown to 22, and skipped it would have stayed at 12: sustained load must walk "
             + "the batch back toward the floor");
  }

  // ==========================================================================
  // Fakes and helpers
  // ==========================================================================

  /// <summary>Scripted recovery service: records every call, and doubles as the backlog source.</summary>
  private sealed class ScriptedRecoveryService : IDeadLetterRecoveryService, IWorkCoordinator {
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _fetchSignals = new();
    private readonly TaskCompletionSource _recoverSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fetchCount;
    private int _recordStacksCalls;

    public ConcurrentQueue<List<DeadLetterEntry>> FetchBatches { get; } = new();
    public ConcurrentQueue<int> FetchedBatchSizes { get; } = new();
    public int FetchCount => Volatile.Read(ref _fetchCount);
    public ConcurrentQueue<Guid> RecoverCalls { get; } = new();
    public ConcurrentQueue<Guid> HoldCalls { get; } = new();
    public ConcurrentQueue<Guid> PermanentlyFailedCalls { get; } = new();
    public ConcurrentQueue<Guid> DiscardCalls { get; } = new();
    public ConcurrentQueue<Guid> ScheduleCalls { get; } = new();
    public Action<Guid>? OnRecover { get; set; }
    public bool DiscardCompletesAsynchronously { get; set; }
    public int GenerationReplayReturn { get; set; }
    public List<HeldCohort> Cohorts { get; set; } = [];
    public Dictionary<string, CanaryVerdict> Verdicts { get; } = [];
    public Dictionary<string, Queue<int>> TrickleWaveReturns { get; } = [];
    public Dictionary<string, int> WaveRequarantines { get; } = [];
    public List<UnstackedDeadLetter> Unstacked { get; set; } = [];
    public int NewStacksReturn { get; set; }
    public int RecordStacksCalls => Volatile.Read(ref _recordStacksCalls);
    public int PruneReturn { get; set; }
    public ServiceBacklog? Backlog { get; set; }

    /// <summary>Completes once the first recovery has run.</summary>
    public Task RecoverSignal => _recoverSignal.Task;

    /// <summary>Completes once the Nth (1-based) fetch has happened.</summary>
    public Task FetchSignal(int ordinal) => _signal(ordinal).Task;

    private TaskCompletionSource _signal(int ordinal) =>
      _fetchSignals.GetOrAdd(ordinal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
      FetchedBatchSizes.Enqueue(maxCount);
      var batch = FetchBatches.TryDequeue(out var next) ? next : [];
      _signal(Interlocked.Increment(ref _fetchCount)).TrySetResult();
      return Task.FromResult<IReadOnlyList<DeadLetterEntry>>(batch);
    }

    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) {
      RecoverCalls.Enqueue(deadLetterId);
      OnRecover?.Invoke(deadLetterId);
      _recoverSignal.TrySetResult();
      return Task.FromResult(true);
    }

    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      HoldCalls.Enqueue(deadLetterId);
      return Task.CompletedTask;
    }

    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) {
      PermanentlyFailedCalls.Enqueue(deadLetterId);
      return Task.CompletedTask;
    }

    public async Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) {
      if (DiscardCompletesAsynchronously) {
        await Task.Yield();
      }
      DiscardCalls.Enqueue(deadLetterId);
    }

    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      ScheduleCalls.Enqueue(deadLetterId);
      return Task.CompletedTask;
    }

    public Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) =>
      Task.FromResult(GenerationReplayReturn);

    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<HeldCohort>>([.. Cohorts]);

    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
      Task.FromResult(probeSize);

    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(Verdicts.TryGetValue(fingerprint, out var verdict)
        ? verdict
        : new CanaryVerdict(CanaryVerdictKind.Pending, 0, 0, 1));

    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) =>
      Task.FromResult(0);

    public Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    public Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) =>
      Task.FromResult(TrickleWaveReturns.TryGetValue(fingerprint, out var waves) && waves.Count > 0 ? waves.Dequeue() : 0);

    public Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(WaveRequarantines.TryGetValue(fingerprint, out var washback) ? washback : 0);

    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) {
      var batch = Unstacked.Take(maxCount).ToList();
      Unstacked = [.. Unstacked.Skip(maxCount)];
      return Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>(batch);
    }

    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<int> RecordStacksAsync(IReadOnlyList<(Guid, Whizbang.Core.DeadLetters.StackIdentity)> entries, CancellationToken ct = default) {
      Interlocked.Increment(ref _recordStacksCalls);
      return Task.FromResult(NewStacksReturn);
    }

    public Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(PruneReturn);

    // IWorkCoordinator: only the backlog reading matters to the recovery worker.
    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(Backlog);
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
  }

  private sealed class ImmediateSchemaGate : ISchemaReadyGate {
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void MarkReady() { }
    public bool IsReady => true;
  }

  private sealed class FixedGenerationProvider(string value) : IGenerationProvider {
    public string GetGeneration() => value;
  }

  /// <summary>A configured notification listener the test rings to run the next scan at once.</summary>
  private sealed class Bell : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    public event Action<WorkSignalCategory>? OnSignal {
      add { _onSignal += value; }
      remove { _onSignal -= value; }
    }
    public event Action<bool>? OnHealthChanged { add { /* the fake never raises this event */ } remove { /* the fake never raises this event */ } }
    public void Ring() => _onSignal?.Invoke(WorkSignalCategory.DeadLetterReady);
  }

  /// <summary>Completes the Nth (1-based) signal when the worker's Nth scan has finished with its rows.</summary>
  private sealed class ScanCounter {
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _signals = new();
    private int _count;

    public ScanCounter(DeadLetterRecoveryWorker worker) {
      worker.OnScanCompleted += () => _signal(Interlocked.Increment(ref _count)).TrySetResult();
    }

    public Task Completed(int ordinal) => _signal(ordinal).Task;

    private TaskCompletionSource _signal(int ordinal) =>
      _signals.GetOrAdd(ordinal, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  private sealed record LogEntry(int EventId, IReadOnlyDictionary<string, object?> State);

  /// <summary>Records every log call with its structured state, at every level (including Debug).</summary>
  private sealed class CapturingLogger : ILogger<DeadLetterRecoveryWorker> {
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var values = new Dictionary<string, object?>(StringComparer.Ordinal);
      if (state is IEnumerable<KeyValuePair<string, object?>> pairs) {
        foreach (var pair in pairs) {
          values[pair.Key] = pair.Value;
        }
      }
      _entries.Enqueue(new LogEntry(eventId.Id, values));
    }
  }

  private sealed record Rig(DeadLetterRecoveryWorker Worker, ScriptedRecoveryService Svc, ScanCounter Scans, Bell Bell);

  private static Rig _build(
      DeadLetterRecoveryOptions options,
      ScriptedRecoveryService svc,
      DeadLetterMetrics? metrics = null,
      HousekeepingCoordinator? housekeeping = null,
      HousekeepingMetrics? rollup = null,
      StreamIntegrityOptions? integrity = null,
      TimeProvider? timeProvider = null,
      ILogger<DeadLetterRecoveryWorker>? logger = null,
      bool registerCoordinator = false) {
    var services = new ServiceCollection();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    if (registerCoordinator) {
      services.AddSingleton<IWorkCoordinator>(svc);
    }
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    var sp = services.BuildServiceProvider();
    var bell = new Bell();
    var worker = new DeadLetterRecoveryWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: new ImmediateSchemaGate(),
      options: Options.Create(options),
      integrityOptions: Options.Create(integrity ?? new StreamIntegrityOptions()),
      generationProvider: new FixedGenerationProvider(GENERATION),
      logger: logger ?? NullLogger<DeadLetterRecoveryWorker>.Instance,
      notificationListener: bell,
      metrics: metrics,
      housekeeping: housekeeping,
      metricsRollup: rollup,
      timeProvider: timeProvider);
    return new Rig(worker, svc, new ScanCounter(worker), bell);
  }

  /// <summary>Quiet defaults: no replay, no campaign, no idle gate, no stack work unless a test opts in.</summary>
  private static DeadLetterRecoveryOptions _options() => new() {
    ScanIntervalMinutes = 60,
    ScanBatchSize = 50,
    AdaptiveScanBatchEnabled = false,
    EnableGenerationReplay = false,
    WaitForIdle = false,
    RetryHeldOnStartup = RetryHeldOnStartupMode.Off,
    StackHistoryRetentionDays = 0,
  };

  private static DeadLetterEntry _entry(
      MessageFailureReason reason = MessageFailureReason.Throttled,
      int recoveryAttempts = 0) => new(
    DeadLetterId: Guid.NewGuid(),
    SourceTable: DeadLetterSourceTable.OUTBOX,
    SourceId: Guid.NewGuid(),
    StreamId: null,
    MessageType: "Test.Event",
    FailureReason: reason,
    AttemptsWhenDlq: 10,
    DeadLetteredAt: DateTimeOffset.UtcNow.AddMinutes(-1),
    RecoveryStatus: DeadLetterRecoveryStatus.Pending,
    RecoveryAttempts: recoveryAttempts,
    Generation: GENERATION);

  private static async Task _stopAsync(DeadLetterRecoveryWorker worker, CancellationTokenSource cts) {
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The current cumulative value of a passive counter, summed over every series whose tags include
  /// the given pairs (all series when no pair is given).
  /// </summary>
  private static double _sum(
      MetricAssertionHelper meters, string instrument,
      string? key1 = null, string? value1 = null, string? key2 = null, string? value2 = null) =>
    meters.GetByName(instrument)
      .Where(m => _hasTag(m, key1, value1) && _hasTag(m, key2, value2))
      .Sum(m => m.Value);

  private static bool _hasTag(RecordedMeasurement measurement, string? key, string? value) =>
    key is null || (measurement.Tags.TryGetValue(key, out var actual) && actual == value);
}
