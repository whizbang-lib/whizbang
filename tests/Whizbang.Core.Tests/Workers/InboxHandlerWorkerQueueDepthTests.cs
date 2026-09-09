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

  private sealed class _neverCommits : IWorkCoordinator {
    public Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("the schema gate is never opened in this test, so no flush reaches the coordinator");
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class _noFailures : IFailureChannel {
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
  public async Task QueuedHandlerCommits_AreObservableAsAGauge_UntilTheyCommitAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(factory));
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(new _neverCommits());
    using var sp = services.BuildServiceProvider();
    // The gate is never marked ready, so a flush blocks holding its batch: the requests are either still
    // in the channel or taken up by the flusher, and the gauge must count them either way.
    var worker = new InboxHandlerWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), new _noFailures(), new SchemaReadyGate(),
      Options.Create(new InboxHandlerWorkerOptions()), NullLogger<InboxHandlerWorker>.Instance,
      metrics: metrics);

    await worker.EnqueueAsync(_request());
    await worker.EnqueueAsync(_request());
    await worker.EnqueueAsync(_request());

    var meter = factory.CreatedMeters.Single(m => m.Name == WorkCoordinatorMetrics.METER_NAME);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.work_coordinator.handler_commits.queued")).IsEqualTo(3)
      .Because("three handler results are dispatched and uncommitted; a queue that holds work invisibly is how memory climbs with nothing on a dashboard to say where (#740)");
  }
}
