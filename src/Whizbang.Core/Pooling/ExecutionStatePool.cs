using System.Collections.Concurrent;

namespace Whizbang.Core.Pooling;

/// <summary>
/// Static generic pool for ExecutionState{T} instances.
/// Each generic type T gets its own pool to avoid type casting issues.
/// Uses ConcurrentBag for thread-safe, lock-free pooling.
/// </summary>
/// <typeparam name="T">The result type for the execution state</typeparam>
public static class ExecutionStatePool<T> {
  private static readonly ConcurrentBag<ExecutionState<T>> _pool = [];

  /// <summary>
  /// Gets a pooled ExecutionState{T} or creates a new one if pool is empty.
  /// Caller must call Initialize() before use and Reset() before returning.
  /// </summary>
  public static ExecutionState<T> Rent() {
    if (_pool.TryTake(out var state)) {
      return state;
    }
    return new ExecutionState<T>();
  }

  /// <summary>
  /// Returns an ExecutionState{T} to the pool for reuse.
  /// Caller should call Reset() before returning.
  /// </summary>
  public static void Return(ExecutionState<T> state) {
    _pool.Add(state);
  }
}
