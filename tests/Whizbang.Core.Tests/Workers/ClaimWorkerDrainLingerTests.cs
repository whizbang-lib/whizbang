using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>Locks the C# half of the doorbell debounce (issue #665): after a claim finds fresh
/// work, the worker LINGERS — empty polls run at a tight ~500 ms cadence for
/// <c>NotifyDrainLingerSeconds</c> (default 8) before the notify-healthy elevation and the
/// adaptive backoff resume. The linger is what makes the SQL-side suppression safe: while
/// producers suppress doorbells toward this instance (its <c>wh_notify_state</c> watermark
/// is fresher than the 7 s SQL window), the linger polls are the guaranteed pickup. The
/// 8 s &gt; 7 s asymmetry means the suppression self-expires while the drainer is still
/// polling — no stranded message, no sleep handshake, clock skew up to the margin is safe.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[NotInParallel(Order = 105)]
public class ClaimWorkerDrainLingerTests {

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

  private sealed class AvailableGate : INotifySignalingGate {
    public bool IsAvailable => true;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged { add { } remove { } }
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  /// <summary>Returns fresh work on the calls listed in <see cref="WorkOnCalls"/>, empty otherwise.</summary>
  private sealed class ScriptedCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private int _calls;
    public HashSet<int> WorkOnCalls { get; init; } = [1];
    public List<DateTimeOffset> ClaimCallTimes { get; } = [];
    private readonly List<TaskCompletionSource> _signals =
      [.. Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
    public Task CallReached(int oneBasedCall) => _signals[oneBasedCall - 1].Task;

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      int call;
      lock (_lock) {
        call = ++_calls;
        ClaimCallTimes.Add(DateTimeOffset.UtcNow);
        if (call <= _signals.Count) { _signals[call - 1].TrySetResult(); }
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        OutboxStreamIds = WorkOnCalls.Contains(call) ? [TrackedGuid.NewMedo()] : [],
      });
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  }

  private static ClaimWorker _buildWorker(
      ScriptedCoordinator coord, ClaimWorkerOptions options, SignalBusLivenessState? liveness = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    return new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      schemaGate,
      Options.Create(options),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate(),
      busLiveness: liveness);
  }

  [Test]
  public async Task AfterFreshWork_EmptyPollsStayTight_DespiteNotifyHealthyElevationAsync() {
    // Work on call 1 starts the linger. Calls 2 and 3 are empty polls INSIDE the linger
    // window — they must run at the tight linger cadence, not the 4 s notify-healthy
    // elevation. This is what guarantees a doorbell suppressed by the SQL debounce is
    // picked up within the margin.
    var coord = new ScriptedCoordinator { WorkOnCalls = [1] };
    var worker = _buildWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 50,
      PollingMaxIntervalMilliseconds = 10_000,
      NotifyHealthyPollingIntervalMilliseconds = 4_000,
    });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.CallReached(3).WaitAsync(TimeSpan.FromSeconds(30));

    var gap1to2 = coord.ClaimCallTimes[1] - coord.ClaimCallTimes[0];
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap1to2).IsLessThan(TimeSpan.FromSeconds(2))
      .Because("inside the drain linger the poll cadence must stay within the SQL debounce "
             + "margin — the elevated idle cadence would let a suppressed doorbell's work "
             + "sit for seconds");
    await Assert.That(gap2to3).IsLessThan(TimeSpan.FromSeconds(2))
      .Because("every empty poll inside the linger keeps the tight cadence, not just the first");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task FreshWorkFoundByLingerPoll_NoMissedDoorbellRecordedAsync() {
    // Call 1 finds work (linger starts), call 2 is empty, call 3 finds fresh work on the
    // empty→non-empty edge WITHOUT a doorbell — because the SQL debounce suppressed it.
    // That is EXPECTED inside the linger; recording it as a missed doorbell would flag the
    // debounce itself as a NOTIFY outage.
    var coord = new ScriptedCoordinator { WorkOnCalls = [1, 3] };
    var liveness = new SignalBusLivenessState();
    var worker = _buildWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 50,
      PollingMaxIntervalMilliseconds = 10_000,
      NotifyHealthyPollingIntervalMilliseconds = null,
    }, liveness);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.CallReached(4).WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(liveness.ConsecutiveMissedDoorbells).IsEqualTo(0)
      .Because("a poll-discovered edge inside the linger is the debounce working as designed "
             + "— expected-unnotified, not a dropped doorbell");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task LingerDisabled_ElevatedCadenceAppliesImmediatelyAsync() {
    // NotifyDrainLingerSeconds = 0 is the off switch: pre-#665 behavior exactly — the
    // notify-healthy elevation governs straight after work. Operators disabling the SQL
    // debounce set this to 0 in the same breath.
    var coord = new ScriptedCoordinator { WorkOnCalls = [1] };
    var worker = _buildWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 50,
      PollingMaxIntervalMilliseconds = 10_000,
      NotifyHealthyPollingIntervalMilliseconds = 2_500,
      NotifyDrainLingerSeconds = 0,
    });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.CallReached(2).WaitAsync(TimeSpan.FromSeconds(30));

    var gap1to2 = coord.ClaimCallTimes[1] - coord.ClaimCallTimes[0];
    await Assert.That(gap1to2).IsGreaterThan(TimeSpan.FromSeconds(2))
      .Because("with the linger off, the elevated idle cadence applies immediately after "
             + "work — the off switch restores exact prior behavior");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task LingerExpires_ElevationResumesAsync() {
    // A short 1 s linger: the polls right after work are tight, and once the window lapses
    // the elevated cadence resumes — the linger is a bounded tail, not a permanent tighten.
    var coord = new ScriptedCoordinator { WorkOnCalls = [1] };
    var worker = _buildWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 50,
      PollingMaxIntervalMilliseconds = 10_000,
      NotifyHealthyPollingIntervalMilliseconds = 2_500,
      NotifyDrainLingerSeconds = 1,
    });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.CallReached(4).WaitAsync(TimeSpan.FromSeconds(30));

    var gap1to2 = coord.ClaimCallTimes[1] - coord.ClaimCallTimes[0];
    var laterGaps = new[] {
      coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1],
      coord.ClaimCallTimes[3] - coord.ClaimCallTimes[2],
    };
    await Assert.That(gap1to2).IsLessThan(TimeSpan.FromSeconds(2))
      .Because("the first empty poll after work sits inside the 1 s linger");
    await Assert.That(laterGaps.Max()).IsGreaterThan(TimeSpan.FromSeconds(2))
      .Because("past the linger the elevated cadence resumes — the tight polling is a "
             + "bounded tail matched to the SQL debounce window, never a new idle floor");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

}
