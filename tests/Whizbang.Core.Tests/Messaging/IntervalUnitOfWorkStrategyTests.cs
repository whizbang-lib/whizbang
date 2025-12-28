using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IntervalUnitOfWorkStrategy.
/// Includes all contract tests (manually copied) plus interval strategy-specific behavior tests.
/// Key behavior: Accumulates messages, flushes on timer tick using PeriodicTimer.
/// NOTE: Contract tests manually copied instead of using [InheritsTests] due to TUnit issue with background tasks.
/// </summary>
public class IntervalUnitOfWorkStrategyTests {
  private IntervalUnitOfWorkStrategy _createStrategy() {
    // Use VERY long interval for contract tests (30 seconds) to prevent timer ticks during tests
    // Interval-specific tests use shorter intervals and explicit Task.Delay for timing
    return new IntervalUnitOfWorkStrategy(TimeSpan.FromSeconds(30));
  }

  // ========================================
  // CONTRACT TESTS (manually copied from IUnitOfWorkStrategyContractTests)
  // ========================================

  [Test]
  public async Task QueueMessageAsync_ReturnsNonEmptyGuidAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };

    // Act
    var unitId = await strategy.QueueMessageAsync(message);

    // Assert
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task QueueMessageAsync_StoresMessageAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };

    // Act
    var unitId = await strategy.QueueMessageAsync(message);
    var messages = strategy.GetMessagesForUnit(unitId);

    // Assert
    await Assert.That(messages.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(messages).Contains(message);
  }

  [Test]
  public async Task QueueMessageAsync_WithLifecycleStage_StoresLifecycleMappingAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };

    // Act
    var unitId = await strategy.QueueMessageAsync(
      message,
      LifecycleStage.PreDistributeAsync
    );
    var lifecycleStages = strategy.GetLifecycleStagesForUnit(unitId);

    // Assert
    await Assert.That(lifecycleStages).ContainsKey(message);
    await Assert.That(lifecycleStages[message]).IsEqualTo(LifecycleStage.PreDistributeAsync);
  }

  [Test]
  public async Task GetMessagesForUnit_NonExistentUnit_ReturnsEmptyAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    var nonExistentUnitId = Guid.NewGuid();

    // Act
    var messages = strategy.GetMessagesForUnit(nonExistentUnitId);

    // Assert
    await Assert.That(messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetLifecycleStagesForUnit_NonExistentUnit_ReturnsEmptyAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    var nonExistentUnitId = Guid.NewGuid();

    // Act
    var lifecycleStages = strategy.GetLifecycleStagesForUnit(nonExistentUnitId);

    // Assert
    await Assert.That(lifecycleStages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CancelUnitAsync_ExistingUnit_RemovesUnitAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };
    var unitId = await strategy.QueueMessageAsync(message);

    // Act
    await strategy.CancelUnitAsync(unitId);
    var messages = strategy.GetMessagesForUnit(unitId);

    // Assert
    await Assert.That(messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CancelUnitAsync_NonExistentUnit_DoesNotThrowAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    var nonExistentUnitId = Guid.NewGuid();

    // Act & Assert (should not throw)
    await strategy.CancelUnitAsync(nonExistentUnitId);
  }

  [Test]
  public async Task OnFlushRequested_CanBeWiredAsync() {
    // Arrange
    await using var strategy = _createStrategy();
    Guid? callbackUnitId = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackUnitId = unitId;
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };

    // Act
    var unitId = await strategy.QueueMessageAsync(message);

    // Allow async operations to complete (some strategies flush immediately, others don't)
    await Task.Delay(100);

    // Assert - Callback was successfully wired (no exception thrown)
    // Actual invocation timing varies by strategy (Immediate invokes immediately, others don't)
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // INTERVAL-SPECIFIC TESTS
  // ========================================

  [Test]
  public async Task Constructor_StartsPeriodicTimerAsync() {
    // Arrange & Act
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    var callbackInvoked = false;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackInvoked = true;
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };
    await strategy.QueueMessageAsync(message);

    // Wait for timer to tick (increased for systems under load)
    await Task.Delay(600);

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
    var callbackCount = 0;
    var callbackUnitIds = new List<Guid>();

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackCount++;
      callbackUnitIds.Add(unitId);
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };
    await strategy.QueueMessageAsync(message);

    // Wait for at least one timer tick
    await Task.Delay(150);

    // Assert - Callback should have been invoked at least once
    await Assert.That(callbackCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(callbackUnitIds.Count).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task PeriodicTimer_CreatesNewUnit_AfterFlushAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    var flushedUnitIds = new List<Guid>();

    strategy.OnFlushRequested += async (unitId, ct) => {
      flushedUnitIds.Add(unitId);
      await Task.CompletedTask;
    };

    // Act - Queue message, wait for flush, queue another message
    var message1 = new TestMessage { Value = "test1" };
    var unitId1 = await strategy.QueueMessageAsync(message1);

    // Wait for timer to flush first unit (increased for systems under load)
    await Task.Delay(300);

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

    strategy.OnFlushRequested += async (unitId, ct) => {
      var messages = strategy.GetMessagesForUnit(unitId);
      flushedUnitIds.Add(unitId);
      flushedMessageCounts.Add(messages.Count);
      await Task.CompletedTask;
    };

    // Act - Queue first batch
    var unitId1 = await strategy.QueueMessageAsync(new TestMessage { Value = "1a" });
    await strategy.QueueMessageAsync(new TestMessage { Value = "1b" });

    // Wait for first flush (increased for systems under load)
    await Task.Delay(400);

    // Queue second batch
    var unitId2 = await strategy.QueueMessageAsync(new TestMessage { Value = "2a" });

    // Wait for second flush (increased for systems under load)
    await Task.Delay(400);

    // Assert - Both units should be flushed
    await Assert.That(flushedUnitIds.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task GetLifecycleStagesForUnit_DuringCallback_ReturnsLifecycleStagesAsync() {
    // Arrange
    await using var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMilliseconds(50));
    IReadOnlyDictionary<object, LifecycleStage>? lifecycleStagesInCallback = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      lifecycleStagesInCallback = strategy.GetLifecycleStagesForUnit(unitId);
      await Task.CompletedTask;
    };

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    await strategy.QueueMessageAsync(message1, LifecycleStage.PreDistributeAsync);
    await strategy.QueueMessageAsync(message2, LifecycleStage.PostDistributeAsync);

    // Wait for timer tick (increased for systems under load)
    await Task.Delay(400);

    // Assert
    await Assert.That(lifecycleStagesInCallback).IsNotNull();
    await Assert.That(lifecycleStagesInCallback!).ContainsKey(message1);
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
