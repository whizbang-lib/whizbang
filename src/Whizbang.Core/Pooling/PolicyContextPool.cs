using System.Collections.Concurrent;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Pooling;

/// <summary>
/// Object pool for PolicyContext instances.
/// Reduces heap allocations by reusing PolicyContext objects.
/// Thread-safe and lock-free using ConcurrentBag.
/// </summary>
public static class PolicyContextPool {
  private static readonly ConcurrentBag<PolicyContext> _pool = new();
  private static int _poolSize = 0;
  private const int MaxPoolSize = 1024;

  /// <summary>
  /// Rents a PolicyContext from the pool and initializes it with the specified values.
  /// If the pool is empty, creates a new instance.
  /// </summary>
  public static PolicyContext Rent(
      object message,
      IMessageEnvelope? envelope,
      IServiceProvider? services,
      string environment
  ) {
    PolicyContext? context = null;

    if (_pool.TryTake(out context)) {
      Interlocked.Decrement(ref _poolSize);
    } else {
      context = new PolicyContext();
    }

    context.Initialize(message, envelope, services, environment);
    return context;
  }

  /// <summary>
  /// Returns a PolicyContext to the pool after resetting it.
  /// If the pool is full, the context is discarded and will be garbage collected.
  /// </summary>
  public static void Return(PolicyContext? context) {
    if (context is null) {
      return;
    }

    // Reset the context before returning to pool
    context.Reset();

    // Only add back to pool if we haven't reached max size
    if (_poolSize < MaxPoolSize) {
      _pool.Add(context);
      Interlocked.Increment(ref _poolSize);
    }
    // If pool is full, let context be garbage collected
  }
}
