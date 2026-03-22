using Whizbang.Core;
using Whizbang.Core.Async;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Transport;

/// <summary>
/// A thread-safe message awaiter for transport integration tests.
/// Handles the common pattern of waiting for messages with proper async safety.
/// </summary>
/// <remarks>
/// <para>
/// CRITICAL: This class uses TaskCreationOptions.RunContinuationsAsynchronously
/// to prevent deadlocks when the handler's TrySetResult continuation runs
/// synchronously and calls Dispose() which waits for the handler.
/// </para>
/// <para>
/// Without this flag, the following deadlock occurs:
/// 1. Handler calls TrySetResult()
/// 2. Continuation runs synchronously in same thread
/// 3. Continuation calls subscription.Dispose()
/// 4. Dispose waits for handler via GetAwaiter().GetResult()
/// 5. Handler is waiting for TrySetResult to return - DEADLOCK
/// </para>
/// </remarks>
/// <typeparam name="TResult">The type of result to extract from received messages.</typeparam>
/// <remarks>
/// Creates a new message awaiter.
/// </remarks>
/// <param name="resultExtractor">
/// Function to extract the result from a received envelope.
/// Return null to skip the message (e.g., warmup messages).
/// </param>
/// <param name="filter">
/// Optional predicate to filter which messages to process.
/// If null, all messages are processed.
/// </param>
public sealed class MessageAwaiter<TResult>(
  Func<IMessageEnvelope, TResult?> resultExtractor,
  Predicate<IMessageEnvelope>? filter = null
  ) : IAwaiterIdentity where TResult : notnull {
  private readonly TaskCompletionSource<TResult> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Guid AwaiterId { get; } = TrackedGuid.NewMedo();

  private readonly Func<IMessageEnvelope, TResult?> _resultExtractor = resultExtractor ?? throw new ArgumentNullException(nameof(resultExtractor));
  private readonly Predicate<IMessageEnvelope>? _filter = filter;

  /// <summary>
  /// Gets whether a result has been received.
  /// </summary>
  public bool IsCompleted => _tcs.Task.IsCompleted;

  /// <summary>
  /// Gets the handler delegate to pass to ITransport.SubscribeAsync.
  /// </summary>
  public Func<IMessageEnvelope, string?, CancellationToken, Task> Handler =>
    async (envelope, _, _) => {
      // Apply filter if specified
      if (_filter != null && !_filter(envelope)) {
        return;
      }

      // Try to extract result
      var result = _resultExtractor(envelope);
      if (result != null) {
        _tcs.TrySetResult(result);
      }

      await Task.CompletedTask;
    };

  /// <summary>
  /// Waits for a message to be received.
  /// </summary>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The extracted result.</returns>
  /// <exception cref="TimeoutException">Thrown if no message is received within the timeout.</exception>
  public async Task<TResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
    return await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        _tcs.Task, timeout, $"No message received within {timeout}", cancellationToken);
  }

  /// <summary>
  /// Tries to set the result directly (useful for testing).
  /// </summary>
  public bool TrySetResult(TResult result) => _tcs.TrySetResult(result);

  /// <summary>
  /// Sets an exception as the result.
  /// </summary>
  public void SetException(Exception exception) => _tcs.TrySetException(exception);
}

/// <summary>
/// A simple string-based message awaiter for common test scenarios.
/// Extracts MessageId as the result.
/// </summary>
public sealed class MessageIdAwaiter : IAwaiterIdentity {
  private readonly TaskCompletionSource<string> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Guid AwaiterId { get; } = TrackedGuid.NewMedo();

  /// <summary>
  /// Gets whether a message has been received.
  /// </summary>
  public bool IsCompleted => _tcs.Task.IsCompleted;

  /// <summary>
  /// Gets the handler delegate to pass to ITransport.SubscribeAsync.
  /// </summary>
  public Func<IMessageEnvelope, string?, CancellationToken, Task> Handler =>
    async (envelope, _, _) => {
      _tcs.TrySetResult(envelope.MessageId.ToString());
      await Task.CompletedTask;
    };

  /// <summary>
  /// Waits for a message to be received.
  /// </summary>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The message ID as a string.</returns>
  /// <exception cref="TimeoutException">Thrown if no message is received within the timeout.</exception>
  public async Task<string> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
    return await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        _tcs.Task, timeout, $"No message received within {timeout}", cancellationToken);
  }
}

/// <summary>
/// A counting message awaiter that waits for a specific number of messages.
/// Thread-safe and uses RunContinuationsAsynchronously to prevent deadlocks.
/// </summary>
public sealed class CountingMessageAwaiter : IAwaiterIdentity {
  private readonly TaskCompletionSource<bool> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Guid AwaiterId { get; } = TrackedGuid.NewMedo();

  private readonly int _expectedCount;
  private int _receivedCount;

  /// <summary>
  /// Creates a new counting message awaiter.
  /// </summary>
  /// <param name="expectedCount">Number of messages to wait for.</param>
  public CountingMessageAwaiter(int expectedCount) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);
    _expectedCount = expectedCount;
  }

  /// <summary>
  /// Gets the number of messages received so far.
  /// </summary>
  public int ReceivedCount => _receivedCount;

  /// <summary>
  /// Gets the expected message count.
  /// </summary>
  public int ExpectedCount => _expectedCount;

  /// <summary>
  /// Gets whether all expected messages have been received.
  /// </summary>
  public bool IsCompleted => _tcs.Task.IsCompleted;

  /// <summary>
  /// Gets the handler delegate to pass to ITransport.SubscribeAsync.
  /// </summary>
  public Func<IMessageEnvelope, string?, CancellationToken, Task> Handler =>
    async (_, _, _) => {
      if (Interlocked.Increment(ref _receivedCount) >= _expectedCount) {
        _tcs.TrySetResult(true);
      }
      await Task.CompletedTask;
    };

  /// <summary>
  /// Waits for all expected messages to be received.
  /// </summary>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if not all messages are received within the timeout.</exception>
  public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
    await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        _tcs.Task, timeout,
        $"Expected {_expectedCount} messages but only received {_receivedCount} within {timeout}",
        cancellationToken);
  }
}
