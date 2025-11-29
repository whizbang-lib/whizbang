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
/// Contract tests that all IExecutionStrategy implementations must pass.
/// These tests define the required behavior for execution strategies.
/// </summary>
[Category("Execution")]
public abstract class ExecutionStrategyContractTests {
  /// <summary>
  /// Factory method that derived classes must implement to provide their specific implementation.
  /// </summary>
  protected abstract IExecutionStrategy CreateStrategy();

  /// <summary>
  /// Indicates whether the strategy guarantees strict FIFO ordering.
  /// SerialExecutor: true, ParallelExecutor: false
  /// </summary>
  protected abstract bool GuaranteesOrdering { get; }

  /// <summary>
  /// Helper to create a minimal PolicyContext for testing.
  /// </summary>
  protected static PolicyContext CreateTestContext() {
    return null!; // Execution strategies don't currently use PolicyContext
  }

  /// <summary>
  /// Helper to create a test envelope
  /// </summary>
  protected static IMessageEnvelope CreateTestEnvelope(string payload) {
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(payload),
      Hops = []
    };
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "Test",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });
    return envelope;
  }

  private record TestMessage(string Content);

  [Test]
  public async Task ExecuteAsync_ShouldCallHandlerAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var handlerCalled = false;

    // Act
    await strategy.ExecuteAsync<int>(
      envelope,
      (env, ctx) => {
        handlerCalled = true;
        return ValueTask.FromResult(42);
      },
      context
    );

    // Assert
    await Assert.That(handlerCalled).IsTrue();
    await strategy.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_ShouldReturnHandlerResultAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act
    var result = await strategy.ExecuteAsync<int>(
      envelope,
      (env, ctx) => ValueTask.FromResult(42),
      context
    );

    // Assert
    await Assert.That(result).IsEqualTo(42);
    await strategy.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_ShouldPassEnvelopeToHandlerAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var envelope = CreateTestEnvelope("test-message");
    var context = CreateTestContext();
    IMessageEnvelope? receivedEnvelope = null;

    // Act
    await strategy.ExecuteAsync<int>(
      envelope,
      (env, ctx) => {
        receivedEnvelope = env;
        return ValueTask.FromResult(0);
      },
      context
    );

    // Assert
    await Assert.That(receivedEnvelope).IsNotNull();
    await Assert.That(receivedEnvelope!.MessageId).IsEqualTo(envelope.MessageId);
    await strategy.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_ShouldPropagateHandlerExceptionAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act & Assert
    await Assert.That(async () => {
      await strategy.ExecuteAsync<int>(
        envelope,
        (env, ctx) => throw new InvalidOperationException("Handler error"),
        context
      );
    }).ThrowsExactly<InvalidOperationException>().WithMessage("Handler error");

    await strategy.StopAsync();
  }

  [Test]
  public async Task ExecuteAsync_ShouldPropagateCancellationAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => {
      await strategy.ExecuteAsync<int>(
        envelope,
        async (env, ctx) => {
          await Task.Delay(1000, cts.Token);
          return 0;
        },
        context,
        cts.Token
      );
    }).Throws<OperationCanceledException>(); // Accepts TaskCanceledException (derived type)

    await strategy.StopAsync();
  }

  [Test]
  public async Task StartAsync_ShouldBeIdempotentAsync() {
    // Arrange
    var strategy = CreateStrategy();

    // Act
    await strategy.StartAsync();
    await strategy.StartAsync(); // Second call should not throw

    // Assert - No exception
    await strategy.StopAsync();
  }

  [Test]
  public async Task StopAsync_ShouldPreventNewExecutionsAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    await strategy.StopAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();

    // Act & Assert - Should throw or fail gracefully
    await Assert.That(async () => {
      await strategy.ExecuteAsync<int>(
        envelope,
        (env, ctx) => ValueTask.FromResult(0),
        context
      );
    }).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task DrainAsync_ShouldWaitForPendingWorkAsync() {
    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var envelope = CreateTestEnvelope("test");
    var context = CreateTestContext();
    var handlerStarted = new TaskCompletionSource<bool>();
    var handlerCompleted = new TaskCompletionSource<bool>();

    // Act - Start a long-running handler
    var executionTask = strategy.ExecuteAsync<int>(
      envelope,
      async (env, ctx) => {
        handlerStarted.SetResult(true);
        await handlerCompleted.Task;
        return 0;
      },
      context
    );

    // Wait for handler to start
    await handlerStarted.Task;

    // Call DrainAsync (should wait for handler)
    var drainTask = strategy.DrainAsync();

    // Complete the handler
    handlerCompleted.SetResult(true);
    await executionTask;

    // DrainAsync should now complete
    await drainTask;

    await strategy.StopAsync();
  }

  [Test]
  public async Task Name_ShouldNotBeEmptyAsync() {
    // Arrange
    var strategy = CreateStrategy();

    // Act
    var name = strategy.Name;

    // Assert
    await Assert.That(name).IsNotEmpty();
  }

  // Ordering-specific test (only runs if GuaranteesOrdering is true)
  [Test]
  public async Task ExecuteAsync_ShouldMaintainStrictFifoOrder_WhenOrderingGuaranteedAsync() {
    // Skip for strategies that don't guarantee ordering
    if (!GuaranteesOrdering) {
      // Skip test
      return;
    }

    // Arrange
    var strategy = CreateStrategy();
    await strategy.StartAsync();
    var context = CreateTestContext();
    var executionOrder = new List<int>();
    var lockObj = new object();
    var tasks = new List<Task>();

    // Act - Execute 10 messages concurrently
    for (int i = 0; i < 10; i++) {
      var index = i; // Capture loop variable
      var envelope = CreateTestEnvelope($"message-{index}");
      var task = strategy.ExecuteAsync<int>(
        envelope,
        async (env, ctx) => {
          await Task.Delay(10); // Simulate work
          lock (lockObj) {
            executionOrder.Add(index);
          }
          return index;
        },
        context
      );
      tasks.Add(task.AsTask());
    }

    await Task.WhenAll(tasks);

    // Assert - Order should be preserved (0, 1, 2, ..., 9)
    await Assert.That(executionOrder).HasCount().EqualTo(10);
    for (int i = 0; i < 10; i++) {
      await Assert.That(executionOrder[i]).IsEqualTo(i);
    }

    await strategy.StopAsync();
  }
}
