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
/// The outstanding budget bounds work this instance holds but has not finished. It can only do that
/// if the outstanding figure is measured independently of the claim. Deriving it from the claim's
/// own response cannot work, because the claim truncates its eligible set to the limit the budget
/// just produced — so the worker observes its own output and never sees the backlog it is holding.
/// </summary>
/// <remarks>
/// <para>
/// The store's claim applies <c>LIMIT p_max_streams</c> to rows that are already leased to this
/// instance and unprocessed. Every held row stays eligible, so a worker holding tens of thousands of
/// rows still sees only <c>p_max_streams</c> of them. Sizing the next claim from that count reads as
/// abundant headroom no matter how much is actually held.
/// </para>
/// <para>
/// Observed in production: throughput fell to zero while the instance held roughly twelve times the
/// most the bound would ever have permitted, and thousands of rows were charged a retry attempt they
/// never used. The bound was enabled and correct arithmetically — it was bounding against a number
/// that could not exceed it.
/// </para>
/// <para>
/// <see cref="_truncatingCoordinator"/> reproduces the store's behavior exactly: eligibility spans
/// held and unheld rows, oldest first, truncated to the requested limit. Claiming small therefore
/// re-offers rows already held rather than taking new ones, which is what lets a correct bound hold
/// steady instead of creeping.
/// </para>
/// </remarks>
[NotInParallel(Order = 101)]
[Category("Workers")]
public class OutstandingBudgetChurnFeedbackTests {

  private const int BACKLOG = 5_000;
  private const int BUDGET_FLOOR = 100;

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

  /// <summary>
  /// Models the claim exactly as SQL performs it: a backlog of rows, all of them eligible, ordered
  /// oldest-first and cut off at the caller's limit. Rows handed back become leased and STAY
  /// eligible, because nothing here ever completes them.
  /// </summary>
  private sealed class _truncatingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Guid[] _rows = new Guid[BACKLOG];
    private readonly HashSet<Guid> _held = [];
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];

    public int CallCount { get; private set; }

    /// <summary>Distinct rows this instance has ever taken a lease on and not released.</summary>
    public int HeldCount { get { lock (_lock) { return _held.Count; } } }

    /// <summary>Largest limit the worker ever asked for.</summary>
    public int MaxStreamsRequested { get; private set; }

    public _truncatingCoordinator() {
      for (var i = 0; i < BACKLOG; i++) { _rows[i] = TrackedGuid.NewMedo().Value; }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      var inbox = new List<InboxWork>();
      lock (_lock) {
        CallCount++;
        if (req.MaxStreams > MaxStreamsRequested) { MaxStreamsRequested = req.MaxStreams; }

        // Oldest-first across the WHOLE backlog, held and unheld alike — then truncated. This is
        // the LIMIT that makes the returned count useless as an outstanding measure.
        var take = Math.Min(req.MaxStreams, BACKLOG);
        for (var i = 0; i < take; i++) {
          _held.Add(_rows[i]);
          inbox.Add(new InboxWork {
            MessageId = _rows[i],
            MessageType = "TestEvent",
            Envelope = null!,
            Attempts = 1,
          });
        }

        if (_watchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = inbox, PerspectiveWork = [] });
    }

    /// <summary>The true figure, counted in the store and never truncated.</summary>
    public ValueTask<OutstandingWork?> CountOutstandingWorkAsync(
        Guid instanceId, CancellationToken cancellationToken = default) {
      lock (_lock) {
        return ValueTask.FromResult<OutstandingWork?>(new OutstandingWork { InboxRows = _held.Count });
      }
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

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
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

  private static ClaimWorker _worker(_truncatingCoordinator coord, WorkCompletionMeter? meter) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 1,
        PollingMaxIntervalMilliseconds = 5,
        MinOutstandingInboxRows = BUDGET_FLOOR,
        MaxOutstandingInboxRows = 10_000,
      }),
      NullLogger<ClaimWorker>.Instance,
      completionMeter: meter);
  }

  [Test]
  public async Task ClaimWorker_DoesNotAccumulateTheWholeBacklogWhenNothingCompletesAsync() {
    var coord = new _truncatingCoordinator();
    // A meter that never records: nothing is draining, so the budget must never grow past its floor.
    var worker = _worker(coord, new WorkCompletionMeter());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(40, TimeSpan.FromSeconds(20));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coord.HeldCount).IsLessThan(BACKLOG / 2)
      .Because($"nothing completed, so the instance must not take a lease on most of a {BACKLOG}-row "
             + "backlog — held work it cannot drain inside the lease is charged a retry attempt it "
             + "never uses, which is what dead-letters healthy messages as MaxAttemptsExceeded");
  }

  [Test]
  public async Task ClaimWorker_BoundsHeldWorkNearTheBudgetFloorWhenDrainIsZeroAsync() {
    var coord = new _truncatingCoordinator();
    var worker = _worker(coord, new WorkCompletionMeter());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(40, TimeSpan.FromSeconds(20));
    await worker.StopAsync(CancellationToken.None);

    // The floor is the cold-start budget and no drain was ever observed, so the budget never rises
    // above it. Slack covers the one in-flight claim that can be sized before the newest count lands.
    await Assert.That(coord.HeldCount).IsLessThanOrEqualTo(BUDGET_FLOOR * 3)
      .Because("with zero measured drain the budget stays at its floor, so held work should settle "
             + "there rather than creep — a bound that merely slows unbounded growth still ends at "
             + "full lease saturation, which is exactly how the previous attempt failed");
  }

  [Test]
  public async Task ClaimWorker_WithoutAMeterLeavesTheBoundDisengagedAsync() {
    var coord = new _truncatingCoordinator();
    // No meter: drain is unmeasurable, so the bound must not engage. Throttling against a rate that
    // was never read presents as an unexplained performance collapse.
    var worker = _worker(coord, meter: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(5, TimeSpan.FromSeconds(20));
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coord.MaxStreamsRequested).IsGreaterThan(BUDGET_FLOOR)
      .Because("an unmeasured budget must degrade to no bound rather than to a silent floor");
  }
}
