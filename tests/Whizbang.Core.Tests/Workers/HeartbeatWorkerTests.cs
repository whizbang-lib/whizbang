using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel(Order = 100)]
public class HeartbeatWorkerTests {

  private sealed class StubInstanceProvider(Guid id, string name, string host, int pid) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = id;
    public string ServiceName { get; } = name;
    public string HostName { get; } = host;
    public int ProcessId { get; } = pid;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class StubCoordinator : IWorkCoordinator {
    public TaskCompletionSource<HeartbeatRequest> FirstHeartbeat { get; } = new();
    public int CallCount { get; private set; }

    /// <summary>Whether RecordHeartbeatAsync accepts the heartbeat (true) or reports this instance
    /// as evicted (false) — the tombstone-refusal signal from migration 106.</summary>
    public bool AcceptHeartbeats { get; set; } = true;

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      CallCount++;
      FirstHeartbeat.TrySetResult(request);
      return Task.FromResult(AcceptHeartbeats);
    }

    // Unused — fail loud if any other path is exercised.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("HeartbeatWorker test must not call ClaimWorkAsync");

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default)
      => Task.FromResult(new PartitionRecomputeResult());

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);

    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<PerspectiveCursorInfo>());

    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  [Test]
  public async Task ExecuteAsync_FirstTick_CallsRecordHeartbeatWithProviderIdentityAsync() {
    var instanceId = TrackedGuid.NewMedo();
    var instProvider = new StubInstanceProvider(instanceId, "svc", "host", 42);
    var coord = new StubCoordinator();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instProvider,
      gate,
      Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 1 }),
      NullLogger<HeartbeatWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var first = await coord.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(first.InstanceId).IsEqualTo((Guid)instanceId);
    await Assert.That(first.ServiceName).IsEqualTo("svc");
    await Assert.That(first.HostName).IsEqualTo("host");
    await Assert.That(first.ProcessId).IsEqualTo(42);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // record_heartbeat (migration 106) refuses a reaped instance's heartbeat and reports it through
  // its own return value. Retrying can never succeed — the tombstone does not expire on this
  // instance's clock — so continuing to call would be a heartbeat guaranteed to be rejected
  // forever. The worker must stop rather than loop on a call it cannot win.
  [Test]
  public async Task ExecuteAsync_WhenEvicted_StopsHeartbeatingAsync() {
    var instanceId = TrackedGuid.NewMedo();
    var instProvider = new StubInstanceProvider((Guid)instanceId, "svc", "host", 1);
    var coord = new StubCoordinator { AcceptHeartbeats = false };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instProvider,
      gate,
      Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 1 }),
      NullLogger<HeartbeatWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await coord.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // If the worker kept looping despite the refusal, several more ticks would have landed by now.
    await Task.Delay(TimeSpan.FromSeconds(3));

    await Assert.That(coord.CallCount).IsEqualTo(1)
      .Because("a refused heartbeat means this instance was evicted; it must not keep retrying a "
               + "call that can never succeed");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsAsync() {
    var threw = false;
    try {
      _ = new HeartbeatWorker(
        null!,
        new StubInstanceProvider(Guid.NewGuid(), "s", "h", 1),
        new SchemaReadyGate(),
        Options.Create(new HeartbeatWorkerOptions()),
        NullLogger<HeartbeatWorker>.Instance);
    } catch (ArgumentNullException) {
      threw = true;
    }
    await Assert.That(threw).IsTrue();
  }

  [Test]
  public async Task ExecuteAsync_DisabledOptions_NoHeartbeatFiresAsync() {
    var instanceId = TrackedGuid.NewMedo();
    var instProvider = new StubInstanceProvider((Guid)instanceId, "svc", "host", 1);
    var coord = new StubCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready but Enabled=false should still skip
    var worker = new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instProvider,
      gate,
      Options.Create(new HeartbeatWorkerOptions { Enabled = false, IntervalSeconds = 1 }),
      NullLogger<HeartbeatWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Give the worker a moment — if the killswitch leaks, the first heartbeat will fire fast.
    var raced = await Task.WhenAny(
        coord.FirstHeartbeat.Task,
        Task.Delay(500, CancellationToken.None));
    await Assert.That(coord.FirstHeartbeat.Task.IsCompleted).IsFalse();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var instanceId = TrackedGuid.NewMedo();
    var instProvider = new StubInstanceProvider((Guid)instanceId, "svc", "host", 1);
    var coord = new StubCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();  // NOT marked ready
    var worker = new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instProvider,
      gate,
      Options.Create(new HeartbeatWorkerOptions { IntervalSeconds = 1 }),
      NullLogger<HeartbeatWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Confirm no heartbeat while gate is closed.
    var racedBefore = await Task.WhenAny(
        coord.FirstHeartbeat.Task,
        Task.Delay(300, CancellationToken.None));
    await Assert.That(coord.FirstHeartbeat.Task.IsCompleted).IsFalse();

    // Open gate — heartbeat should arrive promptly.
    gate.MarkReady();
    var first = await coord.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(first.InstanceId).IsEqualTo((Guid)instanceId);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
