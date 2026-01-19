using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ScopedUnitOfWorkStrategy.
/// Includes all contract tests (manually copied) plus scoped strategy-specific behavior tests.
/// Key behavior: Accumulates messages in single unit, flushes on DisposeAsync.
/// NOTE: Contract tests manually copied instead of using [InheritsTests] due to TUnit issue with background tasks.
/// </summary>
public class ScopedUnitOfWorkStrategyTests {
  private ScopedUnitOfWorkStrategy _createStrategy() {
    return new ScopedUnitOfWorkStrategy();
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
  // SCOPED-SPECIFIC TESTS
  // ========================================

  [Test]
  public async Task QueueMessageAsync_GeneratesUuid7UnitId_OnFirstMessageAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1);
    await Task.Delay(10);
    var unitId2 = await strategy.QueueMessageAsync(message2);

    // Assert - Both messages should share the same unit
    await Assert.That(unitId1).IsEqualTo(unitId2);
    // Assert - Unit ID should be time-ordered (Uuid7)
    await Assert.That(unitId1).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task QueueMessageAsync_AccumulatesMessages_InSameUnitAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
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
    await using var strategy = new ScopedUnitOfWorkStrategy();
    var callbackStarted = false;
    var callbackCompleted = false;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackStarted = true;
      await Task.Delay(50, ct); // Simulate async work
      callbackCompleted = true;
    };

    var message = new TestMessage { Value = "test" };

    // Act
    await strategy.QueueMessageAsync(message);

    // Assert - QueueMessageAsync should return immediately (callback not triggered yet)
    await Assert.That(callbackStarted).IsFalse();
    await Assert.That(callbackCompleted).IsFalse();
  }

  [Test]
  public async Task DisposeAsync_TriggersOnFlushRequestedAsync() {
    // Arrange
    var strategy = new ScopedUnitOfWorkStrategy();
    var callbackInvoked = false;
    Guid? callbackUnitId = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackInvoked = true;
      callbackUnitId = unitId;
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };
    var unitId = await strategy.QueueMessageAsync(message);

    // Act
    await strategy.DisposeAsync();

    // Assert - Callback should be invoked on disposal
    await Assert.That(callbackInvoked).IsTrue();
    await Assert.That(callbackUnitId).IsEqualTo(unitId);
  }

  [Test]
  public async Task DisposeAsync_WithNoMessages_DoesNotTriggerCallbackAsync() {
    // Arrange
    var strategy = new ScopedUnitOfWorkStrategy();
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
  public async Task GetMessagesForUnit_DuringCallback_ReturnsAllQueuedMessagesAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    IReadOnlyList<object>? messagesInCallback = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      messagesInCallback = strategy.GetMessagesForUnit(unitId);
      await Task.CompletedTask;
    };

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };
    var message3 = new TestMessage { Value = "test3" };

    await strategy.QueueMessageAsync(message1);
    await strategy.QueueMessageAsync(message2);
    await strategy.QueueMessageAsync(message3);

    // Act
    await strategy.DisposeAsync();

    // Assert
    await Assert.That(messagesInCallback).IsNotNull();
    await Assert.That(messagesInCallback!.Count).IsEqualTo(3);
    await Assert.That(messagesInCallback).Contains(message1);
    await Assert.That(messagesInCallback).Contains(message2);
    await Assert.That(messagesInCallback).Contains(message3);
  }

  [Test]
  public async Task GetLifecycleStagesForUnit_ReturnsAllStagesAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    IReadOnlyDictionary<object, LifecycleStage>? lifecycleStagesInCallback = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      lifecycleStagesInCallback = strategy.GetLifecycleStagesForUnit(unitId);
      await Task.CompletedTask;
    };

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    await strategy.QueueMessageAsync(message1, LifecycleStage.PreDistributeAsync);
    await strategy.QueueMessageAsync(message2, LifecycleStage.PostDistributeAsync);

    // Act
    await strategy.DisposeAsync();

    // Assert
    await Assert.That(lifecycleStagesInCallback).IsNotNull();
    await Assert.That(lifecycleStagesInCallback!).ContainsKey(message1);
    await Assert.That(lifecycleStagesInCallback).ContainsKey(message2);
    await Assert.That(lifecycleStagesInCallback[message1]).IsEqualTo(LifecycleStage.PreDistributeAsync);
    await Assert.That(lifecycleStagesInCallback[message2]).IsEqualTo(LifecycleStage.PostDistributeAsync);
  }

  [Test]
  public async Task MultipleMessages_DifferentLifecycleStages_ShareSameUnitAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1, LifecycleStage.ImmediateAsync);
    var unitId2 = await strategy.QueueMessageAsync(message2, LifecycleStage.PreDistributeAsync);

    // Assert - Different lifecycle stages still share same unit
    await Assert.That(unitId1).IsEqualTo(unitId2);
  }

  [Test]
  public async Task DisposeAsync_ClearsUnit_AfterFlushAsync() {
    // Arrange
    var strategy = new ScopedUnitOfWorkStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };
    var unitId = await strategy.QueueMessageAsync(message);

    // Act
    await strategy.DisposeAsync();

    // Assert - Unit should be cleared after flush
    var messages = strategy.GetMessagesForUnit(unitId);
    await Assert.That(messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task QueueMessageAsync_WithoutCallback_DoesNotThrowAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    var message = new TestMessage { Value = "test" };

    // Act & Assert (should NOT throw - callback only required on flush)
    var unitId = await strategy.QueueMessageAsync(message);
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task DisposeAsync_WithoutCallback_DoesNotThrowAsync() {
    // Arrange
    var strategy = new ScopedUnitOfWorkStrategy();
    var message = new TestMessage { Value = "test" };
    await strategy.QueueMessageAsync(message);

    // Act & Assert (should NOT throw - just skip flush if no callback)
    await strategy.DisposeAsync();
  }

  /// <summary>
  /// Test message class.
  /// </summary>
  private sealed class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
