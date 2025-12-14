using System.Collections.Concurrent;

namespace Whizbang.Core.Pooling;

/// <summary>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:Rent_ReturnsValidInstance_AlwaysAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:Rent_ReturnsPooledInstance_AfterReturnAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:Return_MakesInstanceAvailableForReuseAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RentReturn_ReusesSameInstance_VerifyReferenceEqualityAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RentAfterReset_HasIncrementedTokenAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:GenericTypes_HaveSeparatePools_IntVsStringAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:GenericTypes_HaveSeparatePools_CustomTypesAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:MultipleRentReturn_WorksCorrectly_ParameterizedAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:SequentialRentReturn_ReusesSingleInstanceAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:ConcurrentRentReturn_ThreadSafe_ParallelOperationsAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:ConcurrentRent_CreatesMultipleInstances_WhenPoolEmptyAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RealisticPattern_RentSetResultReturnAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RealisticPattern_HighThroughput_MinimalAllocationsAsync</tests>
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
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:Rent_ReturnsValidInstance_AlwaysAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:Rent_ReturnsPooledInstance_AfterReturnAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RentAfterReset_HasIncrementedTokenAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:MultipleRentReturn_WorksCorrectly_ParameterizedAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:SequentialRentReturn_ReusesSingleInstanceAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:ConcurrentRentReturn_ThreadSafe_ParallelOperationsAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:ConcurrentRent_CreatesMultipleInstances_WhenPoolEmptyAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RealisticPattern_RentSetResultReturnAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RealisticPattern_HighThroughput_MinimalAllocationsAsync</tests>
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
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:Return_MakesInstanceAvailableForReuseAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RentReturn_ReusesSameInstance_VerifyReferenceEqualityAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:MultipleRentReturn_WorksCorrectly_ParameterizedAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:SequentialRentReturn_ReusesSingleInstanceAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:ConcurrentRentReturn_ThreadSafe_ParallelOperationsAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RealisticPattern_RentSetResultReturnAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/PooledSourcePoolTests.cs:RealisticPattern_HighThroughput_MinimalAllocationsAsync</tests>
  public static void Return(PooledValueTaskSource<T> source) {
    _pool.Add(source);
  }
}
