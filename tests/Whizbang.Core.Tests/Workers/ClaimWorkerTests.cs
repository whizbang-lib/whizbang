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

[NotInParallel(Order = 100)]
public class ClaimWorkerTests {

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

  private sealed class FakeCoordinator : IWorkCoordinator {
    public TaskCompletionSource FirstCallSignal { get; } = new();
    public int CallCount { get; private set; }
    public WorkBatch BatchToReturn { get; set; } = new() {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = []
    };

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      CallCount++;
      FirstCallSignal.TrySetResult();
      return Task.FromResult(BatchToReturn);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("not used");
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

  [Test]
  public async Task ExecuteAsync_PollsAtLeastOnceAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task RequestImmediatePoll_BypassesWaitAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 10_000,            // very long
        PollingMaxIntervalMilliseconds = 60_000           // very long
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var startCount = coord.CallCount;

    // Immediate poll should fire another tick within hundreds of ms despite the 10s base.
    var beforeWake = coord.CallCount;
    worker.RequestImmediatePoll();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (coord.CallCount == beforeWake && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Delay(20);
    }

    await Assert.That(coord.CallCount).IsGreaterThan(startCount);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
