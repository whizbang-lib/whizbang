using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Minimal no-op IWorkCoordinator stub for tests that only need IWorkCoordinatorStrategy
/// but where TransportConsumerWorker resolves IWorkCoordinator from DI.
/// </summary>
internal class NoOpWorkCoordinator : IWorkCoordinator {
  /// <summary>Number of inbox messages stored via StoreInboxMessagesAsync (cumulative across calls).</summary>
  public int StoredInboxCount { get; private set; }

  /// <summary>Number of times StoreInboxMessagesAsync was invoked. Distinguishes one-call-with-N-items
  /// (bulk insert) from N-calls-with-one-item (per-message inserts) — load-bearing for slice 6.</summary>
  public int StoreInboxCallCount { get; private set; }

  /// <summary>Per-call batch sizes for StoreInboxMessagesAsync. Tests assert exact shapes
  /// (e.g. one call of size 100, not 10 calls of size 10).</summary>
  public List<int> StoreInboxBatchSizes { get; } = [];

  /// <summary>All inbox messages stored via StoreInboxMessagesAsync, for test inspection.</summary>
  public List<InboxMessage> StoredMessages { get; } = [];

  public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
    Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = null
    });

  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
    StoreInboxCallCount++;
    StoreInboxBatchSizes.Add(messages.Length);
    StoredInboxCount += messages.Length;
    StoredMessages.AddRange(messages);
    return Task.CompletedTask;
  }

  /// <summary>Number of times StoreOutboxMessagesAsync was invoked.</summary>
  public int StoreOutboxCallCount { get; private set; }

  /// <summary>Captured arguments from the last StoreOutboxMessagesAsync call — for end-to-end
  /// wiring tests that verify the closure passes through correctly (partition count, message
  /// array contents).</summary>
  public int LastOutboxPartitionCount { get; private set; }

  /// <summary>All outbox messages ever stored via StoreOutboxMessagesAsync.</summary>
  public List<OutboxMessage> StoredOutboxMessages { get; } = [];

  public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) {
    StoreOutboxCallCount++;
    LastOutboxPartitionCount = partitionCount;
    StoredOutboxMessages.AddRange(messages);
    return Task.CompletedTask;
  }

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
