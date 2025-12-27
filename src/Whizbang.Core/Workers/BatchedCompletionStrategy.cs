using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Batched completion strategy that collects completions in memory and reports them on the next poll cycle.
/// This is the default strategy for production environments, minimizing database round-trips by batching.
/// </summary>
/// <remarks>
/// <para>
/// This strategy implements the two-phase completion pattern:
/// </para>
/// <list type="number">
/// <item><description><strong>Cycle N</strong>: Process perspectives, collect completions/failures in memory via ReportCompletionAsync/ReportFailureAsync</description></item>
/// <item><description><strong>Cycle N+1</strong>: PerspectiveWorker calls GetPendingCompletions/GetPendingFailures, reports to coordinator via ProcessWorkBatchAsync</description></item>
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
  private readonly ConcurrentBag<PerspectiveCheckpointCompletion> _completions = [];
  private readonly ConcurrentBag<PerspectiveCheckpointFailure> _failures = [];

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
  /// Returns all completions collected since the last ClearPending() call.
  /// PerspectiveWorker will pass these to ProcessWorkBatchAsync on the next poll cycle.
  /// </remarks>
  public PerspectiveCheckpointCompletion[] GetPendingCompletions() {
    return _completions.ToArray();
  }

  /// <inheritdoc />
  /// <remarks>
  /// Returns all failures collected since the last ClearPending() call.
  /// PerspectiveWorker will pass these to ProcessWorkBatchAsync on the next poll cycle.
  /// </remarks>
  public PerspectiveCheckpointFailure[] GetPendingFailures() {
    return _failures.ToArray();
  }

  /// <inheritdoc />
  /// <remarks>
  /// Clears both completion and failure collections.
  /// Called by PerspectiveWorker after successfully reporting to the coordinator.
  /// </remarks>
  public void ClearPending() {
    _completions.Clear();
    _failures.Clear();
  }
}
