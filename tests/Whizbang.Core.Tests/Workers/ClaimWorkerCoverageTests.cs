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

    public WorkBatch BatchToReturn {
      get { lock (_lock) { return _batchToReturn; } }
      set { lock (_lock) { _batchToReturn = value; } }
    }

    public TaskCompletionSource HeartbeatAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int HeartbeatCallCount { get; private set; }

    /// <summary>When set, RecordHeartbeatAsync throws this instead of succeeding.</summary>
    public Exception? HeartbeatException { get; set; }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      WorkBatch batch;
      lock (_lock) {
        CallCount++;
        LastMaxStreams = req.MaxStreams;
        batch = _batchToReturn;
        if (_watchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(batch);
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

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) {
      lock (_lock) { HeartbeatCallCount++; }
      HeartbeatAttempted.TrySetResult();
      return HeartbeatException is not null
        ? Task.FromException<bool>(HeartbeatException)
        : Task.FromResult(true);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  private static (ClaimWorker Worker, RecordingCoordinator Coord) _build(
      RecordingCoordinator coord,
      ClaimWorkerOptions options,
      ISchemaReadyGate? schemaGate = null,
      IWorkChannelWriter? outboxChannel = null,
      IPerspectiveChannelWriter? perspectiveChannel = null,
      ClaimChurnFeedback? churnFeedback = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstance(),
      new NoOpWorkNotificationListener(),
      schemaGate ?? SchemaReadyGate.AlreadyReady(),
      Options.Create(options),
      NullLogger<ClaimWorker>.Instance,
      outboxChannel: outboxChannel,
      perspectiveChannel: perspectiveChannel,
      churnFeedback: churnFeedback);
    return (worker, coord);
  }

  private sealed class WorkerHarness(ClaimWorker worker, CancellationTokenSource cts) : IDisposable {
    public void Dispose() {
      cts.Cancel();
      try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
      cts.Dispose();
    }
  }

  private static WorkerHarness _startWorker(
      RecordingCoordinator coord,
      ClaimWorkerOptions options,
      IWorkChannelWriter? outboxChannel = null,
      IPerspectiveChannelWriter? perspectiveChannel = null,
      ClaimChurnFeedback? churnFeedback = null) {
    var (worker, _) = _build(coord, options, outboxChannel: outboxChannel, perspectiveChannel: perspectiveChannel, churnFeedback: churnFeedback);
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return new WorkerHarness(worker, cts);
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
    var gate = new SchemaReadyGate(); // Never marked ready — the wait blocks until canceled.
    var coord = new RecordingCoordinator();
    var (worker, _) = _build(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, schemaGate: gate);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
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
        OutboxWork = ids.Select(id => new OutboxWork {
          MessageId = id,
          Envelope = null!,
          EnvelopeType = "TestEvent",
          MessageType = "TestEvent",
          Attempts = 1,
          Destination = "test",
        }).ToList(),
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
        PerspectiveWork = streamIds.Select(sid => new PerspectiveWork {
          WorkId = TrackedGuid.NewMedo().Value,
          StreamId = sid,
          PerspectiveName = "Test.Perspective",
          LastProcessedEventId = null,
          PartitionNumber = 1,
        }).ToList(),
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
    var cleanRowIds = Enumerable.Range(0, 5)
      .Select(_ => TrackedGuid.NewMedo().Value)
      .ToList();
    var coord = new RecordingCoordinator {
      // Phase 1: fully materialized, first-attempt rows — a real, clean, MEASURABLE cycle that lets
      // the window grow well above its floor so a later narrowing is actually visible.
      BatchToReturn = new WorkBatch {
        OutboxWork = [],
        PerspectiveWork = [],
        InboxWork = cleanRowIds.Select(id => new InboxWork {
          MessageId = id,
          MessageType = "TestEvent",
          Envelope = null!,
          Attempts = 1,
        }).ToList(),
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
    var grownWidth = coord.LastMaxStreams;
    await Assert.That(grownWidth).IsGreaterThan(25)
      .Because("the window must actually have grown above its floor here, or a later narrowing "
             + "would not be distinguishable from the window simply never having moved");

    // Phase 2: switch to the stream-id-only shape (no materialized attempts) and report heavy
    // re-claim churn through the SAME path the drain worker uses — the claim response alone cannot
    // see this churn on this shape.
    churnFeedback.Report([.. Enumerable.Repeat(2, 9), 1]); // 9 of 10 rows re-claimed
    coord.BatchToReturn = new WorkBatch {
      OutboxWork = [],
      PerspectiveWork = [],
      InboxWork = [],
      InboxStreamIds = Enumerable.Range(0, 4).Select(_ => TrackedGuid.NewMedo().Value).ToList(),
    };

    // A generous buffer of cycles past the swap: however many polls it takes the swapped batch and
    // churn report to actually land (at most one or two), the shrink must be visible well before
    // this many more have gone by, and nothing after it can grow the window back (the swapped batch
    // reports zero churn on every later poll, which reads as UNMEASURED, not clean).
    await coord.WaitForCallsAsync(15, TimeSpan.FromSeconds(5));

    await Assert.That(coord.LastMaxStreams).IsLessThan(grownWidth)
      .Because("churn fed in externally by the drain worker must narrow the window exactly as if "
             + "ClaimWorker had observed the re-claims itself — without the reconstruction, the "
             + "stream-id path stays blind to the condition the window exists to correct, and the "
             + "window would only have kept growing");
  }
}
