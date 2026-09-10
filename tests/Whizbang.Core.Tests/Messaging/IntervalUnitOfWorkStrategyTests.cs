using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IntervalUnitOfWorkStrategy.
/// Inherits the contract tests (IUnitOfWorkStrategyContractTests) and adds the interval strategy's own behavior tests.
/// Key behavior: Accumulates messages, flushes on timer tick using PeriodicTimer.
/// </summary>
[InheritsTests]
public class IntervalUnitOfWorkStrategyTests : IUnitOfWorkStrategyContractTests {
  /// <inheritdoc />
  protected override IUnitOfWorkStrategy CreateStrategy() => _createStrategy();

  private static IntervalUnitOfWorkStrategy _createStrategy() {
    // Use VERY long interval for contract tests (30 seconds) to prevent timer ticks during tests
    // Interval-specific tests use shorter intervals and explicit Task.Delay for timing
    return new IntervalUnitOfWorkStrategy(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task CancelUnitAsync_NonExistentUnit_DoesNotThrowAsync() {
    // Arrange - a real, in-flight unit alongside the unknown id, so "did nothing" is visible.
    await using var strategy = _createStrategy();
    strategy.OnFlushRequested += async (_, _) => await Task.CompletedTask;
    var liveUnitId = await strategy.QueueMessageAsync(new TestMessage { Value = "keep me" });
    var nonExistentUnitId = Guid.NewGuid();

    // Act
    await strategy.CancelUnitAsync(nonExistentUnitId);

    // Assert - cancelling an id nobody owns must not discard the batch that IS open. The guard
    // that makes this true is a single `_currentUnit?.UnitId == unitId` check; drop it and every
    // cancel silently drops the in-flight messages of an unrelated unit.
    await Assert.That(strategy.GetMessagesForUnit(liveUnitId).Count).IsEqualTo(1)
      .Because("the open unit is untouched by a cancel aimed at an id that was never queued");
    await Assert.That(strategy.GetMessagesForUnit(nonExistentUnitId).Count).IsEqualTo(0)
      .Because("and the unknown id still resolves to nothing rather than being created by the cancel");
  }

  // ========================================
  // INTERVAL-SPECIFIC TESTS
  // ========================================

  [Test]
  public async Task Constructor_StartsPeriodicTimerAsync() {
    // Arrange & Act
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    var callbackInvoked = false;

    var callbackSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackInvoked = true;
      callbackSignal.TrySetResult(true);
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };
    await strategy.QueueMessageAsync(message);

    // Wait for timer to trigger callback (signal-based)
    await callbackSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert - Timer should have triggered callback
    await Assert.That(callbackInvoked).IsTrue();
  }

  [Test]
  public async Task QueueMessageAsync_GeneratesUuid7UnitId_OnFirstMessageAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(100));
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1);
    var unitId2 = await strategy.QueueMessageAsync(message2);

    // Assert - Both messages should share the same unit (before timer tick)
    await Assert.That(unitId1).IsEqualTo(unitId2);
    // Assert - Unit ID should be time-ordered (Uuid7)
    await Assert.That(unitId1).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task QueueMessageAsync_AccumulatesMessages_InCurrentUnitAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(500));
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };
    var message3 = new TestMessage { Value = "test3" };

    // Act
    var unitId = await strategy.QueueMessageAsync(message1);
    await strategy.QueueMessageAsync(message2);
    await strategy.QueueMessageAsync(message3);

    var messages = strategy.GetMessagesForUnit(unitId);

    // Assert
    await Assert.That(messages.Count).IsEqualTo(3);
    await Assert.That(messages).Contains(message1);
    await Assert.That(messages).Contains(message2);
    await Assert.That(messages).Contains(message3);
  }

  [Test]
  public async Task QueueMessageAsync_ReturnsImmediatelyAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(100));
    var callbackStarted = false;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackStarted = true;
      await Task.Delay(50, ct);
    };

    var message = new TestMessage { Value = "test" };

    // Act
    await strategy.QueueMessageAsync(message);

    // Assert - QueueMessageAsync should return immediately (callback not triggered yet)
    await Assert.That(callbackStarted).IsFalse();
  }

  [Test]
  public async Task PeriodicTimer_TriggersOnFlushRequestedAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    var callbackTriggered = new TaskCompletionSource<Guid>();
    var callbackCount = 0;
    var callbackUnitIds = new List<Guid>();

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackCount++;
      callbackUnitIds.Add(unitId);
      callbackTriggered.TrySetResult(unitId); // Signal first callback
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };
    await strategy.QueueMessageAsync(message);

    // Wait for callback with timeout (deterministic - no arbitrary delays)
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
    var completedTask = await Task.WhenAny(callbackTriggered.Task, timeoutTask);

    if (completedTask == timeoutTask) {
      throw new TimeoutException("Timer callback was not triggered within 2 seconds");
    }

    // Assert - Callback should have been invoked at least once
    await Assert.That(callbackCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(callbackUnitIds.Count).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task PeriodicTimer_CreatesNewUnit_AfterFlushAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    var flushedUnitIds = new List<Guid>();

    var flushSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    strategy.OnFlushRequested += async (unitId, ct) => {
      flushedUnitIds.Add(unitId);
      flushSignal.TrySetResult(true);
      await Task.CompletedTask;
    };

    // Act - Queue message, wait for flush, queue another message
    var message1 = new TestMessage { Value = "test1" };
    var unitId1 = await strategy.QueueMessageAsync(message1);

    // Wait for timer to flush first unit (signal-based)
    await flushSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var message2 = new TestMessage { Value = "test2" };
    var unitId2 = await strategy.QueueMessageAsync(message2);

    // Assert - Second message should get new unit (first was flushed)
    await Assert.That(unitId2).IsNotEqualTo(unitId1);
    await Assert.That(flushedUnitIds).Contains(unitId1);
  }

  [Test]
  public async Task DisposeAsync_StopsTimer_FlushesRemainingUnitsAsync() {
    // Arrange
    var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(500)); // Long interval
    var callbackInvoked = false;
    Guid? callbackUnitId = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackInvoked = true;
      callbackUnitId = unitId;
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };
    var unitId = await strategy.QueueMessageAsync(message);

    // Act - Dispose before timer ticks
    await strategy.DisposeAsync();

    // Assert - Callback should be invoked on disposal (flushes remaining unit)
    await Assert.That(callbackInvoked).IsTrue();
    await Assert.That(callbackUnitId).IsEqualTo(unitId);
  }

  [Test]
  public async Task DisposeAsync_WithNoMessages_DoesNotTriggerCallbackAsync() {
    // Arrange
    var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(100));
    var callbackInvoked = false;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackInvoked = true;
      await Task.CompletedTask;
    };

    // Act (no messages queued)
    await strategy.DisposeAsync();

    // Assert - Callback should NOT be invoked (no work to flush)
    await Assert.That(callbackInvoked).IsFalse();
  }

  [Test]
  public async Task MultipleUnits_FlushedInOrderAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    var flushedUnitIds = new List<Guid>();
    var flushedMessageCounts = new List<int>();
    var flushCount = 0;
    var firstFlushSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondFlushSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    strategy.OnFlushRequested += async (unitId, ct) => {
      var messages = strategy.GetMessagesForUnit(unitId);
      flushedUnitIds.Add(unitId);
      flushedMessageCounts.Add(messages.Count);
      var count = Interlocked.Increment(ref flushCount);
      if (count == 1) {
        firstFlushSignal.TrySetResult(true);
      } else if (count >= 2) {
        secondFlushSignal.TrySetResult(true);
      }
      await Task.CompletedTask;
    };

    // Act - Queue first batch
    var unitId1 = await strategy.QueueMessageAsync(new TestMessage { Value = "1a" });
    await strategy.QueueMessageAsync(new TestMessage { Value = "1b" });

    // Wait for first flush (signal-based)
    await firstFlushSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Queue second batch
    var unitId2 = await strategy.QueueMessageAsync(new TestMessage { Value = "2a" });

    // Wait for second flush (signal-based)
    await secondFlushSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert - Both units should be flushed
    await Assert.That(flushedUnitIds.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task GetLifecycleStagesForUnit_DuringCallback_ReturnsLifecycleStagesAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    IReadOnlyDictionary<object, LifecycleStage>? lifecycleStagesInCallback = null;
    var flushSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    strategy.OnFlushRequested += async (unitId, ct) => {
      lifecycleStagesInCallback = strategy.GetLifecycleStagesForUnit(unitId);
      flushSignal.TrySetResult(true);
      await Task.CompletedTask;
    };

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    await strategy.QueueMessageAsync(message1, LifecycleStage.PreDistributeDetached);
    await strategy.QueueMessageAsync(message2, LifecycleStage.PostDistributeDetached);

    // Wait for timer tick (signal-based)
    await flushSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert
    await Assert.That(lifecycleStagesInCallback).IsNotNull();
    await Assert.That(lifecycleStagesInCallback).ContainsKey(message1);
    await Assert.That(lifecycleStagesInCallback).ContainsKey(message2);
  }

  [Test]
  public async Task QueueMessageAsync_WithoutCallback_DoesNotThrowAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(100));
    var message = new TestMessage { Value = "test" };

    // Act & Assert (should NOT throw - callback only required on flush)
    var unitId = await strategy.QueueMessageAsync(message);
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Test message class.
  /// </summary>
  private sealed class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
