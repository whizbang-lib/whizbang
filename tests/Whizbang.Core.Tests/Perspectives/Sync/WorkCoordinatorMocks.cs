using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Mock implementation of IWorkCoordinator for testing.
/// Provides configurable behavior for sync testing.
/// </summary>
internal sealed class MockWorkCoordinator : IWorkCoordinator {
  private readonly Func<ProcessWorkBatchRequest, CancellationToken, Task<WorkBatch>>? _processHandler;

  public MockWorkCoordinator() { }

  public MockWorkCoordinator(Func<ProcessWorkBatchRequest, CancellationToken, Task<WorkBatch>> processHandler) {
    _processHandler = processHandler;
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

  public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) {
    if (_processHandler != null) {
      return _processHandler(request, ct);
    }
    return Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = null
    });
  }

  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) {
    return Task.CompletedTask;
  }

  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) {
    return Task.CompletedTask;
  }

  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    return Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
