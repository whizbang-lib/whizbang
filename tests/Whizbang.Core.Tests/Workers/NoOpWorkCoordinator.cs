using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Minimal no-op IWorkCoordinator stub for tests that only need IWorkCoordinatorStrategy
/// but where TransportConsumerWorker resolves IWorkCoordinator from DI.
/// </summary>
internal sealed class NoOpWorkCoordinator : IWorkCoordinator {
  public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
    Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = null
    });

  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) =>
    Task.CompletedTask;

  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) =>
    Task.CompletedTask;

  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkCoordinatorStatistics());

  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
    Task.FromResult<PerspectiveCursorInfo?>(null);
}
