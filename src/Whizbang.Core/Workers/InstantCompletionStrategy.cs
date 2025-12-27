using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Instant completion strategy that reports completions immediately to the coordinator.
/// This strategy calls ProcessWorkBatchAsync immediately when completions/failures occur,
/// trading reduced database batching for immediate consistency.
/// </summary>
/// <remarks>
/// <para>
/// This strategy is recommended for test environments where immediate consistency is required.
/// Each completion/failure triggers a separate ProcessWorkBatchAsync call, which may increase
/// database load in high-throughput scenarios.
/// </para>
/// <para>
/// For production environments with high perspective throughput, prefer <see cref="BatchedCompletionStrategy"/>
/// which batches multiple completions into a single database round-trip.
/// </para>
/// </remarks>
/// <docs>workers/perspective-worker</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:InstantStrategy_ReportCompletionAsync_CallsCoordinatorImmediately_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveCompletionStrategyTests.cs:InstantStrategy_GetPendingCompletions_AlwaysReturnsEmpty_Async</tests>
public sealed class InstantCompletionStrategy : IPerspectiveCompletionStrategy {
  /// <summary>
  /// Creates a new instant completion strategy.
  /// </summary>
  /// <remarks>
  /// No configuration needed - uses lightweight out-of-band coordinator methods.
  /// </remarks>
  public InstantCompletionStrategy() {
    // No-op constructor - no state needed for instant reporting
  }

  /// <inheritdoc />
  /// <remarks>
  /// Immediately calls ReportPerspectiveCompletionAsync on the coordinator.
  /// This lightweight out-of-band method updates the perspective checkpoint
  /// and releases the lease, allowing immediate re-claiming on next poll.
  /// </remarks>
  public async Task ReportCompletionAsync(
    PerspectiveCheckpointCompletion completion,
    IWorkCoordinator coordinator,
    CancellationToken cancellationToken) {
    // Report immediately via lightweight out-of-band method
    await coordinator.ReportPerspectiveCompletionAsync(completion, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Immediately calls ReportPerspectiveFailureAsync on the coordinator.
  /// This lightweight out-of-band method updates the perspective checkpoint
  /// and releases the lease, allowing immediate re-claiming on next poll.
  /// </remarks>
  public async Task ReportFailureAsync(
    PerspectiveCheckpointFailure failure,
    IWorkCoordinator coordinator,
    CancellationToken cancellationToken) {
    // Report immediately via lightweight out-of-band method
    await coordinator.ReportPerspectiveFailureAsync(failure, cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Always returns an empty array because completions are reported immediately.
  /// Nothing is ever pending with the instant strategy.
  /// </remarks>
  public PerspectiveCheckpointCompletion[] GetPendingCompletions() {
    return [];
  }

  /// <inheritdoc />
  /// <remarks>
  /// Always returns an empty array because failures are reported immediately.
  /// Nothing is ever pending with the instant strategy.
  /// </remarks>
  public PerspectiveCheckpointFailure[] GetPendingFailures() {
    return [];
  }

  /// <inheritdoc />
  /// <remarks>
  /// No-op for instant strategy since nothing is ever stored.
  /// </remarks>
  public void ClearPending() {
    // No-op - nothing to clear since we report immediately
  }
}
