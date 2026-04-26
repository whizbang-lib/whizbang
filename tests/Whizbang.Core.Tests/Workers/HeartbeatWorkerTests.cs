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

    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      CallCount++;
      FirstHeartbeat.TrySetResult(request);
      return Task.CompletedTask;
    }

    // Unused — fail loud if any other path is exercised.
    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("HeartbeatWorker test must not call ProcessWorkBatchAsync");

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

    var worker = new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instProvider,
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

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsAsync() {
    var threw = false;
    try {
      _ = new HeartbeatWorker(
        null!,
        new StubInstanceProvider(Guid.NewGuid(), "s", "h", 1),
        Options.Create(new HeartbeatWorkerOptions()),
        NullLogger<HeartbeatWorker>.Instance);
    } catch (ArgumentNullException) {
      threw = true;
    }
    await Assert.That(threw).IsTrue();
  }
}
