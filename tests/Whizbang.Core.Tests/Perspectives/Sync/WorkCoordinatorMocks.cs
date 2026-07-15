using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Mock implementation of IWorkCoordinator for testing.
/// Provides configurable behavior for sync testing.
/// </summary>
internal sealed class MockWorkCoordinator : IWorkCoordinator {
  private readonly Func<IReadOnlyList<SyncInquiry>, CancellationToken, Task<WorkBatch>>? _processHandler;

  public MockWorkCoordinator() { }

  public MockWorkCoordinator(Func<IReadOnlyList<SyncInquiry>, CancellationToken, Task<WorkBatch>> processHandler) {
    _processHandler = processHandler;
  }

  public async Task<IReadOnlyList<SyncInquiryResult>> ResolveSyncInquiriesAsync(
    IReadOnlyList<SyncInquiry> inquiries,
    CancellationToken cancellationToken = default) {
    // PerspectiveSyncAwaiter's only coordinator call for sync. Tests express the desired
    // sync result via a WorkBatch.SyncInquiryResults handler; route the live inquiries
    // through it so a handler can echo back inquiry ids when it needs to.
    if (_processHandler is null) {
      return [];
    }
    var probe = await _processHandler(inquiries, cancellationToken).ConfigureAwait(false);
    return probe.SyncInquiryResults ?? [];
  }

  /// <summary>
  /// Creates a mock that returns sync results with specified pending count.
  /// </summary>
  public static MockWorkCoordinator WithSyncResults(int pendingCount) {
    return new MockWorkCoordinator((_, _) => Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = [
        new SyncInquiryResult {
          InquiryId = Guid.NewGuid(),
          PendingCount = pendingCount
        }
      ]
    }));
  }

  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) {
    return Task.CompletedTask;
  }

  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) {
    return Task.CompletedTask;
  }

  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    return Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
