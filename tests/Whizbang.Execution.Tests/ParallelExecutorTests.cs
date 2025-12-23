using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Tests for ParallelExecutor implementation.
/// Inherits contract tests to ensure compliance with IExecutionStrategy requirements.
/// </summary>
[Category("Execution")]
[InheritsTests]
public class ParallelExecutorTests : ExecutionStrategyContractTests {
  /// <summary>
  /// Creates a ParallelExecutor for testing.
  /// </summary>
  protected override IExecutionStrategy CreateStrategy() {
    return new ParallelExecutor(maxConcurrency: 10);
  }

  /// <summary>
  /// ParallelExecutor does NOT guarantee ordering.
  /// </summary>
  protected override bool GuaranteesOrdering => false;

  // ============================================================================
  // ParallelExecutor-Specific Tests
  // ============================================================================

  [Test]
  [Arguments(1, "Parallel(max:1)")]
  [Arguments(5, "Parallel(max:5)")]
  [Arguments(10, "Parallel(max:10)")]
  [Arguments(20, "Parallel(max:20)")]
  [Arguments(100, "Parallel(max:100)")]
  public async Task Constructor_WithValidMaxConcurrency_CreatesExecutorWithCorrectNameAsync(int maxConcurrency, string expectedName) {
    // Arrange & Act
    var executor = new ParallelExecutor(maxConcurrency);

    // Assert
    await Assert.That(executor).IsNotNull();
    await Assert.That(executor.Name).IsEqualTo(expectedName);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  [Arguments(-10)]
  public async Task Constructor_WithInvalidMaxConcurrency_ThrowsArgumentOutOfRangeExceptionAsync(int maxConcurrency) {
    // Arrange, Act & Assert
    await Assert.That(() => new ParallelExecutor(maxConcurrency))
      .Throws<ArgumentOutOfRangeException>();
  }

  public enum ExecutorState {
    NotStarted,
    Stopped
  }

  [Test]
  [Arguments(ExecutorState.NotStarted, "before StartAsync is called")]
  [Arguments(ExecutorState.Stopped, "after StopAsync is called")]
  public async Task ExecuteAsync_WhenNotRunning_ThrowsInvalidOperationExceptionAsync(ExecutorState state, string description) {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 5);

    // Set executor to the specified state
    if (state == ExecutorState.Stopped) {
      await executor.StartAsync();
      await executor.StopAsync();
    }

    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act & Assert
    await Assert.That(async () => await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(42),
      context
    )).Throws<InvalidOperationException>();
  }

  public static IEnumerable<Func<(Func<ParallelExecutor, Task> operation, string description, bool shouldSucceed)>> GetIdempotentOperations() {
    yield return () => (
      async executor => {
        await executor.StartAsync();
        await executor.StartAsync(); // Idempotent call
      },
      "StartAsync called twice",
      true
    );

    yield return () => (
      async executor => {
        await executor.StartAsync();
        await executor.StopAsync();
        await executor.StopAsync(); // Idempotent call
      },
      "StopAsync called twice",
      true
    );

    yield return () => (
      async executor => {
        await executor.StopAsync(); // Stop before start
      },
      "StopAsync before StartAsync",
      true
    );
  }

  [Test]
  [MethodDataSource(nameof(GetIdempotentOperations))]
  public async Task StateTransitions_IdempotentOperations_SucceedAsync(
      Func<ParallelExecutor, Task> operation,
      string description,
      bool shouldSucceed) {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 5);

    // Act & Assert
    if (shouldSucceed) {
      await operation(executor);
      await Assert.That(executor.Name).IsEqualTo("Parallel(max:5)");
    }
  }

  [Test]
  public async Task StartAsync_AfterStop_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 5);
    await executor.StartAsync();
    await executor.StopAsync();

    // Act & Assert
    await Assert.That(() => executor.StartAsync())
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ExecuteAsync_FastPath_SynchronousHandler_ExecutesImmediatelyAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 5);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var handlerCalled = false;

    // Act
    var result = await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => {
        handlerCalled = true;
        return ValueTask.FromResult(42);
      },
      context
    );

    // Assert
    await Assert.That(handlerCalled).IsTrue();
    await Assert.That(result).IsEqualTo(42);
    await executor.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_SlowPath_AsyncHandler_AwaitsCorrectlyAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 5);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var tcs = new TaskCompletionSource<int>();

    // Act
    var executeTask = executor.ExecuteAsync<int>(
      envelope,
      async (env, ctx) => {
        await tcs.Task;
        return 42;
      },
      context
    ).AsTask();

    // Complete the async operation
    tcs.SetResult(42);
    var result = await executeTask;

    // Assert
    await Assert.That(result).IsEqualTo(42);
    await executor.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_ExceptionInHandler_ReleasesSemaphoreAndRethrowsAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 1);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act & Assert
    await Assert.That(async () => await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => throw new InvalidOperationException("Test exception"),
      context
    )).Throws<InvalidOperationException>();

    // Verify semaphore was released by executing another operation
    var result = await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(99),
      context
    );
    await Assert.That(result).IsEqualTo(99);

    await executor.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_RespectsConcurrencyLimit_OnlyMaxConcurrentExecutionsAsync() {
    // Arrange
    var maxConcurrency = 3;
    var executor = new ParallelExecutor(maxConcurrency);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var concurrentCount = 0;
    var maxObservedConcurrency = 0;
    var tasks = new List<Task<int>>();
    var tcs = new TaskCompletionSource<bool>();

    // Act - Start 10 operations that wait
    for (int i = 0; i < 10; i++) {
      var task = executor.ExecuteAsync<int>(
        envelope,
        async (env, ctx) => {
          var current = Interlocked.Increment(ref concurrentCount);

          // Track max concurrency
          int observed;
          int newMax;
          do {
            observed = maxObservedConcurrency;
            newMax = Math.Max(observed, current);
          } while (Interlocked.CompareExchange(ref maxObservedConcurrency, newMax, observed) != observed);

          await tcs.Task; // Wait for signal

          Interlocked.Decrement(ref concurrentCount);
          return current;
        },
        context
      ).AsTask();
      tasks.Add(task);
    }

    // Give tasks time to start and hit semaphore limit
    await Task.Delay(100);

    // Assert - Should have exactly maxConcurrency running
    await Assert.That(concurrentCount).IsEqualTo(maxConcurrency);

    // Complete all tasks
    tcs.SetResult(true);
    await Task.WhenAll(tasks);

    // Assert - Max observed should not exceed limit
    await Assert.That(maxObservedConcurrency).IsLessThanOrEqualTo(maxConcurrency);

    await executor.StopAsync();
  }

  [Test]
  public async Task DrainAsync_WaitsForAllInFlightWork_CompletesAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 3);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var tcs1 = new TaskCompletionSource<int>();
    var tcs2 = new TaskCompletionSource<int>();
    var tcs3 = new TaskCompletionSource<int>();

    // Act - Start 3 async operations
    var task1 = executor.ExecuteAsync<int>(envelope, async (env, ctx) => await tcs1.Task, context).AsTask();
    var task2 = executor.ExecuteAsync<int>(envelope, async (env, ctx) => await tcs2.Task, context).AsTask();
    var task3 = executor.ExecuteAsync<int>(envelope, async (env, ctx) => await tcs3.Task, context).AsTask();

    await Task.Delay(50); // Let tasks start

    // Start drain (should wait for all 3 tasks)
    var drainTask = executor.DrainAsync();

    // Drain should not complete yet
    await Task.Delay(50);
    await Assert.That(drainTask.IsCompleted).IsFalse();

    // Complete first task
    tcs1.SetResult(1);
    await task1;
    await Task.Delay(50);
    await Assert.That(drainTask.IsCompleted).IsFalse(); // Still waiting

    // Complete second task
    tcs2.SetResult(2);
    await task2;
    await Task.Delay(50);
    await Assert.That(drainTask.IsCompleted).IsFalse(); // Still waiting

    // Complete third task
    tcs3.SetResult(3);
    await task3;

    // Now drain should complete
    await drainTask;
    await Assert.That(drainTask.IsCompleted).IsTrue();

    await executor.StopAsync();
  }

  [Test]
  public async Task DrainAsync_WhenNotRunning_ReturnsImmediatelyAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 5);

    // Act & Assert - Should complete immediately without throwing
    await executor.DrainAsync();
  }

  [Test]
  public async Task ExecuteAsync_ParallelExecution_RunsConcurrentlyAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 10);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var executionTimes = new ConcurrentBag<DateTimeOffset>();
    var tasks = new List<Task<int>>();

    // Act - Start 5 operations simultaneously
    for (int i = 0; i < 5; i++) {
      var task = executor.ExecuteAsync<int>(
        envelope,
        async (env, ctx) => {
          executionTimes.Add(DateTimeOffset.UtcNow);
          await Task.Delay(10); // Small delay
          return 42;
        },
        context
      ).AsTask();
      tasks.Add(task);
    }

    await Task.WhenAll(tasks);

    // Assert - All tasks should have started within a short time window (< 50ms)
    var times = executionTimes.OrderBy(t => t).ToList();
    var timeSpan = times.Last() - times.First();
    await Assert.That(timeSpan.TotalMilliseconds).IsLessThan(50);

    await executor.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_CancellationToken_CancelsSemaphoreWaitAsync() {
    // Arrange
    var executor = new ParallelExecutor(maxConcurrency: 1);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var cts = new CancellationTokenSource();

    var tcs = new TaskCompletionSource<int>();

    // Act - Fill the semaphore
    var blockingTask = executor.ExecuteAsync<int>(
      envelope,
      async (env, ctx) => await tcs.Task,
      context
    ).AsTask();

    // Try to execute with cancellation token
    cts.Cancel();

    // Assert - Should throw OperationCanceledException
    await Assert.That(async () => await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(99),
      context,
      cts.Token
    )).Throws<OperationCanceledException>();

    // Cleanup
    tcs.SetResult(42);
    await blockingTask;
    await executor.StopAsync();
  }
}
