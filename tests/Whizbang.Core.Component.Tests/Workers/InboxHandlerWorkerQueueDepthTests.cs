// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The handler commit queue is the one place dispatched-but-uncommitted work waits in memory. Under a bulk
/// fan-out it held whole composite expansions, so a consumer's memory climbed while its lease count stayed
/// capped and nothing on a dashboard said where the memory was (#740). Composites no longer pass through
/// it; what still does is observable as a gauge, counting both what waits in the channel and what the
/// flusher has taken up but not yet committed.
/// </summary>
/// <docs>operations/observability/metrics#handler-commit-queue</docs>
[Category("Workers")]
public sealed class InboxHandlerWorkerQueueDepthTests {

  /// <summary>Holds the first commit open until released, and says when a flush has taken up a batch.</summary>
  private sealed class HoldsTheFirstCommit : IWorkCoordinator {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default) {
      _entered.TrySetResult();
      await _release.Task.WaitAsync(cancellationToken);
      return [];
    }
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class NoFailures : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private static HandlerCommitRequest _request() => new(
    HandlerId: Guid.CreateVersion7(),
    InstanceId: Guid.CreateVersion7(),
    ServiceName: "test",
    HostName: "test-host",
    ProcessId: 1,
    PartitionCount: 10_000,
    InboxCompletion: new HandlerInboxCompletion(Guid.CreateVersion7(), 2));

  [Test]
  [Timeout(30000)]
  public async Task QueuedHandlerCommits_AreObservableAsAGauge_UntilTheyCommitAsync(CancellationToken cancellationToken) {
    using var factory = new TestMeterFactory();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(factory));
    var coordinator = new HoldsTheFirstCommit();
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator);
    await using var sp = services.BuildServiceProvider();
    // The worker is started, so its flusher runs (#1636: a flusher that never started flushes nothing, and every
    // request would sit in the channel). The coordinator holds the first commit open, so while it is held the
    // requests are either taken up by that flush or still in the channel, and the gauge must count them either way.
    var worker = new InboxHandlerWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      failureChannel: new NoFailures(),
      options: Options.Create(new InboxHandlerWorkerOptions()),
      logger: NullLogger<InboxHandlerWorker>.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      inboxChannelWriter: new InboxChannelWriter(),
      metrics: metrics);

    await worker.StartAsync(cancellationToken);
    await worker.EnqueueAsync(_request(), cancellationToken);
    await worker.EnqueueAsync(_request(), cancellationToken);
    await worker.EnqueueAsync(_request(), cancellationToken);
    // A flush has taken up a batch and is held in the commit: at least one request is now out of the channel.
    await coordinator.Entered.WaitAsync(cancellationToken);

    var meter = factory.CreatedMeters.Single(m => m.Name == WorkCoordinatorMetrics.METER_NAME);
    var queued = ProbeMeterReader.ReadTotal(meter, "whizbang.work_coordinator.handler_commits.queued");
    coordinator.Release();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(queued).IsEqualTo(3)
      .Because("three handler results are dispatched and uncommitted, one batch of them in a commit that has not "
             + "returned; a queue that holds work invisibly is how memory climbs with nothing on a dashboard to say "
             + "where (#740)");
  }
}
