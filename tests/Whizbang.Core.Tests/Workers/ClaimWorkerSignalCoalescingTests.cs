using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// 2026-06-12 production forensic — locks in the wake-coalescing contract for the
/// <see cref="ClaimWorker"/> under bulk-NOTIFY storms. The v0.686 store-level
/// <c>notify_instance_owners</c> hook emits one pg_notify per store call, so a
/// 17 000-event bulk import fires 17 000+ NOTIFYs against the BFF replica's
/// <c>wh_work_i_{instance_id}</c> channel. Each NOTIFY translates to a
/// <see cref="ClaimWorker.RequestImmediatePoll"/> invocation via the listener's
/// <c>OnSignal</c> handler.
///
/// <para>The architectural invariant: NOTIFY is edge-triggered ("there might be
/// work") not queued ("do work N times"). Claim cycles are idempotent — a single
/// <c>claim_work</c> call picks up every eligible row for this instance, so 100
/// signals arriving while a claim is in-flight must collapse to AT MOST one
/// follow-up claim. Anything else multiplies DB load proportional to signal
/// count and undoes the value of the cold-start NOTIFY.</para>
/// </summary>
[NotInParallel(Order = 101)]
public class ClaimWorkerSignalCoalescingTests {

  [Test]
  public async Task SignalStormDuringBusyClaim_CollapsesToAtMostOneFollowupClaimAsync() {
    // ARRANGE — coordinator that BLOCKS its first ClaimWorkAsync call until released,
    // simulating an in-flight claim cycle during which signals arrive.
    var coord = new BlockingFakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions {
        // Long backoff so the natural poll cadence doesn't add calls during the
        // observation window — every claim_work call within ~500 ms after the
        // release MUST be signal-driven.
        PollingIntervalMilliseconds = 60_000,
        PollingMaxIntervalMilliseconds = 60_000,
        NotifyHealthyPollingIntervalMilliseconds = null
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // ACT 1 — wait until the worker is inside ClaimWorkAsync (busy state).
    await coord.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

    // ACT 2 — fire a signal storm while the claim is in-flight.
    const int SignalStorm = 100;
    for (var i = 0; i < SignalStorm; i++) {
      worker.RequestImmediatePoll();
    }

    // ACT 3 — release the blocked claim. The worker should now complete the in-flight
    // call, see the (coalesced) pending wake, and run EXACTLY ONE follow-up claim.
    coord.ReleaseFirstCall();

    // ACT 4 — wait for the follow-up claim to happen (call #2). If signals were
    // queued without coalescing, calls #3..#101 would also fire here.
    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(2));

    // ACT 5 — give the loop enough time that a multi-fire bug would manifest. We
    // wait for call #3 with a short timeout; if it arrives, the bug is real
    // (queued signals firing back-to-back). If it times out, calls have settled.
    var thirdCallTask = coord.WaitForCallsAsync(3, TimeSpan.FromMilliseconds(500));
    try { await thirdCallTask; } catch (TimeoutException) { /* expected — no 3rd call */ }

    // ASSERT — total calls must be tightly bounded. With proper coalescing we expect
    // exactly 2 (initial + one follow-up). Allow a small slack (≤ 4) to absorb any
    // legitimate startup/heartbeat-driven activity without admitting the bug.
    var observed = coord.CallCount;
    await Assert.That(observed).IsLessThanOrEqualTo(4)
      .Because("Signal-storm coalescing invariant: 100 RequestImmediatePoll() calls during a busy claim cycle MUST collapse to AT MOST one follow-up claim — anything more means the wake mechanism is leaking signal count into claim_work call count, the regression observed on production during bulk imports.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class BlockingFakeCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly System.Collections.Generic.Dictionary<int, TaskCompletionSource> _callWatchers = [];
    private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstCallRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount { get; private set; }
    public TaskCompletionSource FirstCallStarted => _firstCallStarted;
    public void ReleaseFirstCall() => _firstCallRelease.TrySetResult();

    public async Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      bool isFirst;
      lock (_lock) {
        CallCount++;
        isFirst = CallCount == 1;
        if (_callWatchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      if (isFirst) {
        _firstCallStarted.TrySetResult();
        await _firstCallRelease.Task.WaitAsync(ct).ConfigureAwait(false);
      }
      return new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };
    }

    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (CallCount >= n) { return Task.CompletedTask; }
        if (!_callWatchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _callWatchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("not used");
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
