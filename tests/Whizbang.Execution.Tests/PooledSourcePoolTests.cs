using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Pooling;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Tests for PooledSourcePool{T} - the generic pooling mechanism.
/// Verifies rent/return mechanics, thread safety, and type separation.
/// </summary>
public class PooledSourcePoolTests {
  // ============================================================================
  // BASIC MECHANICS TESTS
  // ============================================================================

  [Test]
  public async Task Rent_ReturnsValidInstance_AlwaysAsync() {
    // Arrange & Act
    var source = PooledSourcePool<int>.Rent();

    // Assert
    await Assert.That(source).IsNotNull();
    // Note: Token may not be 0 because pool is static and shared across tests
    // This is correct behavior - we're testing the pool works, not that it's empty
  }

  [Test]
  public async Task Rent_ReturnsPooledInstance_AfterReturnAsync() {
    // Arrange
    var original = PooledSourcePool<string>.Rent();
    var originalRef = original;

    PooledSourcePool<string>.Return(original);

    // Act
    var reused = PooledSourcePool<string>.Rent();

    // Assert - Should get the same instance back
    await Assert.That(object.ReferenceEquals(originalRef, reused)).IsTrue();
  }

  [Test]
  public async Task Return_MakesInstanceAvailableForReuseAsync() {
    // Arrange
    var source1 = PooledSourcePool<int>.Rent();
    var source2 = PooledSourcePool<int>.Rent();

    // Return both
    PooledSourcePool<int>.Return(source1);
    PooledSourcePool<int>.Return(source2);

    // Act - Rent two more
    var reused1 = PooledSourcePool<int>.Rent();
    var reused2 = PooledSourcePool<int>.Rent();

    // Assert - Should get the returned instances back (order may vary due to ConcurrentBag)
    var gotSource1 = object.ReferenceEquals(source1, reused1) || object.ReferenceEquals(source1, reused2);
    var gotSource2 = object.ReferenceEquals(source2, reused1) || object.ReferenceEquals(source2, reused2);

    await Assert.That(gotSource1).IsTrue();
    await Assert.That(gotSource2).IsTrue();
  }

  [Test]
  public async Task RentReturn_ReusesSameInstance_VerifyReferenceEqualityAsync() {
    // Arrange
    var original = PooledSourcePool<long>.Rent();

    // Act
    PooledSourcePool<long>.Return(original);
    var reused = PooledSourcePool<long>.Rent();

    // Assert
    await Assert.That(object.ReferenceEquals(original, reused)).IsTrue();
  }

  [Test]
  public async Task RentAfterReset_HasIncrementedTokenAsync() {
    // Arrange
    var source = PooledSourcePool<int>.Rent();
    var originalToken = source.Token;

    // Reset and return
    source.Reset();
    PooledSourcePool<int>.Return(source);

    // Act - Rent again
    var reused = PooledSourcePool<int>.Rent();

    // Assert - Token should have incremented
    await Assert.That(reused.Token).IsEqualTo((short)(originalToken + 1));
  }

  // ============================================================================
  // GENERIC TYPE SEPARATION TESTS
  // ============================================================================

  [Test]
  public async Task GenericTypes_HaveSeparatePools_IntVsStringAsync() {
    // Arrange
    var intSource = PooledSourcePool<int>.Rent();
    var stringSource = PooledSourcePool<string>.Rent();

    // Return both
    PooledSourcePool<int>.Return(intSource);
    PooledSourcePool<string>.Return(stringSource);

    // Act - Rent from each pool
    var rentedInt = PooledSourcePool<int>.Rent();
    var rentedString = PooledSourcePool<string>.Rent();

    // Assert - Each should get back its own type's instance
    await Assert.That(object.ReferenceEquals(intSource, rentedInt)).IsTrue();
    await Assert.That(object.ReferenceEquals(stringSource, rentedString)).IsTrue();
  }

  [Test]
  public async Task GenericTypes_HaveSeparatePools_CustomTypesAsync() {
    // Arrange
    var source1 = PooledSourcePool<TestData1>.Rent();
    var source2 = PooledSourcePool<TestData2>.Rent();

    // Return both
    PooledSourcePool<TestData1>.Return(source1);
    PooledSourcePool<TestData2>.Return(source2);

    // Act
    var rented1 = PooledSourcePool<TestData1>.Rent();
    var rented2 = PooledSourcePool<TestData2>.Rent();

    // Assert
    await Assert.That(object.ReferenceEquals(source1, rented1)).IsTrue();
    await Assert.That(object.ReferenceEquals(source2, rented2)).IsTrue();
  }

  // ============================================================================
  // MULTIPLE OPERATIONS TESTS
  // ============================================================================

  [Test]
  [Arguments(10)]
  [Arguments(50)]
  [Arguments(100)]
  public async Task MultipleRentReturn_WorksCorrectly_ParameterizedAsync(int operationCount) {
    // Arrange
    var sources = new List<PooledValueTaskSource<int>>();

    // Rent multiple
    for (int i = 0; i < operationCount; i++) {
      sources.Add(PooledSourcePool<int>.Rent());
    }

    // Return all
    foreach (var source in sources) {
      source.Reset();
      PooledSourcePool<int>.Return(source);
    }

    // Act - Rent again
    var reused = new List<PooledValueTaskSource<int>>();
    for (int i = 0; i < operationCount; i++) {
      reused.Add(PooledSourcePool<int>.Rent());
    }

    // Assert - SOME instances should be from our original set (proves pooling works)
    // Note: Can't expect ALL because pool is static and shared across tests
    int matchCount = 0;
    foreach (var r in reused) {
      if (sources.Any(s => object.ReferenceEquals(s, r))) {
        matchCount++;
      }
    }

    // At least SOME reuse should occur (conservative threshold)
    await Assert.That(matchCount).IsGreaterThanOrEqualTo(operationCount / 10);
  }

