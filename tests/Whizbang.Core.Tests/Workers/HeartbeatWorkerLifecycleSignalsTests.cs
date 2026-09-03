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

  /// <summary>A bus whose publishes fail, to exercise the non-fatal announce paths.</summary>
  private sealed class FailingBus(Exception failure) : ISignalBus {
    public int Attempts;
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Interlocked.Increment(ref Attempts);
      return ValueTask.FromException(failure);
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { } }
  }

  private static HeartbeatWorker _createWith(ISignalBus bus, ISchemaReadyGate? gate = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new HeartbeatCoordinator());
    var sp = services.BuildServiceProvider();
    var schemaGate = gate ?? new SchemaReadyGate();
    if (gate is null) { ((SchemaReadyGate)schemaGate).MarkReady(); }
    return new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      schemaGate,
      Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 300 }),
      NullLogger<HeartbeatWorker>.Instance,
      signalBus: bus);
  }

  [Test]
  public async Task AFailedJoinAnnounce_IsSurvivedSoTheInstanceStillHeartbeatsAsync() {
    // Announcing is an optimization: reconciling consumers pick a new instance up from the
    // heartbeat scan anyway. Letting a failed announce kill the worker would trade a slower
    // rebalance for an instance that never heartbeats at all and gets reaped as stale.
    var bus = new FailingBus(new InvalidOperationException("signal transport down"));
    var worker = _createWith(bus);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Wait on the attempt itself rather than on a duration — a fixed delay either flakes under
    // load or slows every run to cover the worst case.
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (Volatile.Read(ref bus.Attempts) == 0 && DateTime.UtcNow < deadline) {
      await Task.Yield();
    }
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(bus.Attempts).IsGreaterThan(0)
      .Because("the announce must actually have been attempted for its failure path to mean anything");
  }

  [Test]
  public async Task AFailedLeavingAnnounce_DoesNotBlockShutdownAsync() {
    // The InstanceDied monitor still detects departure through lease and heartbeat expiry, so a
    // failed goodbye costs a slower rebalance — never a shutdown that hangs or throws.
    var bus = new FailingBus(new InvalidOperationException("signal transport down"));
    var worker = _createWith(bus);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ShutdownBeforeTheSchemaIsReady_ExitsQuietlyAsync() {
    // The worker parks on the schema gate at startup. A pod stopped while still waiting has
    // nothing to report and no schema to write to, so the exit must be silent rather than an error
    // on every fast restart.
    var gate = new SchemaReadyGate();   // never marked ready
    var worker = _createWith(new CapturingBus(), gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.StopAsync(CancellationToken.None);
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
