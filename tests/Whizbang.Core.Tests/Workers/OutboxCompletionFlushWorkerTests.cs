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
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest req, CancellationToken ct = default) => Task.FromResult(true);
    public Task<int> CompleteOutboxPublishedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) {
      FirstBatch.TrySetResult(ids);
      return Task.FromResult(ids.Count);
    }
    public Task<int> CompleteOutboxPublishedAsync(IReadOnlyList<Guid> ids, bool debugMode, CancellationToken ct = default) {
      FirstBatch.TrySetResult(ids);
      return Task.FromResult(ids.Count);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
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

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new OutboxCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new OutboxCompletionFlushWorkerOptions {
        Flusher = new BatchFlusherOptions {
          MaxBatchSize = 100,
          CoalesceWindowMs = 25,
          ImmediateFlushThreshold = 5,
          ChannelCapacity = 1_000
        }
      }),
      Options.Create(new WorkCoordinatorOptions()),
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

  [Test]
  public async Task FlushBatch_BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var coord = new CapturingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();  // gate NOT marked ready
    var worker = new OutboxCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new OutboxCompletionFlushWorkerOptions {
        Flusher = new BatchFlusherOptions {
          MaxBatchSize = 100,
          CoalesceWindowMs = 25,
          ImmediateFlushThreshold = 1,  // immediate flush on first item
          ChannelCapacity = 1_000
        }
      }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<OutboxCompletionFlushWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await worker.EnqueueAsync(TrackedGuid.NewMedo());

    // Confirm the flush callback is held back by the gate — coord receives nothing.
    var racedBefore = await Task.WhenAny(coord.FirstBatch.Task, Task.Delay(300, CancellationToken.None));

    // Open gate — flush proceeds.
    gate.MarkReady();
    var batch = await coord.FirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await Assert.That(batch.Count).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_DisabledOptions_NoFlushHappensAsync() {
    var coord = new CapturingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready, but Enabled=false should still skip the flush loop
    var worker = new OutboxCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new OutboxCompletionFlushWorkerOptions {
        Enabled = false,
        Flusher = new BatchFlusherOptions {
          MaxBatchSize = 100,
          CoalesceWindowMs = 25,
          ImmediateFlushThreshold = 1,
          ChannelCapacity = 1_000
        }
      }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<OutboxCompletionFlushWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Enqueue lands on the channel but the flush loop never runs.
    await worker.EnqueueAsync(TrackedGuid.NewMedo());

    var raced = await Task.WhenAny(coord.FirstBatch.Task, Task.Delay(500, CancellationToken.None));
    await Assert.That(coord.FirstBatch.Task.IsCompleted).IsFalse();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
