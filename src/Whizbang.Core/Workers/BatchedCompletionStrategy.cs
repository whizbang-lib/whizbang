using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Batched completion strategy that collects completions in memory and reports them on the next poll cycle.
/// This is the default strategy for production environments, minimizing database round-trips by batching.
/// </summary>
/// <remarks>
/// <para>
/// This strategy implements the two-phase completion pattern with acknowledgement tracking:
/// </para>
/// <list type="number">
/// <item><description><strong>Cycle N</strong>: Process perspectives, collect completions/failures in memory via ReportCompletionAsync/ReportFailureAsync</description></item>
/// <item><description><strong>Cycle N+1</strong>: PerspectiveWorker calls GetPendingCompletions/GetPendingFailures, marks as Sent, reports to coordinator via ProcessWorkBatchAsync</description></item>
/// <item><description><strong>Acknowledgement</strong>: Coordinator returns counts, worker marks as Acknowledged, clears only acknowledged items</description></item>
/// <item><description><strong>Retry</strong>: Stale items (sent but not acknowledged) reset to Pending with exponential backoff</description></item>
/// </list>
/// <para>
/// This approach trades immediate consistency for reduced database load. For test environments requiring
/// immediate consistency, use <see cref="InstantCompletionStrategy"/> instead.
/// </para>
/// </remarks>
/// <docs>workers/perspective-worker</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:BatchedStrategy_ReportCompletionAsync_DoesNotCallCoordinatorImmediately_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:BatchedStrategy_GetPendingCompletions_ReturnsCollectedCompletions_Async</tests>
public sealed class BatchedCompletionStrategy : IPerspectiveCompletionStrategy {
  private readonly CompletionTracker<PerspectiveCheckpointCompletion> _completions;
  private readonly CompletionTracker<PerspectiveCheckpointFailure> _failures;

  public BatchedCompletionStrategy(
    TimeSpan? retryTimeout = null,
    double backoffMultiplier = 2.0,
    TimeSpan? maxTimeout = null
  ) {
    _completions = new CompletionTracker<PerspectiveCheckpointCompletion>(
      retryTimeout, backoffMultiplier, maxTimeout
    );
    _failures = new CompletionTracker<PerspectiveCheckpointFailure>(
      retryTimeout, backoffMultiplier, maxTimeout
    );
  }

  /// <inheritdoc />
  /// <remarks>
  /// Stores the completion in memory. Does NOT call the coordinator.
  /// The completion will be reported on the next poll cycle when PerspectiveWorker calls GetPendingCompletions().
  /// </remarks>
  public Task ReportCompletionAsync(
    PerspectiveCheckpointCompletion completion,
    IWorkCoordinator coordinator,
    CancellationToken cancellationToken) {
    _completions.Add(completion);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <remarks>
  /// Stores the failure in memory. Does NOT call the coordinator.
  /// The failure will be reported on the next poll cycle when PerspectiveWorker calls GetPendingFailures().
  /// </remarks>
  public Task ReportFailureAsync(
    PerspectiveCheckpointFailure failure,
    IWorkCoordinator coordinator,
    CancellationToken cancellationToken) {
    _failures.Add(failure);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <remarks>
  /// Returns all completions with status = Pending (not yet sent to coordinator).
  /// PerspectiveWorker will pass these to ProcessWorkBatchAsync on the next poll cycle.
  /// </remarks>
  public TrackedCompletion<PerspectiveCheckpointCompletion>[] GetPendingCompletions() {
    return _completions.GetPending();
  }

  /// <inheritdoc />
  /// <remarks>
  /// Returns all failures with status = Pending (not yet sent to coordinator).
  /// PerspectiveWorker will pass these to ProcessWorkBatchAsync on the next poll cycle.
  /// </remarks>
  public TrackedCompletion<PerspectiveCheckpointFailure>[] GetPendingFailures() {
    return _failures.GetPending();
  }

  /// <inheritdoc />
  public void MarkAsSent(
    TrackedCompletion<PerspectiveCheckpointCompletion>[] completions,
    TrackedCompletion<PerspectiveCheckpointFailure>[] failures,
    DateTimeOffset sentAt) {
    _completions.MarkAsSent(completions, sentAt);
    _failures.MarkAsSent(failures, sentAt);
  }

  /// <inheritdoc />
  public void MarkAsAcknowledged(int completionCount, int failureCount) {
    _completions.MarkAsAcknowledged(completionCount);
    _failures.MarkAsAcknowledged(failureCount);
  }

  /// <inheritdoc />
  public void ClearAcknowledged() {
    _completions.ClearAcknowledged();
    _failures.ClearAcknowledged();
  }

  /// <inheritdoc />
  public void ResetStale(DateTimeOffset now) {
    _completions.ResetStale(now);
    _failures.ResetStale(now);
  }
}
