using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage round 23 — targets a handful of narrow gaps in <see cref="ClaimWorker"/>:
/// the constructor's outbox-channel doorbell wiring, the two early-return shutdown paths in
/// <c>ExecuteAsync</c> (a canceled schema-ready wait and a canceled startup heartbeat), the
/// outbox/perspective handoff loops in <c>_distributeAsync</c>, and the cross-worker churn-feedback
/// reconstruction in <c>_claimOnceAsync</c> that lets the stream-id claim path see re-claim churn it
/// otherwise cannot observe directly.
/// </summary>
public class ClaimWorkerCoverageTests {

  private sealed class StubInstance : IServiceInstanceProvider {
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

  /// <summary>
  /// A configurable, call-counting <see cref="IWorkCoordinator"/>. <see cref="BatchToReturn"/> is
  /// safe to swap mid-test — the claim loop and the test thread both go through the same lock — so a
  /// test can change what the next claim sees without tearing the worker down.
  /// </summary>
  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];
    private WorkBatch _batchToReturn = new() { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };

    public int CallCount { get; private set; }
    public int LastMaxStreams { get; private set; }

    /// <summary>
    /// The widest window ever requested.
    /// </summary>
    /// <remarks>
    /// A snapshot of <see cref="LastMaxStreams"/> taken at some cycle is not the width the window
    /// reached: it keeps growing while clean batches keep being returned, so on a loaded machine
    /// more cycles run before a test's next step lands and the window ends up wider than whatever
    /// was recorded. A narrowing then measures against a stale number and looks like no narrowing
    /// at all. The peak is the honest comparison point.
    /// </remarks>
    public int PeakMaxStreams { get; private set; }

    public WorkBatch BatchToReturn {
      get { lock (_lock) { return _batchToReturn; } }
      set { lock (_lock) { _batchToReturn = value; } }
    }

    public TaskCompletionSource HeartbeatAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int HeartbeatCallCount { get; private set; }

    /// <summary>When set, RecordHeartbeatAsync throws this instead of succeeding.</summary>
    public Exception? HeartbeatException { get; set; }

    /// <summary>Thrown by the next claim only, then cleared: one failed tick, then normal service.</summary>
    public Exception? NextClaimException { get; set; }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      WorkBatch batch;
      Exception? failure;
      lock (_lock) {
        CallCount++;
        LastMaxStreams = request.MaxStreams;
        if (request.MaxStreams > PeakMaxStreams) { PeakMaxStreams = request.MaxStreams; }
        batch = BatchToReturn;
        failure = NextClaimException;
        NextClaimException = null;
        if (_watchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      return failure is not null ? Task.FromException<WorkBatch>(failure) : Task.FromResult(batch);
    }

    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (CallCount >= n) { return Task.CompletedTask; }
        if (!_watchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _watchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      lock (_lock) { HeartbeatCallCount++; }
      HeartbeatAttempted.TrySetResult();
      return HeartbeatException is not null
        ? Task.FromException<bool>(HeartbeatException)
        : Task.FromResult(true);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// A never-ready schema gate that publishes when a waiter arrives.
  /// </summary>
  /// <remarks>
  /// Distinguishes "the worker is parked on the closed gate" from "the thread pool has not
  /// dequeued ExecuteAsync yet". Only the first makes zero claims and zero heartbeats evidence
  /// that the early return fired, rather than evidence that nothing ran.
  /// </remarks>
  private sealed class SignallingSchemaGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the moment the worker begins waiting on this gate.</summary>
    public Task Entered => _entered.Task;

    public bool IsReady => _ready.Task.IsCompleted;
    public void MarkReady() => _ready.TrySetResult();

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      return _ready.Task.WaitAsync(cancellationToken);
    }
  }

