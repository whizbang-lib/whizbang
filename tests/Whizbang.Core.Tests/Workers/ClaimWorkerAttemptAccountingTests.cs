using System.Text.Json;
using System.Threading.Channels;
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
/// Covers the two claim-side behaviours that stop a backlog spending its own retry budget.
///
/// <para>
/// A claim charges an attempt per row whether or not it is dispatched, so rows a worker claims and
/// never reaches are re-claimed at another attempt each cycle and eventually dead-lettered as
/// <c>MaxAttemptsExceeded</c> having never met a receptor. ClaimWorker therefore (a) narrows its
/// claim when work is coming back re-claimed rather than finished, and (b) hands back anything its
/// handoff loop did not actually deliver.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[NotInParallel(Order = 101)]
public class ClaimWorkerAttemptAccountingTests {

  [Test]
  public async Task ReClaimedWork_NarrowsTheClaimWindowAsync() {
    var coord = new RecordingCoordinator {
      // Every row arrives with attempts > 1 — this instance keeps re-claiming work it never
      // finishes, which is exactly the over-claim signal.
      BatchToReturn = _batchOf(rows: 4, attempts: 5)
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    await Assert.That(coord.LastMaxStreams).IsLessThan(1000)
      .Because("sustained re-claims mean the batch outruns what this instance can dispatch inside "
             + "its lease; every untouched row is silently spending an attempt it never used");
    await Assert.That(coord.LastMaxStreams).IsGreaterThanOrEqualTo(25)
      .Because("the window must never collapse below its floor — a stalled worker is worse than an "
             + "oversized batch");
  }

  [Test]
  public async Task CleanWork_GrowsTheClaimWindowInsteadOfNarrowingItAsync() {
    var coord = new RecordingCoordinator {
      // attempts == 1 is a FIRST claim, not a re-claim: nothing to correct.
      BatchToReturn = _batchOf(rows: 4, attempts: 1)
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 500,
      MinStreamsPerBatch = 25,
      ClaimWindowGrowthStep = 25,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    // This assertion was previously "== 500", which held only because the window was CONSTRUCTED at
    // the ceiling. That made it a test of the starting value rather than of the behaviour it names.
    // The window now starts at the floor (cold start is when over-claiming does its damage), so the
    // real property is directional: clean traffic must GROW the window, never narrow it.
    await Assert.That(coord.LastMaxStreams).IsGreaterThan(25)
      .Because("first-claim traffic is evidence of spare capacity — a healthy worker must be "
             + "allowed to widen rather than stay pinned at its cautious starting point");
    await Assert.That(coord.LastMaxStreams).IsLessThanOrEqualTo(500)
      .Because("the operator's configured batch size remains the ceiling; the window may approach "
             + "it but never exceed it");
  }

  [Test]
  public async Task AdaptiveWindowDisabled_PinsTheClaimAtTheCeilingAsync() {
    var coord = new RecordingCoordinator {
      BatchToReturn = _batchOf(rows: 4, attempts: 9)
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 700,
      MinStreamsPerBatch = 25,
      AdaptiveClaimWindow = false,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    await Assert.That(coord.LastMaxStreams).IsEqualTo(700)
      .Because("the opt-out must be a real opt-out — heavy churn notwithstanding, an operator who "
             + "pins the batch gets the batch they pinned");
  }

  /// <summary>
  /// The handoff loop charges every claimed row an attempt up front. If it cannot deliver them all,
  /// the undelivered remainder must be handed back rather than left holding a lease and a spent
  /// attempt it never used.
  /// </summary>
  [Test]
  public async Task UndeliveredHandoff_ReleasesTheRowsItCouldNotDispatchAsync() {
    var coord = new RecordingCoordinator {
      BatchToReturn = _batchOf(rows: 3, attempts: 1)
    };
    // A channel that refuses the very first write stands in for shutdown, a full channel, or a
    // faulting writer — every case where the loop stops with rows still in hand.
    var refusing = new RefusingInboxChannel();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, inboxChannel: refusing);

    await coord.WaitForReleaseAsync(TimeSpan.FromSeconds(5));

    await Assert.That(coord.ReleasedIds.Count).IsGreaterThan(0)
      .Because("rows the loop never delivered have paid an attempt for a dispatch that did not "
             + "happen; without the hand-back they pay again every cycle until they dead-letter");
    await Assert.That(coord.ReleasedInstanceId).IsNotEqualTo(Guid.Empty)
      .Because("the release must name the claiming instance — an unscoped release could unlock a "
             + "row another worker is actively dispatching");
  }

  /// <summary>
  /// A release that fails must not take the worker down with it. This path runs during shutdown,
  /// where throwing would turn a clean stop into a crash — and the orchestrator reads a non-zero
  /// exit as a failed pod, restarts it, and the next shutdown does the same thing. The cost of
  /// swallowing is one attempt per row; the cost of throwing is a crash loop.
  /// </summary>
  [Test]
  public async Task ReleaseFailure_IsSwallowedSoShutdownStaysCleanAsync() {
    var coord = new RecordingCoordinator {
      BatchToReturn = _batchOf(rows: 2, attempts: 1),
      ThrowOnRelease = true,
    };
    var refusing = new RefusingInboxChannel();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, inboxChannel: refusing);

    // The worker must keep claiming after a release blew up — a faulting hand-back is recoverable,
    // a dead poller is not.
    await coord.WaitForCallsAsync(3, TimeSpan.FromSeconds(5));

    await Assert.That(coord.ReleaseAttempts).IsGreaterThan(0)
      .Because("the release must actually have been attempted — a test that passes because nothing "
             + "was tried would prove nothing");
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(3)
      .Because("a failing release must not stop the claim loop; the rows keep their attempt and are "
             + "re-claimed later, which is strictly better than a worker that stops polling");
  }

  /// <summary>
  /// A coordinator that has not implemented the hand-back must fail LOUDLY. The tempting default is
  /// a silent no-op, but that would leave every undelivered row silently charged on a store whose
  /// author never opted in — the exact invisible budget drain this work exists to remove. A thrown
  /// NotImplementedException surfaces the gap the first time a worker tries.
  /// </summary>
  [Test]
  public async Task CoordinatorWithoutRelease_ThrowsRatherThanSilentlySucceedingAsync() {
    IWorkCoordinator bare = new MinimalCoordinator();

    Exception? caught = null;
    try {
      await bare.ReleaseUnprocessedInboxAsync(TrackedGuid.NewMedo().Value, [TrackedGuid.NewMedo().Value]);
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsTypeOf<NotImplementedException>()
      .Because("a silent default would let rows keep paying attempts on a store that never wired the "
             + "hand-back, reproducing the drain invisibly instead of reporting it");
  }

  /// <summary>A coordinator implementing nothing beyond the required members.</summary>
  private sealed class MinimalCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  // ==================== helpers ====================

  private static WorkBatch _batchOf(int rows, int attempts) {
    var inbox = new List<InboxWork>(rows);
    for (var i = 0; i < rows; i++) {
      inbox.Add(new InboxWork {
        MessageId = TrackedGuid.NewMedo().Value,
        MessageType = "TestEvent",
        Envelope = null!,
        Attempts = attempts,
      });
    }
    return new WorkBatch { OutboxWork = [], InboxWork = inbox, PerspectiveWork = [] };
  }




  // ==================== Handoff dedup ====================

  /// <summary>
  /// claim_work re-emits every row still leased to this instance and unprocessed on EVERY poll, so
  /// the same row arrives in successive batches until it is processed. Writing it to the channel
  /// each time queues duplicate copies of work already in flight.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The dedup is safe here ONLY because in-flight entries now age out. An earlier IsInFlight
  /// write-time filter on this path was unrecoverable in production: a flag stranded by a hung or
  /// canceled task made the worker discard that row's emits forever, and only a restart cleared it.
  /// With ageing, a stranded flag stops mattering once the lease has lapsed — the row becomes
  /// eligible again on its own.
  /// </para>
  /// <para>
  /// A skipped row must NOT be treated as undispatched. It was handed off on an earlier poll and is
  /// being processed; refunding its attempt would credit work that is genuinely in progress.
  /// </para>
  /// </remarks>
  [Test]
  public async Task Handoff_DoesNotReWriteWorkAlreadyInFlightAsync() {
    var coord = new RecordingCoordinator { BatchToReturn = _batchOf(rows: 3, attempts: 1) };
    var channel = new _CountingInboxChannel();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, inboxChannel: channel);

    // The coordinator returns the SAME three rows every poll, exactly as claim_work re-emits rows
    // that remain leased and unprocessed.
    await coord.WaitForCallsAsync(5, TimeSpan.FromSeconds(5));

    await Assert.That(channel.DistinctMessagesWritten).IsEqualTo(3)
      .Because("the same three rows are re-offered on every poll; without dedup each poll queues "
             + "another copy of work already in flight");
    await Assert.That(channel.TotalWrites).IsEqualTo(3)
      .Because("a row already in flight must be skipped outright, not re-queued — duplicates cost "
             + "channel depth and dispatch effort on work that is already being processed");
    await Assert.That(coord.ReleasedIds.Count).IsEqualTo(0)
      .Because("a skipped row was handed off on an earlier poll and is being processed; refunding "
             + "its attempt would credit work that is genuinely in progress");
  }

  /// <summary>Counts writes and reports everything written as permanently in flight.</summary>
  private sealed class _CountingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    private readonly HashSet<Guid> _seen = [];
    public int TotalWrites { get; private set; }
    public int DistinctMessagesWritten => _seen.Count;

    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      lock (_seen) {
        TotalWrites++;
        _seen.Add(work.MessageId);
      }
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool IsInFlight(Guid messageId) {
      lock (_seen) {
        return _seen.Contains(messageId);
      }
    }

    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  // ==================== Outstanding-work budget ====================

  /// <summary>
  /// A batch spanning all three work kinds. Every one of them is leased and charges an attempt, so
  /// the budget has to see the total — counting one column would let the identical over-claim
  /// arithmetic recur in another.
  /// </summary>
  private static WorkBatch _mixedBatch(int inboxRows, int outboxRows, int perspectiveRows) {
    var inbox = new List<InboxWork>(inboxRows);
    for (var i = 0; i < inboxRows; i++) {
      inbox.Add(new InboxWork {
        MessageId = TrackedGuid.NewMedo().Value,
        MessageType = "TestEvent",
        Envelope = null!,
        Attempts = 1,
      });
    }
    var outbox = new List<OutboxWork>(outboxRows);
    for (var i = 0; i < outboxRows; i++) {
      outbox.Add(new OutboxWork {
        MessageId = TrackedGuid.NewMedo().Value,
        Envelope = null!,
        EnvelopeType = "TestEvent",
        MessageType = "TestEvent",
        Attempts = 1,
        Destination = "test",
      });
    }
    var perspective = new List<PerspectiveWork>(perspectiveRows);
    for (var i = 0; i < perspectiveRows; i++) {
      perspective.Add(new PerspectiveWork {
        WorkId = TrackedGuid.NewMedo().Value,
        StreamId = TrackedGuid.NewMedo().Value,
        PerspectiveName = "Test.Perspective",
        LastProcessedEventId = null,
        PartitionNumber = 1,
      });
    }
    return new WorkBatch { OutboxWork = outbox, InboxWork = inbox, PerspectiveWork = perspective };
  }

  [Test]
  public async Task OutstandingBudget_CountsAllThreeWorkKindsNotJustInboxAsync() {
    // The meter MUST be fed here. With no completions the drain rate is zero, the stall rule zeroes
    // headroom, and the claim clamps to 1 no matter how outstanding is counted — so the test would
    // pass for the wrong reason and could not tell the two readings apart. (Verified: an earlier
    // version of this test failed to detect inbox-only counting for exactly that reason.)
    var meter = new WorkCompletionMeter();
    // 200 inbox + 400 outbox + 400 perspective = 1000 outstanding. Inbox alone reads 200.
    var coord = new RecordingCoordinator { BatchToReturn = _mixedBatch(200, 400, 400) };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 5000,
      MinStreamsPerBatch = 25,
      MinOutstandingInboxRows = 100,
      // The budget ceiling sits BETWEEN the two readings on purpose: 1000 counted across all three
      // exhausts it, 200 counted from inbox alone leaves 300 rows of headroom. Without this the
      // claim window is the binding constraint and the test passes either way — which is exactly
      // how two earlier versions of this test managed to be vacuous.
      MaxOutstandingInboxRows = 500,
    }, completionMeter: meter);

