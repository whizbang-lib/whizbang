using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Exercises every default interface member on <see cref="IWorkCoordinator"/> through a
/// minimal fake that implements ONLY the abstract members. The default bodies split into
/// two families with distinct contracts:
///   - Postgres-only surfaces (RecordHeartbeatAsync, ClaimWorkAsync, the flushers, ...) throw
///     NotImplementedException naming the missing member so a mis-wired backend fails loudly.
///   - Optional surfaces (maintenance, drain fetches, lifecycle reconciliation, ...) no-op
///     with empty/zero results so in-memory coordinators and test fakes need no overrides.
/// A fake that overrode any of these (like NoOpWorkCoordinator does for ProcessWorkBatchAsync)
/// would bypass the default body, so this file uses its own non-overriding stub.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
public class WorkCoordinatorDefaultInterfaceTests {
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Default interface members are only dispatchable through the interface type — that dispatch IS the behavior under test")]
  private readonly IWorkCoordinator _coordinator = new MinimalWorkCoordinator();

  // ========================================
  // Compatibility default: ProcessWorkBatchAsync
  // ========================================

  [Test]
  public async Task ProcessWorkBatchAsync_DefaultImplementation_ReturnsEmptyWorkBatchAsync() {
    var request = _createRequest();

    var batch = await _coordinator.ProcessWorkBatchAsync(request);

    await Assert.That(batch.OutboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.InboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveWork.Count).IsEqualTo(0);
    await Assert.That(batch.PerspectiveStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.OutboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.InboxStreamIds.Count).IsEqualTo(0);
    await Assert.That(batch.SyncInquiryResults).IsNull();
  }

  // ========================================
  // Throwing defaults (Postgres-only surfaces)
  // ========================================

