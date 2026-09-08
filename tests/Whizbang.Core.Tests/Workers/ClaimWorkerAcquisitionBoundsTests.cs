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
/// What the claim loop asks the store for, and when it lets go. Acquisition is bounded in rows (#714),
/// the outstanding budget reads inbox rows only and perspective acquisition has its own cap (#719),
/// stealing from other residues is a last resort (#725), and a sustained re-offer with idle consumers
/// releases the leases that were never started (#724).
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[NotInParallel(Order = 102)]
public class ClaimWorkerAcquisitionBoundsTests {

  // ---- #714: acquisition is bounded in rows -----------------------------------------------------

  [Test]
  public async Task Claim_CarriesARowBoundScaledFromTheStreamWindowAsync() {
    var coord = new ScriptedCoordinator(_ => _emptyBatch());
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
      AdaptiveOutstandingBudget = false,
    });

    await coord.WaitForCallsAsync(1, TimeSpan.FromSeconds(5));

    // The window starts at its floor and the rows-per-stream estimate starts at one, so the very
    // first claim may acquire exactly floor rows: a bound in ROWS, distinct from the stream count.
    await Assert.That(coord.Requests[0].MaxAcquireRows).IsEqualTo(25)
      .Because("the stream count doubled as the row cap in the store, which made a fat stream one row per cycle; "
             + "acquisition needs its own bound in rows");
  }

  [Test]
  public async Task Claim_RowBound_NeverExceedsTheOutstandingCeilingAsync() {
    var coord = new ScriptedCoordinator(_ => _emptyBatch());
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 1000,
      MaxOutstandingInboxRows = 300,
      AdaptiveOutstandingBudget = false,
    });

    await coord.WaitForCallsAsync(1, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Requests[0].MaxAcquireRows).IsEqualTo(300)
      .Because("a wide stream window must not lease more rows than the drain is allowed to hold");
  }

  // ---- #719: the budget reads inbox rows only; perspective has its own cap ------------------------

  [Test]
  public async Task Budget_ReadsInboxRowsOnly_SoAPerspectiveBacklogCannotCloseInboxHeadroomAsync() {
    var coord = new ScriptedCoordinator(_ => _emptyBatch()) {
      // Nothing held in the inbox, a mountain held in perspectives.
      OutstandingToReport = new OutstandingWork { InboxRows = 0, OutboxRows = 0, PerspectiveRows = 50_000 },
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
      MinOutstandingInboxRows = 100,
      MaxOutstandingInboxRows = 10_000,
    }, completionMeter: new WorkCompletionMeter());

    await coord.WaitForCallsAsync(2, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Requests[1].IncludeOutstanding).IsTrue()
      .Because("the budget is on by default and asks the store for the counts on the claim's own round trip");
    await Assert.That(coord.Requests[1].MaxAcquireRows).IsEqualTo(100)
      .Because("the budget is sized in inbox rows, so its headroom is read against inbox rows; folding perspective rows "
             + "in let a perspective backlog starve the inbox behind work it could not affect");
  }

  [Test]
  public async Task Claim_PausesPerspectiveAcquisitionWhileTheDrainBacklogIsAboveItsCapAsync() {
    var drain = new PerspectiveDrainChannel();
    for (var i = 0; i < 5; i++) {
      drain.TryWrite(TrackedGuid.NewMedo().Value);
    }
    var coord = new ScriptedCoordinator(_ => _emptyBatch());
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxPerspectiveDrainBacklog = 3,
    }, perspectiveDrainChannel: drain);

    await coord.WaitForCallsAsync(1, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Requests[0].MaxPerspectiveStreams).IsEqualTo(0)
      .Because("with more stream ids queued than the cap, leasing more perspective work only grows the queue "
             + "ahead of a fixed-parallelism drain; held work is still re-emitted so the drain keeps moving");
  }

  [Test]
  public async Task Claim_LeavesPerspectiveAcquisitionAloneWhileTheDrainBacklogIsUnderItsCapAsync() {
    var drain = new PerspectiveDrainChannel();
    drain.TryWrite(TrackedGuid.NewMedo().Value);
    var coord = new ScriptedCoordinator(_ => _emptyBatch());
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxPerspectiveDrainBacklog = 3,
    }, perspectiveDrainChannel: drain);

    await coord.WaitForCallsAsync(1, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Requests[0].MaxPerspectiveStreams).IsNull()
      .Because("under the cap the store's own stream bound applies; null means 'no separate perspective bound'");
  }

  // ---- #725: stealing is a last resort -------------------------------------------------------------

  [Test]
  public async Task Claim_StealsOnlyAfterTwoConsecutiveEmptyInboxClaimsAsync() {
    var coord = new ScriptedCoordinator(_ => _emptyBatch());
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Requests[0].AllowSteal).IsFalse()
      .Because("the first claim is the instance's own residue; ownership stays stable under normal load");
    await Assert.That(coord.Requests[1].AllowSteal).IsFalse()
      .Because("one empty claim is not evidence of an idle residue");
    await Assert.That(coord.Requests[2].AllowSteal).IsTrue()
      .Because("after two empty own-residue claims an idle instance may take unowned work assigned to other residues");
    await Assert.That(coord.Requests[3].AllowSteal).IsTrue();
  }

  [Test]
  public async Task Claim_StopsStealingOnceOwnWorkReturnsAsync() {
    // Three empty claims, then work arrives on the (stealing) fourth claim.
    var coord = new ScriptedCoordinator(call => call == 4 ? _batchOfInboxStreams(2) : _emptyBatch());
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    });

    await coord.WaitForCallsAsync(5, TimeSpan.FromSeconds(5));

    await Assert.That(coord.Requests[3].AllowSteal).IsTrue();
    await Assert.That(coord.Requests[4].AllowSteal).IsFalse()
      .Because("a claim that returned inbox work resets the empty streak; stealing is never the steady state");
  }

  // ---- #724: a sustained re-offer with idle consumers releases what was never started -------------

  [Test]
  public async Task RepeatedReoffer_ReleasesOnlyTheStreamsNotInFlight_OncePerStreakAsync() {
    var started = TrackedGuid.NewMedo().Value;
    var unstarted = TrackedGuid.NewMedo().Value;
    var batch = new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      InboxStreamIds = [started, unstarted],
    };
    var inboxDrain = new FakeInboxDrainChannel();
    inboxDrain.MarkDraining(started);
    var coord = new ScriptedCoordinator(_ => batch);
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, inboxDrainChannel: inboxDrain);

    await coord.WaitForReleaseAsync(TimeSpan.FromSeconds(10));
    await coord.WaitForCallsAsync(ClaimWorker.RELEASE_UNSTARTED_AFTER_REPEATS + 6, TimeSpan.FromSeconds(10));

    await Assert.That(coord.ReleasedInboxStreams).IsEquivalentTo([unstarted])
      .Because("only work no consumer has begun is given back; a stream mid-drain is the instance's to finish");
    await Assert.That(coord.ReleasedPerspectiveStreams).IsEmpty();
    await Assert.That(coord.ReleaseCalls).IsEqualTo(1)
      .Because("one streak releases once; re-releasing on every later repeat would refund attempts the store already refunded");
    await Assert.That(coord.ReleasedInstanceId).IsEqualTo(coord.InstanceId)
      .Because("the release is the stuck instance's own act, scoped to its own leases");
  }

  [Test]
  public async Task RepeatedReoffer_DoesNotReleaseBeforeTheStreakAsync() {
    var batch = _batchOfInboxStreams(2);
    var coord = new ScriptedCoordinator(_ => batch);
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    });

    // The streak is counted from the second claim (the first is the one being repeated).
    await coord.WaitForCallsAsync(ClaimWorker.RELEASE_UNSTARTED_AFTER_REPEATS, TimeSpan.FromSeconds(10));

    await Assert.That(coord.ReleaseCalls).IsEqualTo(0)
      .Because("a few re-offers are normal while a drain is in progress; releasing early would hand back work "
             + "that was about to be processed");
  }

  [Test]
  public async Task ProductiveClaim_ResetsTheReleaseStreak_SoALaterStuckRunReleasesAgainAsync() {
    // A stuck run, one fresh claim, then a second stuck run on a different work set.
    var first = _batchOfInboxStreams(2);
    var second = _batchOfInboxStreams(2);
    var boundary = ClaimWorker.RELEASE_UNSTARTED_AFTER_REPEATS + 2;
    var coord = new ScriptedCoordinator(call => call <= boundary ? first : second);
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    });

    await coord.WaitForCallsAsync(boundary + ClaimWorker.RELEASE_UNSTARTED_AFTER_REPEATS + 3, TimeSpan.FromSeconds(15));

    await Assert.That(coord.ReleaseCalls).IsEqualTo(2)
      .Because("the streak belongs to a work set; a new work set that also stalls is a new stall");
  }

  [Test]
  public async Task RepeatedReoffer_StoreWithoutTheRelease_KeepsClaimingAsync() {
    var batch = _batchOfInboxStreams(1);
    var coord = new ScriptedCoordinator(_ => batch) { ReleaseThrowsNotImplemented = true };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    });

    await coord.WaitForCallsAsync(ClaimWorker.RELEASE_UNSTARTED_AFTER_REPEATS + 4, TimeSpan.FromSeconds(10));

    await Assert.That(coord.ReleaseCalls).IsGreaterThanOrEqualTo(1)
      .Because("the worker asked; the store declined");
    await Assert.That(coord.Requests.Count).IsGreaterThanOrEqualTo(ClaimWorker.RELEASE_UNSTARTED_AFTER_REPEATS + 4)
      .Because("a store that cannot release must not stop the claim loop; the leases lapse on their own as before");
  }

  // ---- harness -------------------------------------------------------------------------------------

  private static WorkBatch _emptyBatch() => new() { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };

  private static WorkBatch _batchOfInboxStreams(int streams) {
    var ids = new List<Guid>(streams);
    for (var i = 0; i < streams; i++) {
      ids.Add(TrackedGuid.NewMedo().Value);
    }
    return new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], InboxStreamIds = ids };
  }

  private static WorkerHarness _startWorker(
      ScriptedCoordinator coord,
      ClaimWorkerOptions options,
      WorkCompletionMeter? completionMeter = null,
      IPerspectiveDrainChannel? perspectiveDrainChannel = null,
      IInboxDrainChannel? inboxDrainChannel = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstance(coord.InstanceId),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(options),
      NullLogger<ClaimWorker>.Instance,
      perspectiveDrainChannel: perspectiveDrainChannel,
      inboxDrainChannel: inboxDrainChannel,
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

  private sealed class StubInstance(Guid instanceId) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = instanceId;
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

  /// <summary>An inbox drain channel whose in-flight set the test controls.</summary>
  private sealed class FakeInboxDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    private readonly HashSet<Guid> _inFlight = [];
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public bool IsInFlight(Guid streamId) { lock (_inFlight) { return _inFlight.Contains(streamId); } }
    public void MarkDraining(Guid streamId) { lock (_inFlight) { _inFlight.Add(streamId); } }
    public void MarkDrained(Guid streamId) { lock (_inFlight) { _inFlight.Remove(streamId); } }
  }

  /// <summary>
  /// A store that answers each claim from a script keyed by call number (1-based), records every
  /// request verbatim, and records the #724 release.
  /// </summary>
  private sealed class ScriptedCoordinator(Func<int, WorkBatch> script) : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public List<ClaimWorkRequest> Requests { get; } = [];
    public OutstandingWork? OutstandingToReport { get; set; }
    public bool ReleaseThrowsNotImplemented { get; set; }
    public int ReleaseCalls { get; private set; }
    public Guid ReleasedInstanceId { get; private set; }
    public List<Guid> ReleasedInboxStreams { get; } = [];
    public List<Guid> ReleasedPerspectiveStreams { get; } = [];

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      WorkBatch batch;
      lock (_lock) {
        Requests.Add(req);
        var call = Requests.Count;
        batch = script(call);
        if (req.IncludeOutstanding && OutstandingToReport is not null) {
          batch = batch with { Outstanding = OutstandingToReport };
        }
        if (_watchers.TryGetValue(call, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(batch);
    }

    public ValueTask<OutstandingWork?> CountOutstandingWorkAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => ValueTask.FromResult(OutstandingToReport);

    public Task<UnstartedLeaseRelease> ReleaseUnstartedLeasesAsync(
        Guid instanceId, IReadOnlyList<Guid> inboxStreamIds, IReadOnlyList<Guid> perspectiveStreamIds,
        CancellationToken cancellationToken = default) {
      lock (_lock) {
        ReleaseCalls++;
        if (ReleaseThrowsNotImplemented) {
          _released.TrySetResult();
          throw new NotImplementedException("this store does not implement ReleaseUnstartedLeasesAsync.");
        }
        ReleasedInstanceId = instanceId;
        ReleasedInboxStreams.AddRange(inboxStreamIds);
        ReleasedPerspectiveStreams.AddRange(perspectiveStreamIds);
      }
      _released.TrySetResult();
      return Task.FromResult(new UnstartedLeaseRelease(inboxStreamIds.Count, perspectiveStreamIds.Count));
    }

    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (Requests.Count >= n) { return Task.CompletedTask; }
        if (!_watchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _watchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task WaitForReleaseAsync(TimeSpan timeout) => _released.Task.WaitAsync(timeout);

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
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
