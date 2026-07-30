using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Testing.MultiService;

/// <summary>
/// An <see cref="IWorkCoordinator"/> that captures stored inbox messages in memory for assertion,
/// with a completion signal (<see cref="WaitForInboxAsync"/>) instead of polling — the harness's
/// storage seam. Every other coordinator operation is the interface default (no-op), so a harness
/// service exercises the receive path without a database.
/// </summary>
/// <docs>testing/multi-service-harness</docs>
public sealed class CapturingWorkCoordinator : IWorkCoordinator {
  private readonly Lock _lock = new();
  private readonly List<InboxMessage> _stored = [];
  private TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Snapshot of every inbox message stored so far (order preserved).</summary>
  public IReadOnlyList<InboxMessage> StoredInboxMessages {
    get {
      lock (_lock) {
        return [.. _stored];
      }
    }
  }

  /// <inheritdoc />
  public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      SyncInquiryResults = null
    });

  /// <inheritdoc />
  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
    lock (_lock) {
      _stored.AddRange(messages);
      var signal = _signal;
      _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      signal.TrySetResult(true);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkCoordinatorStatistics());

  /// <inheritdoc />
  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
    Task.FromResult<PerspectiveCursorInfo?>(null);

  /// <summary>
  /// Completes when at least <paramref name="count"/> inbox messages have been captured — a
  /// completion signal, not a poll. Throws on <paramref name="timeout"/> so a wiring failure
  /// surfaces as a diagnosable timeout rather than a hang.
  /// </summary>
  public async Task<IReadOnlyList<InboxMessage>> WaitForInboxAsync(int count, TimeSpan timeout) {
    var deadline = Task.Delay(timeout);
    while (true) {
      Task signalTask;
      lock (_lock) {
        if (_stored.Count >= count) {
          return [.. _stored];
        }
        signalTask = _signal.Task;
      }
      var winner = await Task.WhenAny(signalTask, deadline);
      if (winner == deadline) {
        lock (_lock) {
          throw new TimeoutException(
            $"Expected {count} captured inbox message(s) within {timeout.TotalSeconds:0.#}s but saw {_stored.Count}.");
        }
      }
    }
  }
}
