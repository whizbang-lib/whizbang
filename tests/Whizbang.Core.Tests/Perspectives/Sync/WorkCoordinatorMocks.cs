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

  public async Task<IReadOnlyList<SyncInquiryResult>> ResolveSyncInquiriesAsync(
    IReadOnlyList<SyncInquiry> inquiries,
    CancellationToken cancellationToken = default) {
    // Slice 26 split: PerspectiveSyncAwaiter calls this dedicated method instead of
    // ProcessWorkBatchAsync. Tests historically express the desired sync result via
    // a WorkBatch.SyncInquiryResults handler. Route through the handler so existing
    // tests keep working without rewriting their setup.
    if (_processHandler is null) {
      return [];
    }
    var probe = await _processHandler(_emptyRequest(), cancellationToken).ConfigureAwait(false);
    return probe.SyncInquiryResults ?? [];
  }

  private static ProcessWorkBatchRequest _emptyRequest() => new() {
    InstanceId = Guid.NewGuid(),
    ServiceName = "mock",
    HostName = "mock",
    ProcessId = 0,
    NewOutboxMessages = [],
    NewInboxMessages = [],
    OutboxCompletions = [],
    InboxCompletions = [],
    OutboxFailures = [],
    InboxFailures = [],
    ReceptorCompletions = [],
    ReceptorFailures = [],
    PerspectiveCompletions = [],
    PerspectiveEventCompletions = [],
    PerspectiveFailures = [],
    RenewOutboxLeaseIds = [],
    RenewInboxLeaseIds = [],
    Flags = WorkBatchOptions.None
  };

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

  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) {
    return Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