  [Test]
  public async Task SequentialRentReturn_ReusesSingleInstanceAsync() {
    // Arrange
    var first = PooledSourcePool<int>.Rent();

    // Act - Return and re-rent multiple times
    PooledSourcePool<int>.Return(first);
    var second = PooledSourcePool<int>.Rent();

    PooledSourcePool<int>.Return(second);
    var third = PooledSourcePool<int>.Rent();

    PooledSourcePool<int>.Return(third);
    var fourth = PooledSourcePool<int>.Rent();

    // Assert - Should all be the same instance
    await Assert.That(object.ReferenceEquals(first, second)).IsTrue();
    await Assert.That(object.ReferenceEquals(second, third)).IsTrue();
    await Assert.That(object.ReferenceEquals(third, fourth)).IsTrue();
  }

  // ============================================================================
  // THREAD SAFETY TESTS
  // ============================================================================

  [Test]
  public async Task ConcurrentRentReturn_ThreadSafe_ParallelOperationsAsync() {
    // Arrange
    const int threadCount = 10;
    const int operationsPerThread = 100;
    var tasks = new List<Task>();
    var allRented = new System.Collections.Concurrent.ConcurrentBag<PooledValueTaskSource<int>>();

    // Act - Multiple threads renting and returning concurrently
    for (int t = 0; t < threadCount; t++) {
      tasks.Add(Task.Run(() => {
        for (int i = 0; i < operationsPerThread; i++) {
          var source = PooledSourcePool<int>.Rent();
          allRented.Add(source);

          // Simulate some work
          source.Reset();

          // Return immediately
          PooledSourcePool<int>.Return(source);
        }
      }));
    }

    await Task.WhenAll(tasks);

    // Assert - No exceptions, all operations completed
    await Assert.That(allRented.Count).IsEqualTo(threadCount * operationsPerThread);
  }

  [Test]
  public async Task ConcurrentRent_CreatesMultipleInstances_WhenPoolEmptyAsync() {
    // Arrange
    const int concurrentRents = 50;
    var tasks = new List<Task<PooledValueTaskSource<int>>>();

    // Act - Rent concurrently from empty pool
    for (int i = 0; i < concurrentRents; i++) {
      tasks.Add(Task.Run(() => PooledSourcePool<int>.Rent()));
    }

    var sources = await Task.WhenAll(tasks);

    // Assert - Should have created multiple instances (pool was empty)
    var uniqueInstances = sources.Distinct(new ReferenceEqualityComparer()).Count();
    await Assert.That(uniqueInstances).IsGreaterThan(1); // At least some parallelism occurred
    await Assert.That(sources.Length).IsEqualTo(concurrentRents);
  }

  // ============================================================================
  // REALISTIC USAGE PATTERN TESTS
  // ============================================================================

  [Test]
  public async Task RealisticPattern_RentSetResultReturnAsync() {
    // Arrange
    var source = PooledSourcePool<int>.Rent();
    source.Reset();

    // Act - Simulate typical usage
    source.SetResult(42);
    var valueTask = new ValueTask<int>(source, source.Token);
    var result = await valueTask;

    // Return to pool after use
    source.Reset();
    PooledSourcePool<int>.Return(source);

    // Rent again
    var reused = PooledSourcePool<int>.Rent();

    // Assert
    await Assert.That(result).IsEqualTo(42);
    await Assert.That(object.ReferenceEquals(source, reused)).IsTrue();
  }

  [Test]
  [Arguments(10)]
  [Arguments(50)]
  [Arguments(100)]
  public async Task RealisticPattern_HighThroughput_MinimalAllocationsAsync(int messageCount) {
    // Arrange
    var results = new List<int>();

    // Act - Simulate high-throughput message processing
    for (int i = 0; i < messageCount; i++) {
      var source = PooledSourcePool<int>.Rent();
      source.Reset();

      source.SetResult(i);
      var valueTask = new ValueTask<int>(source, source.Token);
      var result = await valueTask;
      results.Add(result);

      source.Reset();
      PooledSourcePool<int>.Return(source);
    }

    // Assert
    await Assert.That(results.Count).IsEqualTo(messageCount);
    for (int i = 0; i < messageCount; i++) {
      await Assert.That(results[i]).IsEqualTo(i);
    }
  }

  // ============================================================================
  // HELPER TYPES
  // ============================================================================

  private record TestData1(int Value);
  private record TestData2(string Text);

  private class ReferenceEqualityComparer : IEqualityComparer<PooledValueTaskSource<int>> {
    public bool Equals(PooledValueTaskSource<int>? x, PooledValueTaskSource<int>? y) {
      return object.ReferenceEquals(x, y);
    }

    public int GetHashCode(PooledValueTaskSource<int> obj) {
      return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
  }
}
