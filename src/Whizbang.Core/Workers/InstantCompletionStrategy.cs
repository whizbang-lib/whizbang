using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed partial class InstantCompletionStrategy : IPerspectiveCompletionStrategy {
  private readonly ILogger<InstantCompletionStrategy> _logger;

  /// <summary>
  /// Creates a new instant completion strategy.
  /// </summary>
  /// <param name="logger">Optional logger for diagnostic output.</param>
  /// <remarks>
  /// No configuration needed - uses lightweight out-of-band coordinator methods.
  /// </remarks>
  public InstantCompletionStrategy(ILogger<InstantCompletionStrategy>? logger = null) {
    _logger = logger ?? NullLogger<InstantCompletionStrategy>.Instance;
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
    LogReportingCompletion(_logger, completion.PerspectiveName, completion.StreamId, completion.LastEventId);

    // Report immediately via lightweight out-of-band method
    await coordinator.ReportPerspectiveCompletionAsync(completion, cancellationToken);

    LogCompletionReported(_logger);
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
  public TrackedCompletion<PerspectiveCheckpointCompletion>[] GetPendingCompletions() {
    return [];
  }

  /// <inheritdoc />
  /// <remarks>
  /// Always returns an empty array because failures are reported immediately.
  /// Nothing is ever pending with the instant strategy.
  /// </remarks>
  public TrackedCompletion<PerspectiveCheckpointFailure>[] GetPendingFailures() {
    return [];
  }

  /// <inheritdoc />
  /// <remarks>
  /// No-op for instant strategy since nothing is ever stored.
  /// </remarks>
  public void MarkAsSent(
    TrackedCompletion<PerspectiveCheckpointCompletion>[] completions,
    TrackedCompletion<PerspectiveCheckpointFailure>[] failures,
    DateTimeOffset sentAt) {
    // No-op - nothing to mark since we report immediately
  }

  /// <inheritdoc />
  /// <remarks>
  /// No-op for instant strategy since nothing is ever stored.
  /// </remarks>
  public void MarkAsAcknowledged(int completionCount, int failureCount) {
    // No-op - nothing to acknowledge since we report immediately
  }

  /// <inheritdoc />
  /// <remarks>
  /// No-op for instant strategy since nothing is ever stored.
  /// </remarks>
  public void ClearAcknowledged() {
    // No-op - nothing to clear since we report immediately
  }

  /// <inheritdoc />
  /// <remarks>
  /// No-op for instant strategy since nothing is ever stored.
  /// </remarks>
  public void ResetStale(DateTimeOffset now) {
    // No-op - no stale items since we report immediately
  }

  /// <summary>
  /// Debug log for reporting perspective completion.
  /// Traces immediate completion reporting to coordinator.
  /// </summary>
  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Debug,
    Message = "[InstantCompletionStrategy] Reporting completion: {PerspectiveName}/{StreamId}, lastEventId={LastEventId}"
  )]
  static partial void LogReportingCompletion(ILogger logger, string perspectiveName, Guid streamId, Guid lastEventId);

  /// <summary>
  /// Debug log for successful completion report.
  /// Confirms coordinator received the completion notification.
  /// </summary>
  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Debug,
    Message = "[InstantCompletionStrategy] Completion reported successfully"
  )]
  static partial void LogCompletionReported(ILogger logger);
}