  [Test]
  public async Task RecordHeartbeatAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    var request = new HeartbeatRequest(
      (Guid)TrackedGuid.NewMedo(), "TestService", "test-host", 123);

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.RecordHeartbeatAsync(request));

    await Assert.That(message).Contains("RecordHeartbeatAsync");
    await Assert.That(message).Contains(nameof(MinimalWorkCoordinator));
  }

  [Test]
  public async Task NotifyScheduledRetryDueAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.NotifyScheduledRetryDueAsync());

    await Assert.That(message).Contains("NotifyScheduledRetryDueAsync");
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_SimpleOverload_DelegatesToThrowingDebugOverloadAsync() {
    IReadOnlyList<Guid> ids = [(Guid)TrackedGuid.NewMedo()];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.CompleteOutboxPublishedAsync(ids));

    await Assert.That(message).Contains("CompleteOutboxPublishedAsync");
  }

  [Test]
  public async Task CompleteOutboxPublishedAsync_DebugOverload_ThrowsNamingTheMemberAsync() {
    IReadOnlyList<Guid> ids = [(Guid)TrackedGuid.NewMedo()];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.CompleteOutboxPublishedAsync(ids, debugMode: true));

    await Assert.That(message).Contains("CompleteOutboxPublishedAsync");
  }

  [Test]
  public async Task CompletePerspectiveAsync_SimpleOverload_DelegatesToThrowingDebugOverloadAsync() {
    IReadOnlyList<PerspectiveCursorCompletion> cursors = [];
    IReadOnlyList<Guid> eventWorkIds = [];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.CompletePerspectiveAsync(cursors, eventWorkIds));

    await Assert.That(message).Contains("CompletePerspectiveAsync");
  }

  [Test]
  public async Task CompletePerspectiveAsync_DebugOverload_ThrowsNamingTheMemberAsync() {
    IReadOnlyList<PerspectiveCursorCompletion> cursors = [];
    IReadOnlyList<Guid> eventWorkIds = [];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.CompletePerspectiveAsync(cursors, eventWorkIds, debugMode: true));

    await Assert.That(message).Contains("CompletePerspectiveAsync");
  }

  [Test]
  public async Task RenewLeasesAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    IReadOnlyList<Guid> ids = [(Guid)TrackedGuid.NewMedo()];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.RenewLeasesAsync(WorkCategory.Outbox, ids));

    await Assert.That(message).Contains("RenewLeasesAsync");
  }

  [Test]
  public async Task ReportFailuresAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    IReadOnlyList<MessageFailure> failures = [];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.ReportFailuresAsync(WorkCategory.Inbox, failures));

    await Assert.That(message).Contains("ReportFailuresAsync");
  }

  [Test]
  public async Task CommitHandlerResultAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    var request = _createHandlerCommitRequest();

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.CommitHandlerResultAsync(request));

    await Assert.That(message).Contains("CommitHandlerResultAsync");
  }

  [Test]
  public async Task CommitHandlerBatchAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    IReadOnlyList<HandlerCommitRequest> requests = [_createHandlerCommitRequest()];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.CommitHandlerBatchAsync(requests));

    await Assert.That(message).Contains("CommitHandlerBatchAsync");
  }

  [Test]
  public async Task ClaimWorkAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    var request = new ClaimWorkRequest(
      (Guid)TrackedGuid.NewMedo(), "TestService", "test-host", 123);

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.ClaimWorkAsync(request));

    await Assert.That(message).Contains("ClaimWorkAsync");
  }

  [Test]
  public async Task FlushCompletionsAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    var request = new FlushCompletionsRequest();

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.FlushCompletionsAsync(request));

    await Assert.That(message).Contains("FlushCompletionsAsync");
  }

  [Test]
  public async Task ResolveSyncInquiriesAsync_DefaultImplementation_ThrowsNamingTheMemberAsync() {
    IReadOnlyList<SyncInquiry> inquiries = [];

    var message = await _captureNotImplementedMessageAsync(
      () => _coordinator.ResolveSyncInquiriesAsync(inquiries));

    await Assert.That(message).Contains("ResolveSyncInquiriesAsync");
  }

  // ========================================
  // No-op defaults (optional surfaces)
  // ========================================

  [Test]
  public async Task StoreOutboxMessagesAsync_DefaultImplementation_CompletesWithoutErrorAsync() {
    var threw = false;
    try {
      await _coordinator.StoreOutboxMessagesAsync([], 10);
      await _coordinator.RecordLifecycleCompletionAsync((Guid)TrackedGuid.NewMedo());
    } catch {
      threw = true;
    }

    await Assert.That(threw).IsFalse();
  }

  [Test]
  public async Task CleanupCompletedStreamsAsync_DefaultImplementation_EvictsZeroStreamsAsync() {
    IReadOnlyList<Guid> streamIds = [(Guid)TrackedGuid.NewMedo()];

    var evicted = await _coordinator.CleanupCompletedStreamsAsync(streamIds);

    await Assert.That(evicted).IsEqualTo(0);
  }

  [Test]
  public async Task RecomputePartitionNumbersAsync_DefaultImplementation_ReportsNothingRecomputedAsync() {
    var result = await _coordinator.RecomputePartitionNumbersAsync(10_000);

    await Assert.That(result.InboxRowsRecomputed).IsEqualTo(0);
    await Assert.That(result.OutboxRowsRecomputed).IsEqualTo(0);
    await Assert.That(result.ActiveStreamsRowsRecomputed).IsEqualTo(0);
    await Assert.That(result.AnyRecomputed).IsFalse();
  }

  [Test]
  public async Task GetPerspectiveCursorsBatchAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    Guid[] streamIds = [(Guid)TrackedGuid.NewMedo()];

    var cursors = await _coordinator.GetPerspectiveCursorsBatchAsync(streamIds);

    await Assert.That(cursors.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetOrphanedLifecycleEventsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var registry = new Dictionary<string, IReadOnlyList<string>>();

    var orphans = await _coordinator.GetOrphanedLifecycleEventsAsync(registry, TimeSpan.FromMinutes(5));

    await Assert.That(orphans.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CleanupLifecycleCompletionsAsync_DefaultImplementation_DeletesZeroAsync() {
    var deleted = await _coordinator.CleanupLifecycleCompletionsAsync(TimeSpan.FromHours(1));

    await Assert.That(deleted).IsEqualTo(0);
  }

  [Test]
  public async Task GetCursorsRequiringRewindAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var cursors = await _coordinator.GetCursorsRequiringRewindAsync();

    await Assert.That(cursors.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CompletePerspectiveEventsAsync_SimpleOverload_DelegatesToZeroResultDebugOverloadAsync() {
    Guid[] workItemIds = [(Guid)TrackedGuid.NewMedo()];

    var affected = await _coordinator.CompletePerspectiveEventsAsync(workItemIds);

    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task CompletePerspectiveEventsAsync_DebugOverload_ReturnsZeroAsync() {
    Guid[] workItemIds = [(Guid)TrackedGuid.NewMedo()];

    var affected = await _coordinator.CompletePerspectiveEventsAsync(workItemIds, debugMode: true);

    await Assert.That(affected).IsEqualTo(0);
  }

  [Test]
  public async Task GetStreamEventsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    Guid[] streamIds = [(Guid)TrackedGuid.NewMedo()];

    var events = await _coordinator.GetStreamEventsAsync((Guid)TrackedGuid.NewMedo(), streamIds);

    await Assert.That(events.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetLocalServiceIdAsync_DefaultImplementation_ReturnsEmptyGuidAsync() {
    var serviceId = await _coordinator.GetLocalServiceIdAsync();

    await Assert.That(serviceId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task FetchOutboxBatchAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    IReadOnlyList<Guid> streamIds = [(Guid)TrackedGuid.NewMedo()];

    var rows = await _coordinator.FetchOutboxBatchAsync(streamIds, (Guid)TrackedGuid.NewMedo());

    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchInboxBatchAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    IReadOnlyList<Guid> streamIds = [(Guid)TrackedGuid.NewMedo()];

    var rows = await _coordinator.FetchInboxBatchAsync(streamIds, (Guid)TrackedGuid.NewMedo());

    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchPendingPerspectiveEventsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var pending = await _coordinator.FetchPendingPerspectiveEventsAsync(
      (Guid)TrackedGuid.NewMedo(), "TestPerspective", (Guid)TrackedGuid.NewMedo());

    await Assert.That(pending.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ClaimAndFetchPendingPerspectiveEventsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var pending = await _coordinator.ClaimAndFetchPendingPerspectiveEventsAsync(
      (Guid)TrackedGuid.NewMedo(), "TestPerspective", (Guid)TrackedGuid.NewMedo(), TimeSpan.FromMinutes(5));

    await Assert.That(pending.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FetchEventsByIdsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    IReadOnlyList<Guid> eventIds = [(Guid)TrackedGuid.NewMedo()];

    var events = await _coordinator.FetchEventsByIdsAsync(eventIds);

    await Assert.That(events.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PerformMaintenanceAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var results = await _coordinator.PerformMaintenanceAsync();

    await Assert.That(results.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PurgeOrphanInboxAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    IReadOnlyList<string> handledTypes = ["MyApp.Events.OrderCreated, MyApp"];

    var purged = await _coordinator.PurgeOrphanInboxAsync(handledTypes);

    await Assert.That(purged.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FindStuckOutboxRowsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var rows = await _coordinator.FindStuckOutboxRowsAsync(5, 100);

    await Assert.That(rows.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FindStuckInboxRowsAsync_DefaultImplementation_ReturnsEmptyListAsync() {
    var rows = await _coordinator.FindStuckInboxRowsAsync(5, 100);

    await Assert.That(rows.Count).IsEqualTo(0);
  }

  // ========================================
  // Helpers
  // ========================================

  private static async Task<string> _captureNotImplementedMessageAsync(Func<Task> action) {
    try {
      await action();
    } catch (NotImplementedException ex) {
      return ex.Message;
    }
    return string.Empty;
  }

  private static ProcessWorkBatchRequest _createRequest() {
    return new ProcessWorkBatchRequest {
      InstanceId = (Guid)TrackedGuid.NewMedo(),
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = 123,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = []
    };
  }

  private static HandlerCommitRequest _createHandlerCommitRequest() {
    return new HandlerCommitRequest(
      (Guid)TrackedGuid.NewMedo(),
      (Guid)TrackedGuid.NewMedo(),
      "TestService",
      "test-host",
      123,
      10_000,
      new HandlerInboxCompletion((Guid)TrackedGuid.NewMedo(), (int)MessageProcessingStatus.Stored));
  }

  /// <summary>
  /// Implements ONLY the abstract members of <see cref="IWorkCoordinator"/> so every
  /// default interface member above executes its actual default body. Do NOT add
  /// overrides for defaulted members here — that would silently un-test them.
  /// </summary>
  private sealed class MinimalWorkCoordinator : IWorkCoordinator {
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
