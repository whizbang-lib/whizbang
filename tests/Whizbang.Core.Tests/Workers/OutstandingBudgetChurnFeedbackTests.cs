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
/// The outstanding budget must be sized from what the instance is actually holding, read from the
/// store — never from the claim response.
/// </summary>
/// <remarks>
/// <para>
/// <c>claim_work</c> truncates its <c>eligible_*</c> CTEs to the limit the budget just produced, and
/// those CTEs match rows already leased to the caller. A count taken from the response therefore
/// cannot exceed that limit however much work is held, so the control loop reads its own output
/// instead of the system state: headroom always looks abundant, every poll claims more, and held
/// work grows while the number being watched sits still.
/// </para>
/// <para>
/// Observed in production with the bound enabled and arithmetically correct: throughput fell to zero
/// while the instance held roughly twelve times the most the budget would ever have permitted.
/// </para>
/// </remarks>
[NotInParallel(Order = 101)]
[Category("Workers")]
public class OutstandingBudgetChurnFeedbackTests {

  private const int WINDOW_FLOOR = 25;
  private const int BUDGET_FLOOR = 100;
  private const int BUDGET_CEILING = 10_000;

  /// <summary>Completions reported per claim, so the budget sits in a healthy (non-stalled) regime.</summary>
  private const int DRAIN_PER_CYCLE = 500;

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
  /// Reports a FIXED outstanding figure regardless of what it hands back, which is the situation the
  /// store actually presents: the response is truncated, the held total is not.
  /// </summary>
  private sealed class _reportingCoordinator(
      long outstandingRows, bool measurable, WorkCompletionMeter? meter = null) : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];

    public int CallCount { get; private set; }
    /// <summary>The limit asked for by the most recent claim.</summary>
    public int LastStreamsRequested { get; private set; }
    /// <summary>How many times the worker asked the store what it was holding.</summary>
    public int OutstandingProbeCount;

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      var inbox = new List<InboxWork>();
      lock (_lock) {
        CallCount++;
        LastStreamsRequested = req.MaxStreams;
        if (_watchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      // Report healthy drain. Without it the budget's stall rule ("work held, nothing completing →
      // claim nothing") fires and pins the claim to its minimum whatever the outstanding figure is,
      // which would let these tests pass no matter where that figure came from.
      meter?.Record(DRAIN_PER_CYCLE);

      // Hand back exactly what was asked for — the truncated view. If the worker sized its budget
      // from this, it would never see the outstanding total reported below.
      for (var i = 0; i < req.MaxStreams; i++) {
        inbox.Add(new InboxWork {
          MessageId = TrackedGuid.NewMedo().Value,
          MessageType = "TestEvent",
          Envelope = null!,
          Attempts = 1,
        });
      }
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = inbox, PerspectiveWork = [] });
    }

    public ValueTask<OutstandingWork?> CountOutstandingWorkAsync(
        Guid instanceId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref OutstandingProbeCount);
      return ValueTask.FromResult(measurable ? new OutstandingWork { InboxRows = outstandingRows } : null);
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

  private static ClaimWorker _worker(_reportingCoordinator coord, WorkCompletionMeter? meter) {
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
        MinStreamsPerBatch = WINDOW_FLOOR,
        MinOutstandingInboxRows = BUDGET_FLOOR,
        MaxOutstandingInboxRows = BUDGET_CEILING,
      }),
      NullLogger<ClaimWorker>.Instance,
      completionMeter: meter);
  }

  private static async Task<_reportingCoordinator> _runAsync(
      long outstanding, bool measurable, WorkCompletionMeter? meter, int cycles = 6) {
    var coord = new _reportingCoordinator(outstanding, measurable, meter);
    var worker = _worker(coord, meter);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.WaitForCallsAsync(cycles, TimeSpan.FromSeconds(20));
    await worker.StopAsync(CancellationToken.None);
    return coord;
  }

  [Test]
  public async Task ClaimWorker_ShrinksTheClaimWhenTheStoreReportsHeldWorkAboveBudgetAsync() {
    // Holding the entire ceiling with nothing draining: there is no headroom, so the next claim must
    // collapse to its minimum. It must not stay wide open just because the truncated response looked
    // small.
    var coord = await _runAsync(outstanding: BUDGET_CEILING, measurable: true, meter: new WorkCompletionMeter());

    await Assert.That(coord.LastStreamsRequested).IsLessThan(WINDOW_FLOOR)
      .Because("the budget must respond to what the STORE reports is held, not to the size of the "
             + "claim response — the response is truncated by the very limit being computed, so "
             + "sizing from it leaves the bound permanently wide open");
  }

  [Test]
  public async Task ClaimWorker_KeepsClaimingWhenTheStoreReportsNothingHeldAsync() {
    // The mirror image, and the reason the claim never sizes to zero: an instance holding nothing
    // must not throttle itself. Polling is the only thing that observes outstanding work, so a
    // worker that stops claiming cannot discover it has recovered.
    var coord = await _runAsync(outstanding: 0, measurable: true, meter: new WorkCompletionMeter());

    await Assert.That(coord.LastStreamsRequested).IsGreaterThanOrEqualTo(WINDOW_FLOOR)
      .Because("an idle instance must claim at its window, or a bound meant to prevent over-claim "
             + "becomes a throughput ceiling nobody asked for");
  }

  [Test]
  public async Task ClaimWorker_LeavesTheBoundDisengagedWhenTheStoreCannotReportOutstandingAsync() {
    // Unmeasurable is not zero and not "full". Throttling against a figure that was never read
    // presents as an unexplained performance collapse, so the bound stands down instead.
    var coord = await _runAsync(outstanding: 0, measurable: false, meter: new WorkCompletionMeter());

    await Assert.That(coord.LastStreamsRequested).IsGreaterThanOrEqualTo(WINDOW_FLOOR)
      .Because("a backend that cannot report outstanding work must degrade to no bound rather than "
             + "to a silent floor");

    // The answer is latched, not re-asked. A backend either implements the count or it does not, so
    // re-probing would spend a round trip per poll and repeat the same warning forever to say so.
    await Assert.That(coord.OutstandingProbeCount).IsEqualTo(1)
      .Because("'cannot measure' is a property of the backend, not a transient condition — asking "
             + "again every cycle adds a query per poll to the hot claim path and turns a one-time "
             + "diagnostic into log noise that hides it");
  }

  [Test]
  public async Task ClaimWorker_LeavesTheBoundDisengagedWithoutAMeterAsync() {
    // No meter means no measured drain, so the budget has no rate to size itself from.
    var coord = await _runAsync(outstanding: BUDGET_CEILING, measurable: true, meter: null);

    await Assert.That(coord.LastStreamsRequested).IsGreaterThanOrEqualTo(WINDOW_FLOOR)
      .Because("an unmeasured budget must not throttle — it would look exactly like a performance "
             + "problem with no signal pointing at the throttle");
  }
}
