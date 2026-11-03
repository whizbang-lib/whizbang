// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Tasks.Sources;

namespace Whizbang.Core.Pooling;

/// <summary>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_NewInstance_StartsWithVersionZeroAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_Reset_IncrementsVersionTokenAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_MultipleResets_TokenIncreasesMonotonicallyAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_ReuseAfterReset_WorksCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_CompletesValueTaskAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_VariousIntegers_ReturnsCorrectValueAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_VariousStrings_ReturnsCorrectValueAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResultNull_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_Pending_ReturnsCorrectStatusAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_AfterSetResult_ReturnsSucceededAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetException_ThrowsOnAwaitAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetException_VariousExceptionTypes_PreservesExceptionTypeAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_AfterSetException_ReturnsFaultedAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetResult_WithStaleToken_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_WithStaleToken_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_AwaitWithStaleToken_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_ConcurrentAwait_OnSameToken_WorksCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_MultipleAwaitsAfterCompletion_WorksAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SimplePoolingPattern_WorksCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_ManyOperations_WithPooling_NoErrorsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_DoubleSetResult_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResultAfterSetException_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetExceptionAfterSetResult_ThrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetResult_BeforeCompletion_BlocksUntilSetAsync</tests>
/// Poolable implementation of IValueTaskSource{T} for zero-allocation async patterns.
/// Supports versioning to prevent stale ValueTask reuse.
/// Thread-safe and designed for object pooling.
/// </summary>
/// <typeparam name="T">The result type</typeparam>
public sealed class PooledValueTaskSource<T> : IValueTaskSource<T> {
  private short _token;
  private T? _result;
  private Exception? _exception;
  private ValueTaskSourceStatus _status;
  private Action<object?>? _continuation;
  private object? _continuationState;

  /// <summary>
  /// Takes the continuation's place once the source completes. The completing side and the awaiting side each make one
  /// atomic exchange on <see cref="_continuation"/>, so exactly one of them runs the continuation, once. Plain fields
  /// written on one side and read on the other let both read before either write was visible: the wake-up was lost and
  /// the awaiter waited forever, or the continuation ran without its state.
  /// </summary>
  private static readonly Action<object?> _completedSentinel = static _ => { };

  /// <summary>
  /// Fires when a consumer registers a continuation (begins awaiting this source).
  /// Useful for monitoring pool utilization and signal-based test synchronization.
  /// </summary>
  public event Action? OnContinuationRegistered;

  /// <summary>
  /// Gets the current version token for this source.
  /// Token increments on Reset() to invalidate old ValueTask instances.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_NewInstance_StartsWithVersionZeroAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_Reset_IncrementsVersionTokenAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_MultipleResets_TokenIncreasesMonotonicallyAsync</tests>
  public short Token => _token;

  /// <summary>
  /// Resets the source for reuse and increments the version token.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_Reset_IncrementsVersionTokenAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_MultipleResets_TokenIncreasesMonotonicallyAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_ReuseAfterReset_WorksCorrectlyAsync</tests>
  public void Reset() {
    _token++;
    _result = default;
    _exception = null;
    _status = ValueTaskSourceStatus.Pending;
    _continuation = null;
    _continuationState = null;
  }

  /// <summary>
  /// Sets the result value and completes the ValueTask.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_CompletesValueTaskAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_VariousIntegers_ReturnsCorrectValueAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_VariousStrings_ReturnsCorrectValueAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResultNull_ReturnsNullAsync</tests>
  public void SetResult(T result) {
    if (_status != ValueTaskSourceStatus.Pending) {
      throw new InvalidOperationException("Cannot set result on a completed source");
    }

    _result = result;
    _status = ValueTaskSourceStatus.Succeeded;
    _signalCompletion();
  }

  /// <summary>
  /// Sets an exception and faults the ValueTask.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetException_ThrowsOnAwaitAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetException_VariousExceptionTypes_PreservesExceptionTypeAsync</tests>
  public void SetException(Exception exception) {
    if (_status != ValueTaskSourceStatus.Pending) {
      throw new InvalidOperationException("Cannot set exception on a completed source");
    }

    ArgumentNullException.ThrowIfNull(exception);
    _exception = exception;
    _status = ValueTaskSourceStatus.Faulted;
    _signalCompletion();
  }

  /// <summary>
  /// Gets the current status of the operation.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_Pending_ReturnsCorrectStatusAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_AfterSetResult_ReturnsSucceededAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetStatus_AfterSetException_ReturnsFaultedAsync</tests>
  public ValueTaskSourceStatus GetStatus(short token) {
    _validateToken(token);
    return _status;
  }

  /// <summary>
  /// Gets the result of the operation. Throws if faulted.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_SetResult_CompletesValueTaskAsync</tests>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_GetResult_WithStaleToken_ThrowsAsync</tests>
  public T GetResult(short token) {
    _validateToken(token);

    if (_status == ValueTaskSourceStatus.Succeeded) {
      return _result!;
    }

    if (_status == ValueTaskSourceStatus.Faulted) {
      throw _exception!;
    }

    throw new InvalidOperationException("Cannot get result from incomplete operation");
  }

  /// <summary>
  /// Registers a continuation callback (required by IValueTaskSource). Registered while the source is pending, the
  /// continuation runs when it completes; registered after, it runs now. Either way it runs once, on the thread pool.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Component.Tests/PooledValueTaskSourceTests.cs:PooledValueTaskSource_CompletionRacingRegistration_ResumesTheAwaiterExactlyOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Pooling/PooledValueTaskSourceContractTests.cs:OnCompleted_CalledTwice_RejectsTheSecondRegistrationAsync</tests>
  public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) {
    _validateToken(token);
    ArgumentNullException.ThrowIfNull(continuation);

    // The state is written before the continuation is published; the exchange is a full fence, so a completer that
    // takes the continuation also sees its state. A second registration is refused, and the first one's state restored.
    var priorState = _continuationState;
    _continuationState = state;
    var previous = Interlocked.CompareExchange(ref _continuation, continuation, null);
    if (previous is not null && !ReferenceEquals(previous, _completedSentinel)) {
      _continuationState = priorState;
      throw new InvalidOperationException("OnCompleted already called");
    }

    OnContinuationRegistered?.Invoke();

    // Completed before the continuation could be published: the completer found nothing to run, so this side runs it.
    if (ReferenceEquals(previous, _completedSentinel)) {
      _invokeContinuation(continuation, state);
    }
  }

  private void _validateToken(short token) {
    if (token != _token) {
      throw new InvalidOperationException($"Invalid token: expected {_token}, got {token}");
    }
  }

  private void _signalCompletion() {
    // The result and status are written before this exchange, which is a full fence: an awaiter that finds the
    // sentinel, or runs the continuation taken here, reads the completed result.
    // Only a second completion racing the first (refused when it is not concurrent) can take the sentinel itself back,
    // and running the sentinel does nothing.
    var continuation = Interlocked.Exchange(ref _continuation, _completedSentinel);
    if (continuation is not null) {
      _invokeContinuation(continuation, _continuationState);
    }
  }

  private static void _invokeContinuation(Action<object?> continuation, object? state) {
    // Invoke continuation on thread pool to avoid stack overflow
    ThreadPool.QueueUserWorkItem(static s => {
      var tuple = ((Action<object?>, object?))s!;
      tuple.Item1(tuple.Item2);
    }, (continuation, state));
  }
}
