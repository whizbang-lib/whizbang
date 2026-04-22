using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

/// <summary>
/// Tests for drain pattern (2f), runaway cap (2f safety), and immediate outbox
/// completion flush (2g) behaviors in WorkCoordinatorPublisherWorker, plus
/// option defaults for 2d/2e.
/// All tests use signal-based synchronization (TaskCompletionSource / SemaphoreSlim);
/// no Task.Delay or polling loops. Harness timeouts bound RED-path runtime.
/// </summary>
public class WorkCoordinatorPublisherWorkerDrainTests {

  // ==========================================================================
  // Option default tests (2d, 2e)
  // ==========================================================================

  [Test]
  public async Task WorkCoordinatorPublisherOptions_PollingIntervalMilliseconds_DefaultIs250Async() {
    var options = new WorkCoordinatorPublisherOptions();
    await Assert.That(options.PollingIntervalMilliseconds).IsEqualTo(250);
  }

  [Test]
  public async Task WorkCoordinatorPublisherOptions_MaxStreamsPerBatch_DefaultIs1000Async() {
    var options = new WorkCoordinatorPublisherOptions();
    await Assert.That(options.MaxStreamsPerBatch).IsEqualTo(1000);
  }

  [Test]
  public async Task WorkCoordinatorPublisherOptions_MaxConsecutiveFullDrains_DefaultIs100Async() {
    var options = new WorkCoordinatorPublisherOptions();
    await Assert.That(options.MaxConsecutiveFullDrains).IsEqualTo(100);
  }

  [Test]
  public async Task WorkCoordinatorPublisherOptions_ImmediateOutboxCompletionFlushThreshold_DefaultIs1Async() {
    var options = new WorkCoordinatorPublisherOptions();
    await Assert.That(options.ImmediateOutboxCompletionFlushThreshold).IsEqualTo(1);
  }

  // ==========================================================================
  // 2d: MaxStreamsPerBatch wiring
  // ==========================================================================

  [Test]
  public async Task ProcessWorkBatch_PassesMaxStreamsPerBatchFromOptions_ToCoordinatorAsync() {
    // Arrange — worker configured with a distinctive value
    var capture = new RequestCapturingCoordinator();
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(capture, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 777;
      options.PollingIntervalMilliseconds = 10_000;  // keep poll wait large; we only need the first claim
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    // Act
    await worker.StartAsync(cts.Token);
    await capture.FirstClaimSignal.Task.WaitAsync(cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(capture.LastRequest).IsNotNull();
    await Assert.That(capture.LastRequest!.MaxStreamsPerBatch).IsEqualTo(777);
  }

  // ==========================================================================
  // 2f: drain pattern — full-batch claim skips poll wait
  // ==========================================================================

  [Test]
  public async Task FullBatchClaim_DrainsWithoutPollWait_MultipleCyclesBackToBackAsync() {
    // Arrange — return 3 full batches of 5 items each, then empty.
    // With a 10s poll interval, if drain isn't working, total runtime would exceed 20s.
    // With drain, all 3 claims complete back-to-back in well under 2s.
    var coordinator = new ScriptedWorkCoordinator();
    coordinator.EnqueueFullBatch(count: 5);
    coordinator.EnqueueFullBatch(count: 5);
    coordinator.EnqueueFullBatch(count: 5);

    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 5;
      options.PollingIntervalMilliseconds = 10_000;
    });

    var worker = services.GetRequiredService<IHostedService>();
    // 15s safety net — same pattern as other drain tests in this file.
    // Real assertion is the completion signal (WaitForClaimsAsync + ClaimCount);
    // deadline is a deadlock guard for slow CI runners. If drain were broken
    // we'd need 30s+ (3 × 10s poll interval) to see 3 claims.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

    // Act
    await worker.StartAsync(cts.Token);
    // Three claims with work-returning state should happen without the poll wait.
    await coordinator.WaitForClaimsAsync(3, cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ClaimCount).IsGreaterThanOrEqualTo(3);
  }

  // ==========================================================================
  // 2f regression: concurrent SignalNewWorkAvailable during drain must not
  // throw SemaphoreFullException.
  // ==========================================================================