    // Feed the meter so a drain rate exists. With none, the stall rule zeroes headroom regardless of
    // how outstanding is counted, and the readings again become indistinguishable.
    for (var i = 0; i < 8; i++) {
      meter.Record(400);
      await coord.WaitForCallsAsync(i + 2, TimeSpan.FromSeconds(5));
    }

    await Assert.That(coord.LastMaxStreams).IsLessThanOrEqualTo(1)
      .Because("outbox and perspective rows hold leases and charge attempts exactly as inbox rows "
             + "do — at 1000 held against a 500 budget there is no headroom at all, whereas counting "
             + "inbox alone would see 200, find 300 rows spare, and keep claiming");
  }

  [Test]
  public async Task OutstandingBudget_WithoutAMeter_DoesNotEngageAtAllAsync() {
    // Same over-budget batch, but nothing can measure drain.
    var coord = new RecordingCoordinator { BatchToReturn = _mixedBatch(40, 40, 40) };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
      MinOutstandingInboxRows = 100,
    });

    await coord.WaitForCallsAsync(3, TimeSpan.FromSeconds(5));

    // Without measurement the drain rate would read zero forever. If the bound still engaged, the
    // stall rule would zero headroom and pin every claim at 1 — an unexplained throughput collapse
    // with nothing in the logs. Declining to bound at all is the honest behaviour: no measurement,
    // no bound. Asserting well above the floor is what distinguishes "not engaged" from "engaged
    // and clamped", which a `> 25` assertion could not.
    await Assert.That(coord.LastMaxStreams).IsGreaterThan(50)
      .Because("an unmeasured budget throttles silently and presents as a performance mystery — "
             + "absent a meter the bound must not engage rather than clamp on a rate it cannot read");
  }

  [Test]
  public async Task OutstandingBudget_NeverSizesTheClaimToZeroAsync() {
    // Far beyond any budget: 500 rows held against a 100-row floor, with no completions recorded.
    var coord = new RecordingCoordinator { BatchToReturn = _mixedBatch(500, 0, 0) };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
      MinOutstandingInboxRows = 100,
    }, completionMeter: new WorkCompletionMeter());

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    // The poll is the ONLY thing that observes outstanding work. A worker that stops polling can
    // never discover it has recovered, which is precisely how the first version of this deadlocked.
    // Re-emitting rows already leased to us charges no new attempt, so polling stays cheap.
    await Assert.That(coord.LastMaxStreams).IsGreaterThanOrEqualTo(1)
      .Because("the claim must never be sized to zero — polling is the only observation channel, so "
             + "a worker that stops polling cannot see that it is healthy again");
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(4)
      .Because("claims must keep happening while over budget; the bound narrows the claim, it does "
             + "not suspend the loop");
  }

  [Test]
  public async Task OutstandingBudget_GrowsOnRecordedCompletionsAsync() {
    var meter = new WorkCompletionMeter();
    var coord = new RecordingCoordinator { BatchToReturn = _mixedBatch(120, 0, 0) };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
      MinOutstandingInboxRows = 100,
    }, completionMeter: meter);

    // Completions are an OBSERVED EVENT, not a difference between readings. That is what lets this
    // assert on the control loop without depending on wall-clock behaviour, and what stops arriving
    // work from masking drain and understating capacity.
    for (var i = 0; i < 20; i++) {
      meter.Record(200);
      await coord.WaitForCallsAsync(i + 2, TimeSpan.FromSeconds(5));
    }

    await Assert.That(coord.LastMaxStreams).IsGreaterThan(1)
      .Because("a worker demonstrably draining hundreds of rows per interval has earned more than "
             + "the cautious floor; the budget must widen on measured throughput or it would pin a "
             + "healthy service at its cold-start value forever");
  }

  private static WorkerHarness _startWorker(
      RecordingCoordinator coord,
      ClaimWorkerOptions options,
      IInboxChannelWriter? inboxChannel = null,
      WorkCompletionMeter? completionMeter = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstance(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(options),
      NullLogger<ClaimWorker>.Instance,
      inboxChannel: inboxChannel,
      completionMeter: completionMeter);
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return new WorkerHarness(worker, cts);
  }

  private sealed class WorkerHarness(ClaimWorker worker, CancellationTokenSource cts) : IDisposable {
    public void Dispose() {
      cts.Cancel();
      try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
      cts.Dispose();
    }
  }

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
  /// Refuses every write — standing in for shutdown, a full channel, or a faulting writer: any case
  /// where the handoff loop stops with claimed rows still in hand.
  /// </summary>
  private sealed class RefusingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public event Action? OnNewInboxWorkAvailable { add { } remove { } }
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) =>
      throw new InvalidOperationException("channel refuses writes");
    public bool TryWrite(InboxWork work) => false;
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.TryComplete();
    public void SignalNewInboxWorkAvailable() { }
  }

  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount { get; private set; }
    public int LastMaxStreams { get; private set; }
    public List<Guid> ReleasedIds { get; } = [];
    public Guid ReleasedInstanceId { get; private set; }
    public int ReleaseAttempts { get; private set; }
    public bool ThrowOnRelease { get; set; }
    public WorkBatch BatchToReturn { get; set; } = new() { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      lock (_lock) {
        CallCount++;
        LastMaxStreams = req.MaxStreams;
        if (_watchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(BatchToReturn);
    }

    /// <summary>
    /// Reports the work this fake store is holding, across all three kinds.
    /// </summary>
    /// <remarks>
    /// The worker no longer infers outstanding from the claim response, because the real claim
    /// truncates its eligible set to the limit being computed — a count taken from it can never
    /// exceed that limit. It asks the store instead, so a fake that does not answer leaves the
    /// budget disengaged (by design) and any test of the bound would silently measure nothing.
    /// </remarks>
    public ValueTask<OutstandingWork?> CountOutstandingWorkAsync(
        Guid instanceId, CancellationToken cancellationToken = default) {
      lock (_lock) {
        return ValueTask.FromResult<OutstandingWork?>(new OutstandingWork {
          InboxRows = BatchToReturn.InboxWork.Count,
          OutboxRows = BatchToReturn.OutboxWork.Count,
          PerspectiveRows = BatchToReturn.PerspectiveWork.Count
        });
      }
    }

    public Task<int> ReleaseUnprocessedInboxAsync(
        Guid instanceId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) {
      lock (_lock) {
        ReleaseAttempts++;
        if (ThrowOnRelease) {
          _released.TrySetResult();
          throw new InvalidOperationException("release failed");
        }
        ReleasedInstanceId = instanceId;
        ReleasedIds.AddRange(messageIds);
      }
      _released.TrySetResult();
      return Task.FromResult(messageIds.Count);
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

    public Task WaitForReleaseAsync(TimeSpan timeout) => _released.Task.WaitAsync(timeout);

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
