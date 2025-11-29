using System.Collections.Concurrent;

namespace Whizbang.Core.Pooling;

/// <summary>
/// Static generic pool for PooledValueTaskSource{T} instances.
/// Each generic type T gets its own pool to avoid type casting issues.
/// Uses ConcurrentBag for thread-safe, lock-free pooling.
/// </summary>
/// <typeparam name="T">The result type for the value task sources</typeparam>
public static class PooledSourcePool<T> {
  private static readonly ConcurrentBag<PooledValueTaskSource<T>> _pool = [];

  /// <summary>
  /// Gets a pooled PooledValueTaskSource{T} or creates a new one if pool is empty.
  /// Caller is responsible for calling Reset() before use.
  /// </summary>
  public static PooledValueTaskSource<T> Rent() {
    if (_pool.TryTake(out var source)) {
      return source;
    }
    return new PooledValueTaskSource<T>();
  }

  /// <summary>
  /// Returns a PooledValueTaskSource{T} to the pool for reuse.
  /// Caller should call Reset() before returning.
  /// </summary>
  public static void Return(PooledValueTaskSource<T> source) {
    _pool.Add(source);
  }
}
