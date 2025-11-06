using System.Threading.Tasks.Sources;

namespace Whizbang.Core.Pooling;

/// <summary>
/// Poolable implementation of IValueTaskSource{T} for zero-allocation async operations.
/// Uses ManualResetValueTaskSourceCore{T} for state management and token-based safety.
/// </summary>
/// <typeparam name="TResult">The type of result this source will produce</typeparam>
public sealed class PooledValueTaskSource<TResult> : IValueTaskSource<TResult> {
  private ManualResetValueTaskSourceCore<TResult> _core;

  /// <summary>
  /// Initializes a new PooledValueTaskSource with default configuration.
  /// </summary>
  public PooledValueTaskSource() {
    _core = new ManualResetValueTaskSourceCore<TResult> {
      // Run continuations asynchronously to avoid sync context issues
      RunContinuationsAsynchronously = true
    };
  }

  /// <summary>
  /// Gets the current version token. This token prevents reuse bugs by invalidating
  /// stale ValueTask instances after the source is reset.
  /// </summary>
  public short Token => _core.Version;

  /// <summary>
  /// Resets the source for reuse. Increments the version token.
  /// Must be called before returning to the pool or reusing.
  /// </summary>
  public void Reset() => _core.Reset();

  /// <summary>
  /// Sets the result and signals completion to any awaiting continuations.
  /// </summary>
  /// <param name="result">The result value</param>
  public void SetResult(TResult result) => _core.SetResult(result);

  /// <summary>
  /// Sets an exception and signals faulted completion to any awaiting continuations.
  /// </summary>
  /// <param name="error">The exception to propagate</param>
  public void SetException(Exception error) => _core.SetException(error);

  // IValueTaskSource<TResult> implementation - delegates to _core

  /// <inheritdoc />
  public TResult GetResult(short token) => _core.GetResult(token);

  /// <inheritdoc />
  public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

  /// <inheritdoc />
  public void OnCompleted(
    Action<object?> continuation,
    object? state,
    short token,
    ValueTaskSourceOnCompletedFlags flags
  ) => _core.OnCompleted(continuation, state, token, flags);
}
