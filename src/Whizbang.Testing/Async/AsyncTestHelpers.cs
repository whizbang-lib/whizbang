namespace Whizbang.Testing.Async;

/// <summary>
/// Provides helper methods for async test scenarios, replacing flaky Task.Delay patterns
/// with reliable condition-based waiting.
/// </summary>
/// <remarks>
/// <para>
/// This class addresses common sources of test flakiness:
/// </para>
/// <list type="bullet">
///   <item><description>Task.WhenAny(tcs.Task, Task.Delay(...)) - Race condition where delay can win under load</description></item>
///   <item><description>Bare Task.Delay() for synchronization - Arbitrary timeouts don't scale</description></item>
///   <item><description>Task.Delay() for negative tests - Cannot reliably prove something didn't happen</description></item>
/// </list>
/// </remarks>
public static class AsyncTestHelpers {
  /// <summary>
  /// Default poll interval for condition checks.
  /// </summary>
  public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(10);

  /// <summary>
  /// Waits for a synchronous condition to become true with polling.
  /// </summary>
  /// <param name="condition">The condition to check. Must be thread-safe.</param>
  /// <param name="timeout">Maximum time to wait for the condition.</param>
  /// <param name="pollInterval">How often to check the condition. Defaults to 10ms.</param>
  /// <param name="timeoutMessage">Optional message for the timeout exception.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if condition is not met within timeout.</exception>
  /// <example>
  /// <code>
  /// // Wait for worker to process at least one item
  /// await AsyncTestHelpers.WaitForConditionAsync(
  ///     () => worker.ProcessedCount > 0,
  ///     TimeSpan.FromSeconds(5));
  /// </code>
  /// </example>
  public static async Task WaitForConditionAsync(
    Func<bool> condition,
    TimeSpan timeout,
    TimeSpan? pollInterval = null,
    string? timeoutMessage = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(condition);

    var interval = pollInterval ?? DefaultPollInterval;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      while (!condition()) {
        await Task.Delay(interval, cts.Token);
      }
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(timeoutMessage ?? $"Condition not met within {timeout}");
    }
  }

  /// <summary>
  /// Waits for an async condition to become true with polling.
  /// </summary>
  /// <param name="condition">The async condition to check. Must be thread-safe.</param>
  /// <param name="timeout">Maximum time to wait for the condition.</param>
  /// <param name="pollInterval">How often to check the condition. Defaults to 10ms.</param>
  /// <param name="timeoutMessage">Optional message for the timeout exception.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if condition is not met within timeout.</exception>
  /// <example>
  /// <code>
  /// // Wait for database record to appear
  /// await AsyncTestHelpers.WaitForConditionAsync(
  ///     async () => await db.ExistsAsync(id),
  ///     TimeSpan.FromSeconds(10));
  /// </code>
  /// </example>
  public static async Task WaitForConditionAsync(
    Func<Task<bool>> condition,
    TimeSpan timeout,
    TimeSpan? pollInterval = null,
    string? timeoutMessage = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(condition);

    var interval = pollInterval ?? DefaultPollInterval;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      while (!await condition()) {
        await Task.Delay(interval, cts.Token);
      }
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(timeoutMessage ?? $"Condition not met within {timeout}");
    }
  }

  /// <summary>
  /// Asserts that a condition remains false for a specified duration.
  /// This is more reliable than <c>Task.Delay()</c> followed by an assertion.
  /// </summary>
  /// <param name="condition">The condition that should remain false. Must be thread-safe.</param>
  /// <param name="duration">How long to monitor the condition.</param>
  /// <param name="pollInterval">How often to check the condition. Defaults to 10ms.</param>
  /// <param name="failureMessage">Message to include in the exception if condition becomes true.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="AssertionException">Thrown if condition becomes true during the duration.</exception>
  /// <example>
  /// <code>
  /// // Assert that no signal was received (negative test)
  /// await AsyncTestHelpers.AssertNeverAsync(
  ///     () => signalCount > 0,
  ///     TimeSpan.FromMilliseconds(200),
  ///     failureMessage: "Signal was received when it should not have been");
  /// </code>
  /// </example>
  public static async Task AssertNeverAsync(
    Func<bool> condition,
    TimeSpan duration,
    TimeSpan? pollInterval = null,
    string? failureMessage = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(condition);

    var interval = pollInterval ?? DefaultPollInterval;
    var deadline = DateTime.UtcNow + duration;

    while (DateTime.UtcNow < deadline) {
      cancellationToken.ThrowIfCancellationRequested();

      if (condition()) {
        throw new AssertionException(
          failureMessage ?? "Condition became true when it should have remained false");
      }

      // Don't wait past the deadline
      var remaining = deadline - DateTime.UtcNow;
      if (remaining > TimeSpan.Zero) {
        var waitTime = remaining < interval ? remaining : interval;
        await Task.Delay(waitTime, cancellationToken);
      }
    }

    // Final check at deadline
    if (condition()) {
      throw new AssertionException(
        failureMessage ?? "Condition became true when it should have remained false");
    }
  }

  /// <summary>
  /// Asserts that an async condition remains false for a specified duration.
  /// </summary>
  /// <param name="condition">The async condition that should remain false. Must be thread-safe.</param>
  /// <param name="duration">How long to monitor the condition.</param>
  /// <param name="pollInterval">How often to check the condition. Defaults to 10ms.</param>
  /// <param name="failureMessage">Message to include in the exception if condition becomes true.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="AssertionException">Thrown if condition becomes true during the duration.</exception>
  public static async Task AssertNeverAsync(
    Func<Task<bool>> condition,
    TimeSpan duration,
    TimeSpan? pollInterval = null,
    string? failureMessage = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(condition);

    var interval = pollInterval ?? DefaultPollInterval;
    var deadline = DateTime.UtcNow + duration;

    while (DateTime.UtcNow < deadline) {
      cancellationToken.ThrowIfCancellationRequested();

      if (await condition()) {
        throw new AssertionException(
          failureMessage ?? "Condition became true when it should have remained false");
      }

      var remaining = deadline - DateTime.UtcNow;
      if (remaining > TimeSpan.Zero) {
        var waitTime = remaining < interval ? remaining : interval;
        await Task.Delay(waitTime, cancellationToken);
      }
    }

    // Final check at deadline
    if (await condition()) {
      throw new AssertionException(
        failureMessage ?? "Condition became true when it should have remained false");
    }
  }

  /// <summary>
  /// Waits for a value to match an expected condition with polling.
  /// Returns the value when the condition is met.
  /// </summary>
  /// <typeparam name="T">The type of value to check.</typeparam>
  /// <param name="getValue">Function to get the current value. Must be thread-safe.</param>
  /// <param name="predicate">Condition the value must satisfy.</param>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="pollInterval">How often to check. Defaults to 10ms.</param>
  /// <param name="timeoutMessage">Optional message for the timeout exception.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The value that satisfied the condition.</returns>
  /// <exception cref="TimeoutException">Thrown if condition is not met within timeout.</exception>
  /// <example>
  /// <code>
  /// // Wait for count to reach 5 and get the final value
  /// var count = await AsyncTestHelpers.WaitForValueAsync(
  ///     () => counter.Value,
  ///     value => value >= 5,
  ///     TimeSpan.FromSeconds(5));
  /// </code>
  /// </example>
  public static async Task<T> WaitForValueAsync<T>(
    Func<T> getValue,
    Func<T, bool> predicate,
    TimeSpan timeout,
    TimeSpan? pollInterval = null,
    string? timeoutMessage = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(getValue);
    ArgumentNullException.ThrowIfNull(predicate);

    var interval = pollInterval ?? DefaultPollInterval;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      T value;
      while (!predicate(value = getValue())) {
        await Task.Delay(interval, cts.Token);
      }
      return value;
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException(timeoutMessage ?? $"Value condition not met within {timeout}");
    }
  }
}

/// <summary>
/// Exception thrown when an async test assertion fails.
/// </summary>
public sealed class AssertionException : Exception {
  /// <summary>
  /// Creates a new assertion exception.
  /// </summary>
  public AssertionException() : base() { }

  /// <summary>
  /// Creates a new assertion exception with the specified message.
  /// </summary>
  /// <param name="message">The assertion failure message.</param>
  public AssertionException(string message) : base(message) { }

  /// <summary>
  /// Creates a new assertion exception with the specified message and inner exception.
  /// </summary>
  /// <param name="message">The assertion failure message.</param>
  /// <param name="innerException">The inner exception.</param>
  public AssertionException(string message, Exception innerException) : base(message, innerException) { }
}
