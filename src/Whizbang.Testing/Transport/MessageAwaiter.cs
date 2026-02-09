using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

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
public sealed class MessageAwaiter<TResult> where TResult : notnull {
  private readonly TaskCompletionSource<TResult> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  private readonly Func<IMessageEnvelope, TResult?> _resultExtractor;
  private readonly Predicate<IMessageEnvelope>? _filter;

  /// <summary>
  /// Creates a new message awaiter.
  /// </summary>
  /// <param name="resultExtractor">
  /// Function to extract the result from a received envelope.
  /// Return null to skip the message (e.g., warmup messages).
  /// </param>
  /// <param name="filter">
  /// Optional predicate to filter which messages to process.
  /// If null, all messages are processed.
  /// </param>
  public MessageAwaiter(
    Func<IMessageEnvelope, TResult?> resultExtractor,
    Predicate<IMessageEnvelope>? filter = null
  ) {
    _resultExtractor = resultExtractor ?? throw new ArgumentNullException(nameof(resultExtractor));
    _filter = filter;
  }

  /// <summary>
  /// Gets whether a result has been received.
  /// </summary>
  public bool IsCompleted => _tcs.Task.IsCompleted;

  /// <summary>
  /// Gets the handler delegate to pass to ITransport.SubscribeAsync.
  /// </summary>
  public Func<IMessageEnvelope, string?, CancellationToken, Task> Handler =>
    async (envelope, envelopeType, ct) => {
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
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      return await _tcs.Task.WaitAsync(cts.Token);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException($"No message received within {timeout}");
    }
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
public sealed class MessageIdAwaiter {
  private readonly TaskCompletionSource<string> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>
  /// Gets whether a message has been received.
  /// </summary>
  public bool IsCompleted => _tcs.Task.IsCompleted;

  /// <summary>
  /// Gets the handler delegate to pass to ITransport.SubscribeAsync.
  /// </summary>
  public Func<IMessageEnvelope, string?, CancellationToken, Task> Handler =>
    async (envelope, envelopeType, ct) => {
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
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      return await _tcs.Task.WaitAsync(cts.Token);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException($"No message received within {timeout}");
    }
  }
}
