using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the F1 follow-on: HeartbeatWorker publishes InstanceJoinedSignal exactly once after the
/// first successful heartbeat and InstanceLeavingSignal on graceful stop. Both go to the bus so
/// downstream orphan-takeover / cache-warming / topology-recalc consumers can react without
/// waiting for the periodic instance-lifecycle monitor scan.
/// </summary>
[NotInParallel(Order = 200)]
public class HeartbeatWorkerLifecycleSignalsTests {
  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class HeartbeatCoordinator : IWorkCoordinator {
    public TaskCompletionSource<HeartbeatRequest> FirstHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int HeartbeatCount { get; private set; }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) {
      var c = Interlocked.Increment(ref _count);
      HeartbeatCount = c;
      FirstHeartbeat.TrySetResult(request);
      return Task.FromResult(true);
    }
    private int _count;

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
  }

  private sealed class CapturingBus : ISignalBus {
    public List<Type> Published { get; } = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      lock (Published) { Published.Add(typeof(TSignal)); }
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { } }
  }

  private static (HeartbeatWorker Worker, HeartbeatCoordinator Coord, CapturingBus Bus) _create() {
    var coord = new HeartbeatCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    var bus = new CapturingBus();
    var worker = new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      schemaGate,
      Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 300 }),   // long interval — first heartbeat only
      NullLogger<HeartbeatWorker>.Instance,
      signalBus: bus);
    return (worker, coord, bus);
  }

  [Test]
  public async Task FirstHeartbeat_PublishesInstanceJoinedSignalAsync() {
    var (worker, coord, bus) = _create();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try {
      await worker.StartAsync(cts.Token);
      await coord.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
      // The publish happens synchronously after RecordHeartbeat completes on the same tick.
      var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
      while (DateTimeOffset.UtcNow < deadline) {
        lock (bus.Published) {
          if (bus.Published.Contains(typeof(InstanceJoinedSignal))) { break; }
        }
        await Task.Yield();
      }
      List<Type> snapshot;
      lock (bus.Published) { snapshot = [.. bus.Published]; }
      await Assert.That(snapshot).Contains(typeof(InstanceJoinedSignal))
        .Because("the first successful heartbeat is when wh_service_instances gets this pod's row — announce the join");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task InstanceJoined_PublishesOnlyOnceAsync() {
    var (worker, coord, bus) = _create();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try {
      await worker.StartAsync(cts.Token);
      // Wait for two heartbeats — the InstanceJoined publish must only happen once.
      await coord.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
      // Give a moment for a second heartbeat tick (interval is 300s, so this won't happen —
      // but the publish gate is on the first tick regardless). We assert only ONE join in the list.
      var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
      while (DateTimeOffset.UtcNow < deadline) {
        await Task.Yield();
      }
      int joinCount;
      lock (bus.Published) { joinCount = bus.Published.Count(t => t == typeof(InstanceJoinedSignal)); }
      await Assert.That(joinCount).IsEqualTo(1);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task GracefulStop_PublishesInstanceLeavingSignalAsync() {
    var (worker, coord, bus) = _create();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);
    await coord.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

    await worker.StopAsync(CancellationToken.None);

    List<Type> snapshot;
    lock (bus.Published) { snapshot = [.. bus.Published]; }
    await Assert.That(snapshot).Contains(typeof(InstanceLeavingSignal))
      .Because("graceful shutdown must announce InstanceLeaving so peers can rebalance without waiting for the stale-heartbeat threshold");
  }
}
