using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel(Order = 100)]
public class OutboxCompletionFlushWorkerTests {

  private sealed class CapturingCoordinator : IWorkCoordinator {
    public TaskCompletionSource<IReadOnlyList<Guid>> FirstBatch { get; } = new();
    public Task RecordHeartbeatAsync(HeartbeatRequest req, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CompleteOutboxPublishedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) {
      FirstBatch.TrySetResult(ids);
      return Task.FromResult(ids.Count);
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
  public async Task EnqueuedIds_FlushedToCoordinatorAsync() {
    var coord = new CapturingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new OutboxCompletionFlushWorkerOptions {
        Flusher = new BatchFlusherOptions {
          MaxBatchSize = 100,
          CoalesceWindowMs = 25,
          ImmediateFlushThreshold = 5,
          ChannelCapacity = 1_000
        }
      }),
      NullLogger<OutboxCompletionFlushWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var id = TrackedGuid.NewMedo();
    await worker.EnqueueAsync(id);

    // 30s tolerates heavy parallel load on a contended test machine running 12k+ tests
    // across 10 modules — the BatchFlusher's Task.Run startup can be queued for several
    // seconds when the scheduler is saturated.
    var batch = await coord.FirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await Assert.That(batch.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(batch).Contains((Guid)id);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