  [Test]
  public async Task FullBatchDrain_WithConcurrentSignals_DoesNotThrowSemaphoreFullAsync() {
    // Arrange — return full batches while an external component signals
    // SignalNewWorkAvailable repeatedly. Pre-fix: drain path reset _wakeSignaled=0
    // without consuming the semaphore, letting the next external Release()
    // overflow the 1-count semaphore → SemaphoreFullException.
    var coordinator = new ScriptedWorkCoordinator();
    for (int i = 0; i < 10; i++) {
      coordinator.EnqueueFullBatch(count: 5);
    }

    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 5;
      options.PollingIntervalMilliseconds = 10_000;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

    // Act — start the worker, spam SignalNewWorkAvailable while it drains.
    await worker.StartAsync(cts.Token);
    var signalSpam = Task.Run(async () => {
      while (!cts.Token.IsCancellationRequested) {
        channelWriter.SignalNewWorkAvailable();
        await Task.Yield();
      }
    }, cts.Token);

    try {
      await coordinator.WaitForClaimsAsync(5, cts.Token);
    } catch (OperationCanceledException) {
      // test deadline — that's fine, we only care about NOT observing an exception
    }
    await cts.CancelAsync();
    try { await signalSpam; } catch (OperationCanceledException) { }
    await worker.StopAsync(CancellationToken.None);

    // Assert — drain processed multiple claims and the worker is still alive.
    // If the SemaphoreFullException regression returns, the worker will have
    // crashed and claim count will stall.
    await Assert.That(coordinator.ClaimCount).IsGreaterThanOrEqualTo(3)
      .Because("Concurrent SignalNewWorkAvailable during drain must not crash the worker.");
  }

  // ==========================================================================
  // 2f safety cap: MaxConsecutiveFullDrains
  // ==========================================================================