  private static (ClaimWorker Worker, RecordingCoordinator Coord) _build(
      RecordingCoordinator coord,
      ClaimWorkerOptions options,
      ISchemaReadyGate? schemaGate = null,
      IWorkChannelWriter? outboxChannel = null,
      IPerspectiveChannelWriter? perspectiveChannel = null,
      ClaimChurnFeedback? churnFeedback = null,
      Microsoft.Extensions.Logging.ILogger<ClaimWorker>? logger = null) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new StubInstance(),
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: schemaGate ?? SchemaReadyGate.AlreadyReady(),
      options: Options.Create(options),
      logger: logger ?? NullLogger<ClaimWorker>.Instance,
      outboxChannel: outboxChannel ?? new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: perspectiveChannel ?? new PerspectiveChannelWriter(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance,
      churnFeedback: churnFeedback);
    return (worker, coord);
  }

  private sealed class WorkerHarness(ClaimWorker worker, CancellationTokenSource cts) : IDisposable {
    public void Dispose() {
      cts.Cancel();
      try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }
      cts.Dispose();
    }
  }

  private static WorkerHarness _startWorker(
      RecordingCoordinator coord,
      ClaimWorkerOptions options,
      IWorkChannelWriter? outboxChannel = null,
      IPerspectiveChannelWriter? perspectiveChannel = null,
      ClaimChurnFeedback? churnFeedback = null,
      Microsoft.Extensions.Logging.ILogger<ClaimWorker>? logger = null) {
    var (worker, _) = _build(coord, options, outboxChannel: outboxChannel, perspectiveChannel: perspectiveChannel, churnFeedback: churnFeedback, logger: logger);
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return new WorkerHarness(worker, cts);
  }

  /// <summary>
  /// A deadlock inside one claim is the database's business, not the loop's: the tick is reported
  /// with the classified reason and the next tick runs. The loop already survived every exception;
  /// what an operator could not tell from the line was whether it was a passing failure or a defect.
  /// </summary>
  [Test]
  public async Task ClaimTick_TransientDatabaseFailure_IsReportedWithItsReasonAndTheNextTickRunsAsync() {
    var coord = new RecordingCoordinator { NextClaimException = FakeDbException.WithSqlState("40P01", message: "deadlock detected") };
    var logger = new Microsoft.Extensions.Logging.Testing.FakeLogger<ClaimWorker>();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 10,
      PollingMaxIntervalMilliseconds = 50,
    }, logger: logger);

    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(10));

    var reported = logger.Collector.GetSnapshot()
      .Where(e => e.Id.Id == ClaimWorker.TRANSIENT_FAILURE_EVENT_ID).ToList();
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Warning);
    await Assert.That(reported[0].Message).Contains(TransientDatabaseFailure.DEADLOCK, StringComparison.Ordinal);
    await Assert.That(reported[0].Message).Contains("40P01", StringComparison.Ordinal);
    await Assert.That(reported[0].Exception).IsTypeOf<FakeDbException>();
  }

  // ============================================================
  // Constructor: outbox-channel doorbell wiring (lines 177-178)
  // ============================================================

  /// <summary>
  /// A new outbox row persisted through the synchronous store-and-publish path must wake the claim
  /// loop immediately. If the constructor stops subscribing to the outbox channel's
  /// OnNewWorkAvailable event, that row sits until the adaptive backoff's next tick — reintroducing
  /// the poll-tick latency this doorbell exists to remove, invisibly, since nothing throws.
  /// </summary>
  [Test]
  public async Task OutboxChannelSignal_WakesTheClaimLoopImmediatelyAsync() {
    var coord = new RecordingCoordinator();
    var outboxChannel = new WorkChannelWriter();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      // Deliberately huge: a second claim inside the short wait below can only be explained by the
      // doorbell, never by the ordinary backstop poll landing early.
      PollingIntervalMilliseconds = 300_000,
      PollingMaxIntervalMilliseconds = 300_000,
    }, outboxChannel: outboxChannel);

    await coord.WaitForCallsAsync(1, TimeSpan.FromSeconds(5));
    outboxChannel.SignalNewWorkAvailable();
    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(5));

    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(2)
      .Because("the outbox channel's OnNewWorkAvailable must be wired to SignalNewWork in the "
             + "constructor; without it a freshly persisted row waits out the full adaptive backoff "
             + "instead of being noticed immediately");
  }

  // ============================================================
  // ExecuteAsync: canceled schema-ready wait (line 344)
  // ============================================================

  /// <summary>
  /// A canceled schema-ready wait must stop the worker outright, before the loop ever gets near
  /// claim_work. If this catch were dropped (or widened into a catch-all that logs and falls
  /// through), a shutdown landing while migrations are still pending would either fault the worker
  /// or let it race into the claim loop against a database nobody ever confirmed was ready.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task SchemaGateWaitCanceled_StopsTheWorkerBeforeAnyClaimAsync(CancellationToken testToken) {
    var gate = new SignallingSchemaGate(); // Never marked ready — the wait blocks until canceled.
    var coord = new RecordingCoordinator();
    var (worker, _) = _build(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, schemaGate: gate);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);

    // Wait until the worker is provably parked on the gate before canceling. StartAsync only
    // queues ExecuteAsync via Task.Run(_, stoppingToken); Task.Run never runs the delegate when
    // the token is already canceled at dequeue time and the task settles Canceled instead — which
    // satisfies IsCompleted, leaves IsFaulted false, and leaves both call counts at zero. Every
    // assertion below was therefore equally true of a worker that never started, so canceling
    // straight after StartAsync tested the thread pool rather than the catch.
    await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await cts.CancelAsync();

    // ExecuteTask completing on its own is the signal that the early return fired, rather than the
    // loop trying to run against a schema nobody confirmed was ready.
    // SuppressThrowing because the task may end RanToCompletion (the catch swallowed the
    // cancellation) or Canceled (the token was already canceled when ExecuteAsync first ran),
    // depending on how quickly the thread pool picks it up. Both are graceful exits; rethrowing
    // one of them makes this test fail only under a loaded suite, which is what it did.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("a host shutting down mid-migration must not leave the claim loop parked on the gate");

    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("canceling the schema-ready wait is an ordinary shutdown path, not an error — it "
             + "must not fault the worker");
    await Assert.That(coord.CallCount).IsEqualTo(0)
      .Because("the gate never opened, so claim_work must never have been issued against an "
             + "unmigrated schema");
    await Assert.That(coord.HeartbeatCallCount).IsEqualTo(0)
      .Because("the early return happens before the startup heartbeat registration runs too");

    await worker.StopAsync(CancellationToken.None);
    worker.Dispose();
  }

  // ============================================================
  // ExecuteAsync: canceled startup heartbeat (line 356)
  // ============================================================

  /// <summary>
  /// A canceled startup heartbeat must also stop the worker outright, distinct from an ordinary
  /// registration failure (which is non-fatal and lets the loop proceed — see the sibling
  /// AFailedStartupRegistration tests). If this catch is dropped, or subsumed by the generic
  /// Exception branch beneath it, a shutdown racing the registration call would let ClaimWorker fall
  /// through into the claim loop moments before the host tears its scope down — trading a clean stop
  /// for a background failure nobody is awaiting.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task HeartbeatCanceledDuringStartup_StopsTheWorkerWithoutClaimingAsync(CancellationToken testToken) {
    var coord = new RecordingCoordinator {
      HeartbeatException = new OperationCanceledException("simulated shutdown mid-registration"),
    };
    var (worker, _) = _build(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    });

    // Note: the worker's own lifetime token is never canceled here. That isolates the catch itself —
    // if the early return were missing, nothing else would stop the loop from claiming.
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    await coord.HeartbeatAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    // ExecuteTask completing on its own, with the lifetime token still live, is the signal: the
    // early return fired rather than the loop carrying on to claim work.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a canceled startup heartbeat is a shutdown-in-progress signal, not an error");
    await Assert.That(coord.CallCount).IsEqualTo(0)
      .Because("the early return must happen before the claim loop is ever entered — falling "
             + "through would let a claim slip in during teardown");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
    worker.Dispose();
  }

  // ============================================================
  // _distributeAsync: outbox and perspective handoff loops (lines 537-540, 577-578)
  // ============================================================

  /// <summary>
  /// Claimed outbox rows must actually reach OutboxPublishWorker's channel. If this loop is dropped,
  /// every claimed outbox row is leased and forgotten — it sits until the lease expires, gets
  /// silently re-claimed at another spent attempt, and is never actually published.
  /// </summary>
  [Test]
  public async Task ClaimedOutboxWork_IsWrittenToTheOutboxChannelAsync() {
    var ids = new[] { TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value };
    var coord = new RecordingCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [.. ids.Select(id => new OutboxWork {
          MessageId = id,
          Envelope = null!,
          EnvelopeType = "TestEvent",
          MessageType = "TestEvent",
          Attempts = 1,
          Destination = "test",
        })],
        InboxWork = [],
        PerspectiveWork = [],
      }
    };
    var outboxChannel = new WorkChannelWriter();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 50,
      PollingMaxIntervalMilliseconds = 200,
    }, outboxChannel: outboxChannel);

    var seen = new HashSet<Guid>();
    for (var i = 0; i < ids.Length; i++) {
      using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      var work = await outboxChannel.Reader.ReadAsync(readCts.Token);
      seen.Add(work.MessageId);
    }

    await Assert.That(seen.Count).IsEqualTo(ids.Length)
      .Because("every claimed outbox row must reach the publish channel exactly once per poll — "
             + "fewer than claimed means rows are being silently dropped on handoff");
    await Assert.That(seen.SetEquals(ids)).IsTrue()
      .Because("the exact set of claimed message ids must arrive on the channel, not merely some "
             + "count of items");
  }

  /// <summary>
  /// Claimed perspective work must actually reach PerspectiveWorker's channel. If this loop is
  /// dropped, a perspective row is leased but never materialized — PerspectiveWorker simply never
  /// sees it, and the stream's perspectives silently stop advancing.
  /// </summary>
  [Test]
  public async Task ClaimedPerspectiveWork_IsWrittenToThePerspectiveChannelAsync() {
    var streamIds = new[] { TrackedGuid.NewMedo().Value, TrackedGuid.NewMedo().Value };
    var coord = new RecordingCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [.. streamIds.Select(sid => new PerspectiveWork {
          WorkId = TrackedGuid.NewMedo().Value,
          StreamId = sid,
          PerspectiveName = "Test.Perspective",
          LastProcessedEventId = null,
          PartitionNumber = 1,
        })],
        // A real store populates the stream-id list alongside the rows, and it has to be set here
        // too: ClaimWorker's "did this claim find anything" test reads PerspectiveStreamIds, not
        // PerspectiveWork. A batch carrying rows but no stream ids reads as an empty poll and is
        // never distributed -- the rows stay leased and nothing consumes them.
        PerspectiveStreamIds = [.. streamIds],
      }
    };
    var perspectiveChannel = new PerspectiveChannelWriter();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 50,
      PollingMaxIntervalMilliseconds = 200,
    }, perspectiveChannel: perspectiveChannel);

    var seen = new HashSet<Guid>();
    for (var i = 0; i < streamIds.Length; i++) {
      using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      var work = await perspectiveChannel.Reader.ReadAsync(readCts.Token);
      seen.Add(work.StreamId);
    }

    await Assert.That(seen.Count).IsEqualTo(streamIds.Length)
      .Because("every claimed perspective work item must reach PerspectiveWorker's channel exactly "
             + "once per poll — fewer than claimed means rows are being silently dropped on handoff");
    await Assert.That(seen.SetEquals(streamIds)).IsTrue()
      .Because("the exact set of claimed stream ids must arrive on the channel, not merely some "
             + "count of items");
  }

  // ============================================================
  // _claimOnceAsync: churn-feedback reconstruction (lines 771-778)
  // ============================================================

  /// <summary>
  /// Re-claim churn observed by the drain worker (which fetches rows for the stream-id claim path,
  /// where the claim response itself never sees an attempt count) must still narrow ClaimWorker's own
  /// claim window on the next cycle. Without this reconstruction, a claim that only returns stream
  /// ids reads as unmeasured forever — the window can never learn that the batch outruns dispatch, no
  /// matter how badly the instance is thrashing.
  /// </summary>
  [Test]
  public async Task ChurnFeedback_NarrowsTheClaimWindowOnTheStreamIdPathAsync() {
    var churnFeedback = new ClaimChurnFeedback();
    // Floor-wide, not merely non-empty: develop's adaptive-window rule is that a claim NARROWER
    // than MinStreamsPerBatch does not move the window at all, and only a floor-wide clean claim
    // grows it. Five rows against a floor of 25 leaves the window pinned at its floor, so the
    // later narrowing would be indistinguishable from the window never having moved.
    var cleanRowIds = Enumerable.Range(0, 30)
      .Select(_ => TrackedGuid.NewMedo().Value)
      .ToList();
    var coord = new RecordingCoordinator {
      // Phase 1: fully materialized, first-attempt rows — a real, clean, MEASURABLE cycle that lets
      // the window grow well above its floor so a later narrowing is actually visible.
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        PerspectiveWork = [],
        InboxWork = cleanRowIds.ConvertAll(id => new InboxWork {
          MessageId = id,
          MessageType = "TestEvent",
          Envelope = null!,
          Attempts = 1,
        }),
      }
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 2000,
      MinStreamsPerBatch = 25,
      ClaimWindowGrowthStep = 100,
      AdaptiveClaimWindow = true,
    }, churnFeedback: churnFeedback);

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));
    await Assert.That(coord.LastMaxStreams).IsGreaterThan(25)
      .Because("the window must actually have grown above its floor here, or a later narrowing "
             + "would not be distinguishable from the window simply never having moved");

    // Phase 2: switch to the stream-id-only shape (no materialized attempts) and report heavy
    // re-claim churn through the SAME path the drain worker uses — the claim response alone cannot
    // see this churn on this shape.
    // Floor-wide sample: AdaptiveClaimWindow now ignores any sample narrower than the floor, in
    // BOTH directions. That rule exists because one re-offered row reads as 100% churn on paper,
    // and a loop that halved on it walked a 1000-stream window down to the floor in under a second
    // while the queue held a single row. So the churn has to be reported over at least
    // MinStreamsPerBatch rows to be considered at all.
    coord.BatchToReturn = new WorkBatch {
      OutboxWork = [],
      PerspectiveWork = [],
      InboxWork = [],
      // Floor-wide here too: under the new rule a claim narrower than MinStreamsPerBatch does not
      // move the window in EITHER direction, so a 4-id batch would leave the window pinned and the
      // narrowing this test exists to prove could never be observed.
      InboxStreamIds = [.. Enumerable.Range(0, 30).Select(_ => TrackedGuid.NewMedo().Value)],
    };

    // The swap has to be visible to a WHOLE cycle before the churn is reported, and that ordering
    // is the difference between this test measuring adaptivity and measuring the scheduler.
    // ClaimWorker takes the feedback unconditionally and destructively on every cycle
    // (_churnFeedback?.Take() in _observeChurn), so a cycle still holding the clean phase-1 batch
    // will consume this report and spend it against thirty first-attempt rows. The evidence is then
    // gone, every later cycle reads Observed=0, which is UNMEASURED rather than clean, and the
    // window neither grows nor shrinks again: the assertion below sees the width equal to the peak
    // and reports a failure to narrow that is really a failure to deliver the sample. Reported
    // once a cycle that BEGAN after the swap has completed, the churn can only ever be consumed
    // against the stream-id shape, which is the path under test.
    var callsAtSwap = coord.CallCount;
    await coord.WaitForCallsAsync(callsAtSwap + 2, TimeSpan.FromSeconds(5));

    churnFeedback.Report([.. Enumerable.Repeat(2, 27), .. Enumerable.Repeat(1, 3)]); // 27 of 30 re-claimed

    // One more cycle is all the shrink needs, since the very next claim consumes the report; the
    // buffer is for a loaded machine, not for the mechanism. Nothing here can grow the window back,
    // because an unmeasured cycle blocks growth exactly as an unmeasured drain does.
    await coord.WaitForCallsAsync(coord.CallCount + 4, TimeSpan.FromSeconds(5));

    // Compared against the peak rather than a width sampled before the swap. Growth continues
    // until the swapped batch lands, so a sampled width is only a lower bound on how wide the
    // window actually got, and on a loaded machine the extra cycles pushed the real peak above it
    // — leaving a genuine narrowing still above the sample. Nothing after the swap can grow the
    // window, so the peak is the pre-swap width by construction.
    await Assert.That(coord.LastMaxStreams).IsLessThan(coord.PeakMaxStreams)
      .Because("churn fed in externally by the drain worker must narrow the window exactly as if "
             + "ClaimWorker had observed the re-claims itself — without the reconstruction, the "
             + "stream-id path stays blind to the condition the window exists to correct, and the "
             + "window would only have kept growing");
  }
}
