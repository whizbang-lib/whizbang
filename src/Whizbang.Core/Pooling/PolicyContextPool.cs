using System.Collections.Concurrent;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Pooling;

/// <summary>
/// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Rent_ShouldReturnInitializedContextAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Return_WithNullContext_ShouldNotThrowAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:RentReturn_ShouldReinitializeContextAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Pool_ShouldCreateNewContext_WhenEmptyAsync</tests>
/// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Pool_ShouldNotExceedMaxSize_WhenReturningManyContextsAsync</tests>
/// Object pool for PolicyContext instances.
/// Reduces heap allocations by reusing PolicyContext objects.
/// Thread-safe and lock-free using ConcurrentBag.
/// </summary>
public static class PolicyContextPool {
  private static readonly ConcurrentBag<PolicyContext> _pool = [];
  private static int _poolSize;
  private const int MAX_POOL_SIZE = 1024;

  /// <summary>
  /// Rents a PolicyContext from the pool and initializes it with the specified values.
  /// If the pool is empty, creates a new instance.
  /// </summary>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Rent_ShouldReturnInitializedContextAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:RentReturn_ShouldReinitializeContextAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Pool_ShouldCreateNewContext_WhenEmptyAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Pool_ShouldNotExceedMaxSize_WhenReturningManyContextsAsync</tests>
  public static PolicyContext Rent(
      object message,
      IMessageEnvelope? envelope,
      IServiceProvider? services,
      string environment
  ) {
    if (_pool.TryTake(out var context)) {
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
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Return_WithNullContext_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:RentReturn_ShouldReinitializeContextAsync</tests>
  /// <tests>tests/Whizbang.Policies.Tests/PolicyContextPoolTests.cs:Pool_ShouldNotExceedMaxSize_WhenReturningManyContextsAsync</tests>
  public static void Return(PolicyContext? context) {
    if (context is null) {
      return;
    }

    // Reset the context before returning to pool
    context.Reset();

    // Only add back to pool if we haven't reached max size
    if (_poolSize < MAX_POOL_SIZE) {
      _pool.Add(context);
      Interlocked.Increment(ref _poolSize);
    }
    // If pool is full, let context be garbage collected
  }
}
