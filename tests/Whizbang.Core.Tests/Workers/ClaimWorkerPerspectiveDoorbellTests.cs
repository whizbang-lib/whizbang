using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The perspective doorbell must wake the claim loop on the LEGACY (no signal bus) path.
/// With the bus wired, <c>WorkPerspectiveAvailableSignal</c> already routes to ClaimWorker;
/// without it, the listener's raw <c>OnSignal</c> is the only wake path — and ClaimWorker is
/// the sole component that can claim perspective streams and feed the drain channel, so a
/// dropped perspective doorbell leaves the apply waiting on the relaxed poll cadence. The
/// post-stamp doorbell (fenced-visibility fix) rings with the 'perspective' payload, making
/// this the exact signal that bounds fenced perspective visibility.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
[NotInParallel(Order = 102)]
public class ClaimWorkerPerspectiveDoorbellTests {

  [Test]
  public async Task PerspectiveDoorbell_WithoutSignalBus_WakesClaimLoopAsync() {
    // ARRANGE — polling cadence parked far out so any second claim inside the observation
    // window must be signal-driven.
    var coord = new CountingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var listener = new RaisableListener();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 60_000,
        PollingMaxIntervalMilliseconds = 60_000,
        NotifyHealthyPollingIntervalMilliseconds = null
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);
    await coord.NthCallSeen(1).WaitAsync(TimeSpan.FromSeconds(5));

    // ACT — the post-stamp doorbell arrives with the 'perspective' payload.
    listener.Raise(WorkSignalCategory.Perspective);

    // ASSERT — the wake must produce a follow-up claim well before the 60s poll.
    await coord.NthCallSeen(2).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(2)
      .Because("a 'perspective' doorbell on the legacy listener path must wake the claim loop — "
             + "ClaimWorker is the only claimer of perspective streams, so dropping it quantizes "
             + "perspective visibility to the poll cadence");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private sealed class CountingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly List<TaskCompletionSource> _waiters = [];
    public int CallCount { get; private set; }

    public Task NthCallSeen(int n) {
      lock (_lock) {
        if (CallCount >= n) {
          return Task.CompletedTask;
        }
        while (_waiters.Count < n) {
          _waiters.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        return _waiters[n - 1].Task;
      }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      lock (_lock) {
        CallCount++;
        for (var i = 0; i < CallCount && i < _waiters.Count; i++) {
          _waiters[i].TrySetResult();
        }
      }
      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.FromResult(true);
  }

  private sealed class RaisableListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    public event Action<WorkSignalCategory>? OnSignal {
      add => _onSignal += value;
      remove => _onSignal -= value;
    }
    public event Action<bool>? OnHealthChanged { add { } remove { } }
    public void Raise(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
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
}
