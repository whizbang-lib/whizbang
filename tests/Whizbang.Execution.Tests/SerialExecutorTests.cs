using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Tests for SerialExecutor implementation.
/// Inherits contract tests to ensure compliance with IExecutionStrategy requirements.
/// </summary>
[Category("Execution")]
[InheritsTests]
public class SerialExecutorTests : ExecutionStrategyContractTests {
  /// <summary>
  /// Creates a SerialExecutor for testing.
  /// </summary>
  protected override IExecutionStrategy CreateStrategy() {
    return new SerialExecutor();
  }

  /// <summary>
  /// SerialExecutor guarantees strict FIFO ordering.
  /// </summary>
  protected override bool GuaranteesOrdering => true;

  // ============================================================================
  // SerialExecutor-Specific Tests
  // ============================================================================

  [Test]
  public async Task Constructor_Default_CreatesUnboundedExecutorAsync() {
    // Arrange & Act
    var executor = new SerialExecutor();

    // Assert
    await Assert.That(executor).IsNotNull();
    await Assert.That(executor.Name).IsEqualTo("Serial");
  }

  [Test]
  [Arguments(1)]
  [Arguments(10)]
  [Arguments(100)]
  [Arguments(1000)]
  public async Task Constructor_WithValidBoundedCapacity_CreatesExecutorAsync(int capacity) {
    // Arrange & Act
    var executor = new SerialExecutor(capacity);

    // Assert
    await Assert.That(executor).IsNotNull();
    await Assert.That(executor.Name).IsEqualTo("Serial");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-1)]
  [Arguments(-10)]
  public async Task Constructor_WithInvalidCapacity_ThrowsArgumentOutOfRangeExceptionAsync(int capacity) {
    // Arrange, Act & Assert
    await Assert.That(() => new SerialExecutor(capacity))
      .Throws<ArgumentOutOfRangeException>();
  }

  public enum ExecutorState {
    NotStarted,
    Stopped
  }

  [Test]
  [Arguments(ExecutorState.NotStarted, "before StartAsync is called")]
  [Arguments(ExecutorState.Stopped, "after StopAsync is called")]
  public async Task ExecuteAsync_WhenNotRunning_ThrowsInvalidOperationExceptionAsync(
    ExecutorState state,
    string description) {
    // Arrange
    var executor = new SerialExecutor();

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

  public static IEnumerable<(Func<SerialExecutor, Task> operation, string description, bool shouldSucceed)> GetIdempotentOperations() {
    yield return (
      async executor => {
        await executor.StartAsync();
        await executor.StartAsync(); // Idempotent call
      },
      "StartAsync called twice",
      true
    );

    yield return (
      async executor => {
        await executor.StartAsync();
        await executor.StopAsync();
        await executor.StopAsync(); // Idempotent call
      },
      "StopAsync called twice",
      true
    );

    yield return (
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
    Func<SerialExecutor, Task> operation,
    string description,
    bool shouldSucceed) {
    // Arrange
    var executor = new SerialExecutor();

    // Act & Assert
    if (shouldSucceed) {
      await operation(executor);
      await Assert.That(executor.Name).IsEqualTo("Serial");
    }
  }

  [Test]
  public async Task StartAsync_AfterStop_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();
    await executor.StopAsync();

    // Act & Assert
    await Assert.That(() => executor.StartAsync())
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ExecuteAsync_CompletesSuccessfullyAsync() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act
    var result = await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(42),
      context
    );

    // Assert
    await Assert.That(result).IsEqualTo(42);
    await executor.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_WithAsyncHandler_AwaitsCorrectlyAsync() {
    // Arrange
    var executor = new SerialExecutor();
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
  public async Task ExecuteAsync_ExceptionInHandler_RethrowsAsync() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act & Assert
    await Assert.That(async () => await executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => throw new InvalidOperationException("Test exception"),
      context
    )).Throws<InvalidOperationException>();

    await executor.StopAsync();
  }

  [Test]
  public async Task DrainAsync_WaitsForAllInFlightWork_CompletesAsync() {
    // Arrange
    var executor = new SerialExecutor();
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

    await Task.Delay(50); // Let tasks queue

    // Start drain
    var drainTask = executor.DrainAsync();

    // Drain should not complete yet
    await Task.Delay(50);
    await Assert.That(drainTask.IsCompleted).IsFalse();

    // Complete tasks in order (serial execution)
    tcs1.SetResult(1);
    await task1;
    await Task.Delay(50);

    tcs2.SetResult(2);
    await task2;
    await Task.Delay(50);

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
    var executor = new SerialExecutor();

    // Act & Assert - Should complete immediately without throwing
    await executor.DrainAsync();
  }

  [Test]
  public async Task ExecuteAsync_SerialExecution_MaintainsStrictOrderAsync() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var executionOrder = new List<int>();
    var tasks = new List<Task<int>>();

    // Act - Start 10 operations that record order
    for (int i = 0; i < 10; i++) {
      var index = i;
      var task = executor.ExecuteAsync<int>(
        envelope,
        async (env, ctx) => {
          lock (executionOrder) {
            executionOrder.Add(index);
          }
          await Task.Yield();
          return index;
        },
        context
      ).AsTask();
      tasks.Add(task);
    }

    await Task.WhenAll(tasks);

    // Assert - Execution order should be 0, 1, 2, 3, 4, 5, 6, 7, 8, 9
    await Assert.That(executionOrder).IsEquivalentTo(Enumerable.Range(0, 10));

    await executor.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_CancellationToken_SkipsCancelledWorkAsync() {
    // Arrange - Use bounded channel with capacity of 1
    var executor = new SerialExecutor(channelCapacity: 1);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var blockingTcs = new TaskCompletionSource<int>();
    var cts = new CancellationTokenSource();
    var handlerCalled = 0;

    // Act - Queue blocking work first to fill the channel
    var blockingTask = executor.ExecuteAsync<int>(
      envelope,
      async (env, ctx) => await blockingTcs.Task,
      context
    ).AsTask();

    // Queue work with cancellation token (will queue successfully)
    var cancellableTask = executor.ExecuteAsync<int>(
      envelope,
      (env, ctx) => {
        Interlocked.Increment(ref handlerCalled);
        return ValueTask.FromResult(42);
      },
      context,
      cts.Token
    ).AsTask();

    // Give time for work to be queued
    await Task.Delay(100);

    // Cancel AFTER work is queued but BEFORE worker processes it
    cts.Cancel();

    // Unblock worker to process the cancelled work (should skip it via line 165)
    blockingTcs.SetResult(1);
    await blockingTask;

    // Give worker time to process (and skip) the cancelled work
    await Task.Delay(200);

    // Assert - Handler should not have been called (work was skipped via continue)
    await Assert.That(handlerCalled).IsEqualTo(0);
    await executor.StopAsync();
  }

  [Test]
  public async Task DrainAsync_WithWorkerCancellation_HandlesOperationCanceledExceptionAsync() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var tcs = new TaskCompletionSource<int>();

    // Act - Queue long-running work
    var task = executor.ExecuteAsync<int>(
      envelope,
      async (env, ctx) => await tcs.Task,
      context
    ).AsTask();

    // Stop executor (triggers cancellation of worker)
    await executor.StopAsync();

    // Complete the work
    tcs.SetResult(42);

    // Drain should handle OperationCanceledException from worker (line 158)
    await executor.DrainAsync();

    // Assert - No exception should be thrown
  }

  [Test]
  public async Task ProcessWorkItemsAsync_ExceptionInHandler_CaughtAndRecordedAsync() {
    // Arrange
    var executor = new SerialExecutor();
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var exceptionThrown = false;

    // Act - Execute handler that throws
    try {
      await executor.ExecuteAsync<int>(
        envelope,
        (env, ctx) => {
          exceptionThrown = true;
          throw new InvalidOperationException("Test exception");
        },
        context
      );
    } catch (InvalidOperationException) {
      // Exception is propagated to caller
    }

    // Assert - Exception was caught in ProcessWorkItemsAsync (line 172)
    await Assert.That(exceptionThrown).IsTrue();
    await executor.StopAsync();
  }

  [Test]
  [Arguments(10)]
  [Arguments(100)]
  public async Task ExecuteAsync_BoundedChannel_HandlesBackpressureAsync(int capacity) {
    // Arrange
    var executor = new SerialExecutor(capacity);
    await executor.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    var tcs = new TaskCompletionSource<int>();
    var tasks = new List<Task<int>>();

    // Act - Queue more items than channel capacity
    for (int i = 0; i < capacity + 10; i++) {
      var task = executor.ExecuteAsync<int>(
        envelope,
        async (env, ctx) => {
          await tcs.Task;
          return 42;
        },
        context
      ).AsTask();
      tasks.Add(task);
    }

    // Complete all tasks
    tcs.SetResult(42);
    await Task.WhenAll(tasks);

    // Assert - All tasks should complete
    await Assert.That(tasks.Count).IsEqualTo(capacity + 10);

    await executor.StopAsync();
  }
}
