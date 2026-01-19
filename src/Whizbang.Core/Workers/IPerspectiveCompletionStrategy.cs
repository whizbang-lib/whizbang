using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Strategy interface for reporting perspective checkpoint completions and failures.
/// Allows configurable behavior for when/how PerspectiveWorker reports results to the coordinator.
/// </summary>
/// <remarks>
/// <para>
/// Implementations control the timing and batching of completion/failure reporting:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>BatchedCompletionStrategy</strong>: Collects completions in memory, reports them on the next poll cycle.
/// This is the default behavior and minimizes database round-trips by batching multiple completions.
/// </description></item>
/// <item><description>
/// <strong>InstantCompletionStrategy</strong>: Reports completions immediately via ProcessWorkBatchAsync.
/// Useful for test environments where immediate consistency is required.
/// </description></item>
/// <item><description>
/// <strong>Custom Strategies</strong>: Developers can implement custom strategies for unit-of-work patterns,
/// time-based batching, or other domain-specific requirements.
/// </description></item>
/// </list>
/// </remarks>
/// <docs>workers/perspective-worker</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs</tests>
public interface IPerspectiveCompletionStrategy {
  /// <summary>
  /// Reports a perspective checkpoint completion to the work coordinator.
  /// </summary>
  /// <param name="completion">The completion to report</param>
  /// <param name="coordinator">The work coordinator to report to</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task representing the async operation</returns>
  /// <remarks>
  /// Batched strategies may store the completion for later reporting.
  /// Instant strategies will call ProcessWorkBatchAsync immediately.
  /// </remarks>
  Task ReportCompletionAsync(
    PerspectiveCheckpointCompletion completion,
    IWorkCoordinator coordinator,
    CancellationToken cancellationToken);

  /// <summary>
  /// Reports a perspective checkpoint failure to the work coordinator.
  /// </summary>
  /// <param name="failure">The failure to report</param>
  /// <param name="coordinator">The work coordinator to report to</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task representing the async operation</returns>
  /// <remarks>
  /// Batched strategies may store the failure for later reporting.
  /// Instant strategies will call ProcessWorkBatchAsync immediately.
  /// </remarks>
  Task ReportFailureAsync(
    PerspectiveCheckpointFailure failure,
    IWorkCoordinator coordinator,
    CancellationToken cancellationToken);

  /// <summary>
  /// Gets all pending completions that have been collected but not yet reported.
  /// </summary>
  /// <returns>Array of tracked completions with status information</returns>
  /// <remarks>
  /// Batched strategies return collected completions awaiting the next poll cycle.
  /// Instant strategies always return an empty array (nothing is pending).
  /// </remarks>
  TrackedCompletion<PerspectiveCheckpointCompletion>[] GetPendingCompletions();

  /// <summary>
  /// Gets all pending failures that have been collected but not yet reported.
  /// </summary>
  /// <returns>Array of tracked failures with status information</returns>
  /// <remarks>
  /// Batched strategies return collected failures awaiting the next poll cycle.
  /// Instant strategies always return an empty array (nothing is pending).
  /// </remarks>
  TrackedCompletion<PerspectiveCheckpointFailure>[] GetPendingFailures();

  /// <summary>
  /// Mark items as sent to ProcessWorkBatchAsync.
  /// </summary>
  void MarkAsSent(
    TrackedCompletion<PerspectiveCheckpointCompletion>[] completions,
    TrackedCompletion<PerspectiveCheckpointFailure>[] failures,
    DateTimeOffset sentAt);

  /// <summary>
  /// Mark oldest N items as acknowledged based on counts from ProcessWorkBatchAsync.
  /// </summary>
  void MarkAsAcknowledged(int completionCount, int failureCount);

  /// <summary>
  /// Clear all acknowledged items.
  /// </summary>
  void ClearAcknowledged();

  /// <summary>
  /// Reset stale items back to pending with exponential backoff.
  /// </summary>
  void ResetStale(DateTimeOffset now);
}
