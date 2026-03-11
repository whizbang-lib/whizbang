namespace Whizbang.Core.Async;

/// <summary>
/// Provides shared timeout and cancellation handling for async wait operations.
/// </summary>
/// <remarks>
/// Extracts the common pattern used across all awaiter classes:
/// <c>CreateLinkedTokenSource</c> → <c>CancelAfter</c> → <c>WaitAsync</c> →
/// catch <see cref="OperationCanceledException"/> → throw <see cref="TimeoutException"/>.
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#awaiter-identity</docs>
/// <tests>Whizbang.Core.Tests/Async/AsyncTimeoutHelperTests.cs</tests>
public static class AsyncTimeoutHelper {
  /// <summary>
  /// Waits for a task to complete within the specified timeout.
  /// </summary>
  /// <param name="task">The task to wait for.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="timeoutMessage">Message for the <see cref="TimeoutException"/> if timeout is exceeded.</param>
  /// <param name="cancellationToken">External cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown when the timeout expires before the task completes.</exception>
  /// <exception cref="OperationCanceledException">Thrown when the external <paramref name="cancellationToken"/> is cancelled.</exception>
  public static async Task WaitWithTimeoutAsync(
      Task task,
      TimeSpan timeout,
      string timeoutMessage,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(task);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      await task.WaitAsync(cts.Token);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(timeoutMessage);
    }
  }

  /// <summary>
  /// Waits for a task to complete within the specified timeout and returns its result.
  /// </summary>
  /// <typeparam name="T">The result type of the task.</typeparam>
  /// <param name="task">The task to wait for.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="timeoutMessage">Message for the <see cref="TimeoutException"/> if timeout is exceeded.</param>
  /// <param name="cancellationToken">External cancellation token.</param>
  /// <returns>The result of the completed task.</returns>
  /// <exception cref="TimeoutException">Thrown when the timeout expires before the task completes.</exception>
  /// <exception cref="OperationCanceledException">Thrown when the external <paramref name="cancellationToken"/> is cancelled.</exception>
  public static async Task<T> WaitWithTimeoutAsync<T>(
      Task<T> task,
      TimeSpan timeout,
      string timeoutMessage,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(task);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      return await task.WaitAsync(cts.Token);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(timeoutMessage);
    }
  }
}