  [Test]
  public async Task MaxConsecutiveFullDrains_ForcesPollWaitAfterCapReachedAsync() {
    // Arrange — return full batches forever, cap at 2.
    // Worker lifecycle:
    //   - _processInitialWorkBatchAsync (runs before main loop) → claim #1
    //   - main iter 1 → claim #2; drain check: counter 0<2, ++, continue
    //   - main iter 2 → claim #3; drain check: counter 1<2, ++, continue
    //   - main iter 3 → claim #4; drain check: counter 2<2 FALSE, reset, WAIT
    //   - main iter 4 → claim #5 (delayed by the forced poll wait)
    // So claims #1–#4 are back-to-back; gap between #4 and #5 observes the wait.
    // Disable 2g so the wake-signal path can't bypass the cap check.
    var coordinator = new InfiniteFullBatchCoordinator(batchSize: 3);
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 3;
      options.PollingIntervalMilliseconds = 500;
      options.MaxConsecutiveFullDrains = 2;
      options.ImmediateOutboxCompletionFlushThreshold = 0;  // isolate drain behavior
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

    // Act
    await worker.StartAsync(cts.Token);
    await coordinator.WaitForClaimsAsync(5, cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — claims #1..#4 back-to-back; wait kicks in before claim #5.
    var gap34 = coordinator.ClaimTimestamps[3] - coordinator.ClaimTimestamps[2];
    var gap45 = coordinator.ClaimTimestamps[4] - coordinator.ClaimTimestamps[3];
    await Assert.That(gap34.TotalMilliseconds).IsLessThan(300)
      .Because("Claim #3→#4 is still within the drain cap — no forced wait.");
    await Assert.That(gap45.TotalMilliseconds).IsGreaterThanOrEqualTo(400)
      .Because("Cap of 2 reached; worker must wait the poll interval before claim #5.");
  }

  [Test]
  public async Task MaxConsecutiveFullDrainsZero_DisablesCap_ClaimsRunBackToBackAsync() {
    // Arrange — cap = 0 disables the cap entirely. Worker should keep draining
    // with no forced pause as long as batches come back full.
    var coordinator = new InfiniteFullBatchCoordinator(batchSize: 2);
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 2;
      options.PollingIntervalMilliseconds = 10_000;  // absurd; cap-0 means we should never hit it
      options.MaxConsecutiveFullDrains = 0;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // Act
    await worker.StartAsync(cts.Token);
    // If the cap weren't disabled, the 10s PollingIntervalMilliseconds would prevent reaching
    // 10 claims inside the harness deadline. When disabled, all 10 claims drain back-to-back
    // in well under 1s locally; the 30s budget is purely a CI safety net for slow runners
    // where worker startup + coordinator round-trips eat more wall-clock than expected.
    await coordinator.WaitForClaimsAsync(10, cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ClaimCount).IsGreaterThanOrEqualTo(10);
  }

  // ==========================================================================
  // 2g: immediate outbox completion flush
  // ==========================================================================

  [Test]
  public async Task OutboxCompletions_ImmediateFlushWhenThresholdReachedAsync() {
    // Arrange — Worker publishes one work item (success). The completion for that
    // item should reach the coordinator via a SEPARATE ProcessWorkBatchRequest without
    // waiting for the next poll tick.
    // MaxStreamsPerBatch=10, but only 1 item is returned, so drain-pattern doesn't engage
    // (batch not full). Immediate-flush is the ONLY mechanism that can surface the
    // completion within the test window (10s poll interval vs 3s harness deadline).
    var coordinator = new RequestCapturingCoordinator();
    coordinator.EnqueueWorkForNextClaim([_newOutboxWork()]);

    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 10;
      options.PollingIntervalMilliseconds = 10_000;
      options.ImmediateOutboxCompletionFlushThreshold = 1;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

    // Act
    await worker.StartAsync(cts.Token);
    // Wait for a ProcessWorkBatchRequest that carries OutboxCompletions (the immediate flush).
    await coordinator.CompletionReportedSignal.Task.WaitAsync(cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — the completion reached the coordinator without waiting a full poll cycle.
    await Assert.That(coordinator.ReportedCompletions.Count).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task OutboxCompletions_FlushDisabledWhenThresholdZero_WaitsForNextPollAsync() {
    // Arrange — threshold = 0 disables immediate flush. With a 10s poll interval,
    // the completion should NOT reach the coordinator within the test window (1s).
    var coordinator = new RequestCapturingCoordinator();
    coordinator.EnqueueWorkForNextClaim([_newOutboxWork()]);

    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 10;
      options.PollingIntervalMilliseconds = 10_000;
      options.ImmediateOutboxCompletionFlushThreshold = 0;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

    // Act — wait for the first claim (not the completion).
    await worker.StartAsync(cts.Token);
    await coordinator.FirstClaimSignal.Task.WaitAsync(cts.Token);

    // Give the publisher a moment to actually publish. Signal-based via strategy.
    await strategy.AnyPublishCompletedSignal.Task.WaitAsync(cts.Token);

    // Now cancel. The immediate-flush path should NOT have fired.
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — no completions reached the coordinator (next poll is 10s away).
    await Assert.That(coordinator.ReportedCompletions.Count).IsEqualTo(0)
      .Because("With ImmediateOutboxCompletionFlushThreshold=0, completions wait for the next poll cycle.");
  }

  // ==========================================================================
  // 2h: Acknowledge-local-sent-count fix
  // Regression test for the stuck-completion bug: when the SQL processes
  // completions but returns an empty work batch, ack_counts were lost, leaving
  // items in "Sent" state until a slow exponential-backoff reset cycle retried
  // them. Fix: acknowledge what we sent locally instead of relying on metadata.
  // ==========================================================================

  [Test]
  public async Task OutboxCompletions_AreAcknowledgedEvenWhenCoordinatorReturnsEmptyBatchAsync() {
    // Arrange — Worker publishes one message successfully; publisher path calls
    // _completions.Add. On the next ProcessWorkBatchAsync the worker must send
    // that completion AND — because the coordinator response has no work rows
    // carrying ack-count metadata — it must still mark the completion acknowledged
    // locally so it doesn't loop through ResetStale for 5–60 minutes.
    var coordinator = new CompletionTrackingCoordinator();
    coordinator.EnqueueWorkForNextClaim([_newOutboxWork()]);

    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.MaxStreamsPerBatch = 10;
      options.PollingIntervalMilliseconds = 100;
      options.ImmediateOutboxCompletionFlushThreshold = 1;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

    // Act — wait until the coordinator sees an outbox completion for the
    // published message, then wait a further poll cycle.
    await worker.StartAsync(cts.Token);
    await coordinator.FirstCompletionWithEmptyBatchSignal.Task.WaitAsync(cts.Token);
    // Give the worker time to do at least one more poll after the ack-empty cycle.
    await coordinator.PollAfterAckSignal.Task.WaitAsync(cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — the SAME completion must not be re-sent after the initial
    // coordinator processed it. Pre-fix this test would observe the completion
    // resent repeatedly (until a 5min ResetStale cycle); post-fix the tracker
    // acknowledges on the first successful round-trip.
    await Assert.That(coordinator.DistinctCompletionSendCounts.Values.All(c => c == 1)).IsTrue()
      .Because("Each completion should be sent exactly once; stuck-loop would resend.");
  }

  /// <summary>
  /// Coordinator that returns empty WorkBatches and tracks how many times each
  /// completion MessageId is resent across calls.
  /// </summary>
  private sealed class CompletionTrackingCoordinator : IWorkCoordinator {
    public Dictionary<Guid, int> DistinctCompletionSendCounts { get; } = [];
    public TaskCompletionSource FirstCompletionWithEmptyBatchSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource PollAfterAckSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callsAfterFirstCompletion;
    private readonly Queue<List<OutboxWork>> _scriptedWork = new();
    private readonly Lock _lock = new();

    public void EnqueueWorkForNextClaim(List<OutboxWork> work) => _scriptedWork.Enqueue(work);

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      lock (_lock) {
        foreach (var completion in request.OutboxCompletions) {
          DistinctCompletionSendCounts.TryGetValue(completion.MessageId, out var count);
          DistinctCompletionSendCounts[completion.MessageId] = count + 1;
        }

        if (request.OutboxCompletions.Length > 0 && !FirstCompletionWithEmptyBatchSignal.Task.IsCompleted) {
          FirstCompletionWithEmptyBatchSignal.TrySetResult();
        } else if (FirstCompletionWithEmptyBatchSignal.Task.IsCompleted) {
          _callsAfterFirstCompletion++;
          if (_callsAfterFirstCompletion >= 3) {
            PollAfterAckSignal.TrySetResult();
          }
        }

        var work = _scriptedWork.Count > 0 ? _scriptedWork.Dequeue() : [];
        // Intentionally return empty response after the work is consumed — this
        // is the bug-inducing case.
        return Task.FromResult(new WorkBatch {
          OutboxWork = work,
          InboxWork = [],
          PerspectiveWork = []
        });
      }
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // ==========================================================================
  // Adaptive poll backoff on empty polls
  // ==========================================================================

  /// <summary>
  /// Option default: PollingMaxIntervalMilliseconds = 2000 gives idle workers
  /// an 8× max reduction (250 → 500 → 1000 → 2000) while capping worst-case
  /// discovery latency for transport-sourced inbox writes at 2 s.
  /// </summary>
  [Test]
  public async Task WorkCoordinatorPublisherOptions_PollingMaxIntervalMilliseconds_DefaultIs2000Async() {
    var options = new WorkCoordinatorPublisherOptions();
    await Assert.That(options.PollingMaxIntervalMilliseconds).IsEqualTo(2000);
  }

  /// <summary>
  /// Backoff contract: on consecutive empty polls the loop doubles its wait
  /// up to PollingMaxIntervalMilliseconds. With base=50 ms and max=400 ms the
  /// expected doubling schedule is 50, 100, 200, 400, 400, …
  /// Total wall-clock elapsed across 5 empty waits ≥ 500 ms; the fixed-interval
  /// baseline would be ~250 ms (5 × 50 ms). The margin is generous to tolerate
  /// CI scheduler noise.
  /// </summary>
  [Test]
  [NotInParallel("PollBackoff")]
  public async Task WhenEmptyPollsAccumulate_TotalWaitExceedsFixedBaselineAsync() {
    var coordinator = new EmptyBatchCoordinator();
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.PollingIntervalMilliseconds = 50;
      options.PollingMaxIntervalMilliseconds = 400;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    await worker.StartAsync(cts.Token);
    // 6 claims: the startup one + 5 in-loop polls. Those 5 waits should obey
    // the backoff schedule. Fixed: ~250 ms; backoff: ~1150 ms. Assert > 500.
    await coordinator.WaitForClaimsAsync(6, cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    var ts = coordinator.ClaimTimestamps;
    var totalWaitMs = (ts[5] - ts[0]).TotalMilliseconds;
    await Assert.That(totalWaitMs).IsGreaterThanOrEqualTo(500)
      .Because("With base=50ms and max=400ms the backoff schedule (50+100+200+400+400 ≈ 1150ms) "
             + "must make the 5-wait total substantially exceed the fixed-interval baseline (5×50 = 250ms). "
             + "A 500 ms lower bound distinguishes the two with plenty of CI headroom.");
  }

  /// <summary>
  /// Reset contract: a non-empty batch collapses _consecutiveEmptyPolls to zero,
  /// so the next empty poll must wait the BASE interval, not the capped-out one.
  /// </summary>
  [Test]
  [NotInParallel("PollBackoff")]
  public async Task WhenWorkReturnsAfterEmptyPolls_BackoffResetsToBaseAsync() {
    var coordinator = new EmptyThenFullCoordinator(emptyCountBeforeWork: 4);
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.PollingIntervalMilliseconds = 50;
      options.PollingMaxIntervalMilliseconds = 400;
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    await worker.StartAsync(cts.Token);
    // Claims layout: 4 empty → 1 full → next empty post-reset. We need >= 6 claims.
    await coordinator.WaitForClaimsAsync(6, cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // The gap AFTER the full-batch claim (index 4) to the next claim (index 5)
    // should be ~base, not ~max. If reset works, the gap is < 200 ms.
    var postFullGap = (coordinator.ClaimTimestamps[5] - coordinator.ClaimTimestamps[4]).TotalMilliseconds;
    await Assert.That(postFullGap).IsLessThan(200)
      .Because("A non-empty batch must reset _consecutiveEmptyPolls so the next wait returns to "
             + "PollingIntervalMilliseconds (~50 ms). If stuck at the cap the gap would be ~400 ms.");
  }

  /// <summary>
  /// External-wake contract: <see cref="IWorkChannelWriter.SignalNewWorkAvailable"/>
  /// must interrupt a backoff sleep regardless of how far it has climbed. The
  /// existing _pollWakeSignal semaphore already provides this; the adaptive wait
  /// must continue to honour it.
  /// </summary>
  [Test]
  [NotInParallel("PollBackoff")]
  public async Task WhenRequestImmediatePollFires_BackoffIsInterruptedAsync() {
    var coordinator = new EmptyBatchCoordinator();
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.PollingIntervalMilliseconds = 50;
      options.PollingMaxIntervalMilliseconds = 5_000;  // huge cap so interruption is unmistakable
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

    await worker.StartAsync(cts.Token);
    // Let the backoff climb to the cap.
    await coordinator.WaitForClaimsAsync(4, cts.Token);

    // Capture baseline, signal wake, expect the very next claim quickly.
    var beforeSignal = DateTimeOffset.UtcNow;
    channelWriter.SignalNewWorkAvailable();
    await coordinator.WaitForClaimsAsync(1, cts.Token);
    var afterSignal = DateTimeOffset.UtcNow;

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    var signalToClaimMs = (afterSignal - beforeSignal).TotalMilliseconds;
    await Assert.That(signalToClaimMs).IsLessThan(1_000)
      .Because("An external wake signal must short-circuit any in-flight backoff sleep. "
             + "If we waited the full 5 s cap, backoff would be dominating. A 1 s upper bound "
             + "is generous enough to absorb CI noise while still failing if the wake path is broken.");
  }

  /// <summary>
  /// Kill-switch contract: setting PollingMaxIntervalMilliseconds ≤
  /// PollingIntervalMilliseconds disables backoff entirely, returning to the
  /// pre-fix fixed-interval behaviour. Operational escape hatch in case the
  /// adaptive path ever needs to be disabled in production.
  /// </summary>
  [Test]
  [NotInParallel("PollBackoff")]
  public async Task WhenPollingMaxEqualsBase_BackoffIsDisabledAsync() {
    var coordinator = new EmptyBatchCoordinator();
    var strategy = new NoopPublishStrategy();
    var channelWriter = new TestWorkChannelWriter();
    var services = _buildServices(coordinator, strategy, channelWriter, options => {
      options.PollingIntervalMilliseconds = 50;
      options.PollingMaxIntervalMilliseconds = 50;  // kill-switch: equal means disabled
    });

    var worker = services.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

    await worker.StartAsync(cts.Token);
    await coordinator.WaitForClaimsAsync(6, cts.Token);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // With backoff disabled, 5 waits at ~50 ms total ≈ 250 ms. Allow headroom
    // but assert we're not in the hundreds-of-ms-per-wait regime.
    var totalWaitMs = (coordinator.ClaimTimestamps[5] - coordinator.ClaimTimestamps[0]).TotalMilliseconds;
    await Assert.That(totalWaitMs).IsLessThan(600)
      .Because("PollingMaxIntervalMilliseconds=PollingIntervalMilliseconds disables backoff. "
             + "5 fixed 50 ms waits total ~250 ms; a 600 ms upper bound catches CI jitter "
             + "but fails loudly if backoff sneaks in.");
  }

  // ==========================================================================
  // Helpers / fakes
  // ==========================================================================

  /// <summary>
  /// Coordinator that always returns an empty WorkBatch and records the
  /// wall-clock timestamp of every claim. Used by the adaptive-backoff tests
  /// to measure inter-claim gaps.
  /// </summary>
  private sealed class EmptyBatchCoordinator : IWorkCoordinator, IDisposable {
    private readonly SemaphoreSlim _claimCounter = new(0, int.MaxValue);
    public int ClaimCount { get; private set; }
    public List<DateTimeOffset> ClaimTimestamps { get; } = [];

    public void Dispose() => _claimCounter.Dispose();

    public async Task WaitForClaimsAsync(int n, CancellationToken ct) {
      for (int i = 0; i < n; i++) {
        await _claimCounter.WaitAsync(ct);
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      // Ignore immediate-completion flushes — they don't represent a drain tick.
      if (request.OutboxCompletions.Length > 0) {
        return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
      }
      ClaimCount++;
      ClaimTimestamps.Add(DateTimeOffset.UtcNow);
      _claimCounter.Release();
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Coordinator that returns N empty batches, then one batch with a single
  /// work item, then empty batches forever. Used to prove the backoff reset
  /// path: after the full batch, _consecutiveEmptyPolls must return to zero.
  /// </summary>
  private sealed class EmptyThenFullCoordinator(int emptyCountBeforeWork) : IWorkCoordinator, IDisposable {
    private readonly int _emptyCountBeforeWork = emptyCountBeforeWork;
    private readonly SemaphoreSlim _claimCounter = new(0, int.MaxValue);
    public int ClaimCount { get; private set; }
    public List<DateTimeOffset> ClaimTimestamps { get; } = [];

    public void Dispose() => _claimCounter.Dispose();

    public async Task WaitForClaimsAsync(int n, CancellationToken ct) {
      for (int i = 0; i < n; i++) {
        await _claimCounter.WaitAsync(ct);
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      if (request.OutboxCompletions.Length > 0) {
        return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
      }
      ClaimCount++;
      ClaimTimestamps.Add(DateTimeOffset.UtcNow);
      _claimCounter.Release();

      // Return one non-empty batch on the Nth claim, then always empty.
      var isFull = ClaimCount == _emptyCountBeforeWork + 1;
      var work = isFull ? new List<OutboxWork> { _newOutboxWork() } : [];
      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static OutboxWork _newOutboxWork() {
    var id = Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = id,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  private static ServiceProvider _buildServices(
    IWorkCoordinator coordinator,
    IMessagePublishStrategy strategy,
    IWorkChannelWriter channelWriter,
    Action<WorkCoordinatorPublisherOptions> configure) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton(strategy);
    services.AddSingleton(channelWriter);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(
      Guid.NewGuid(), "TestService", "TestHost", Environment.ProcessId));

    var options = new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 100
    };
    configure(options);
    services.AddSingleton(Options.Create(options));

    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  // ==========================================================================
  // Fake: RequestCapturingCoordinator
  // Captures every ProcessWorkBatchRequest and signals first claim / completion report.
  // ==========================================================================

  private sealed class RequestCapturingCoordinator : IWorkCoordinator {
    public ProcessWorkBatchRequest? LastRequest { get; private set; }
    public TaskCompletionSource FirstClaimSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CompletionReportedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<MessageCompletion> ReportedCompletions { get; } = [];

    private readonly Queue<List<OutboxWork>> _scriptedWork = new();

    public void EnqueueWorkForNextClaim(List<OutboxWork> work) {
      _scriptedWork.Enqueue(work);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      LastRequest = request;

      if (request.OutboxCompletions.Length > 0) {
        ReportedCompletions.AddRange(request.OutboxCompletions);
        CompletionReportedSignal.TrySetResult();
      }

      var work = _scriptedWork.Count > 0 ? _scriptedWork.Dequeue() : [];
      FirstClaimSignal.TrySetResult();

      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // ==========================================================================
  // Fake: ScriptedWorkCoordinator
  // Returns a pre-scripted sequence of batches and signals claim counts.
  // ==========================================================================

  private sealed class ScriptedWorkCoordinator : IWorkCoordinator, IDisposable {
    private readonly Queue<List<OutboxWork>> _batches = new();
    private readonly SemaphoreSlim _claimCounter = new(0, int.MaxValue);
    public int ClaimCount { get; private set; }

    public void Dispose() => _claimCounter.Dispose();

    public void EnqueueFullBatch(int count) {
      var batch = new List<OutboxWork>(count);
      for (int i = 0; i < count; i++) {
        batch.Add(_newOutboxWork());
      }
      _batches.Enqueue(batch);
    }

    public async Task WaitForClaimsAsync(int n, CancellationToken ct) {
      for (int i = 0; i < n; i++) {
        await _claimCounter.WaitAsync(ct);
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      ClaimCount++;
      var work = _batches.Count > 0 ? _batches.Dequeue() : [];
      if (work.Count > 0) {
        _claimCounter.Release();
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // ==========================================================================
  // Fake: InfiniteFullBatchCoordinator
  // Always returns MaxStreamsPerBatch-sized batches; records claim timestamps.
  // ==========================================================================

  private sealed class InfiniteFullBatchCoordinator(int batchSize) : IWorkCoordinator, IDisposable {
    private readonly int _batchSize = batchSize;
    private readonly SemaphoreSlim _claimCounter = new(0, int.MaxValue);
    public int ClaimCount { get; private set; }
    public List<DateTimeOffset> ClaimTimestamps { get; } = [];

    public void Dispose() => _claimCounter.Dispose();

    public async Task WaitForClaimsAsync(int n, CancellationToken ct) {
      for (int i = 0; i < n; i++) {
        await _claimCounter.WaitAsync(ct);
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      // Only count requests that are actually claiming work (have non-empty completions list OR are pure-claim requests).
      // For this fake we treat every request as a claim — the coordinator loop always issues these.
      // But skip if this is an immediate-completions-only flush (OutboxCompletions.Length > 0 && no fresh work to return).
      // For this test we want to count true drain cycles only; easiest is to skip when OutboxCompletions is non-empty
      // (that's the immediate-flush call for step 2g).
      if (request.OutboxCompletions.Length > 0) {
        // Immediate completion flush; don't count as a drain claim.
        return Task.FromResult(new WorkBatch {
          OutboxWork = [],
          InboxWork = [],
          PerspectiveWork = []
        });
      }

      ClaimCount++;
      ClaimTimestamps.Add(DateTimeOffset.UtcNow);

      var work = new List<OutboxWork>(_batchSize);
      for (int i = 0; i < _batchSize; i++) {
        work.Add(_newOutboxWork());
      }

      _claimCounter.Release();
      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // ==========================================================================
  // Fake: NoopPublishStrategy — always-ready bulk strategy that auto-succeeds.
  // ==========================================================================

  private sealed class NoopPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource AnyPublishCompletedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      AnyPublishCompletedSignal.TrySetResult();
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      AnyPublishCompletedSignal.TrySetResult();
      IReadOnlyList<MessagePublishResult> results = workItems.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      }).ToList();
      return Task.FromResult(results);
    }
  }

  // ==========================================================================
  // Fake: TestWorkChannelWriter — minimal in-memory channel writer.
  // ==========================================================================

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public void ClearInFlight() { }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }
}
