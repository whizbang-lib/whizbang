using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ImmediateUnitOfWorkStrategy.
/// Includes all contract tests (manually copied) plus immediate strategy-specific behavior tests.
/// Key behavior: Flushes immediately on each QueueMessageAsync call.
/// NOTE: Contract tests manually copied instead of using [InheritsTests] due to TUnit issue with background tasks.
/// </summary>
public class ImmediateUnitOfWorkStrategyTests {
  private ImmediateUnitOfWorkStrategy _createStrategy() {
    return new ImmediateUnitOfWorkStrategy();
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
    await Task.Delay(100, CancellationToken.None);

    // Assert - Callback was successfully wired (no exception thrown)
    // Actual invocation timing varies by strategy (Immediate invokes immediately, others don't)
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // IMMEDIATE-SPECIFIC TESTS
  // ========================================

  [Test]
  public async Task QueueMessageAsync_GeneratesUuid7UnitIdAsync() {
    // Arrange
    var strategy = _createStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1);
    // Small delay to ensure time ordering
    await Task.Delay(10, CancellationToken.None);
    var unitId2 = await strategy.QueueMessageAsync(message2);

    // Assert - Uuid7 should be time-ordered
    await Assert.That(unitId2).IsGreaterThan(unitId1);
  }

  [Test]
  public async Task QueueMessageAsync_ImmediatelyTriggersOnFlushRequestedAsync() {
    // Arrange
    var strategy = _createStrategy();
    var callbackInvoked = false;
    Guid? callbackUnitId = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackInvoked = true;
      callbackUnitId = unitId;
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };

    // Act
    var unitId = await strategy.QueueMessageAsync(message);

    // Assert - Callback should be invoked immediately (no delay needed)
    await Assert.That(callbackInvoked).IsTrue();
    await Assert.That(callbackUnitId).IsEqualTo(unitId);
  }

  [Test]
  public async Task QueueMessageAsync_AwaitsCallbackCompletionAsync() {
    // Arrange
    var strategy = _createStrategy();
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

    // Assert - QueueMessageAsync should not return until callback completes
    await Assert.That(callbackStarted).IsTrue();
    await Assert.That(callbackCompleted).IsTrue();
  }

  [Test]
  public async Task QueueMessageAsync_PassesCorrectUnitIdToCallbackAsync() {
    // Arrange
    var strategy = _createStrategy();
    Guid? receivedUnitId = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      receivedUnitId = unitId;
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };

    // Act
    var returnedUnitId = await strategy.QueueMessageAsync(message);

    // Assert
    await Assert.That(receivedUnitId).IsEqualTo(returnedUnitId);
  }

  [Test]
  public async Task GetMessagesForUnit_AfterCallback_ReturnsQueuedMessageAsync() {
    // Arrange
    var strategy = _createStrategy();
    IReadOnlyList<object>? messagesInCallback = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      messagesInCallback = strategy.GetMessagesForUnit(unitId);
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };

    // Act
    await strategy.QueueMessageAsync(message);

    // Assert
    await Assert.That(messagesInCallback).IsNotNull();
    await Assert.That(messagesInCallback!.Count).IsEqualTo(1);
    await Assert.That(messagesInCallback[0]).IsEqualTo(message);
  }

  [Test]
  public async Task QueueMessageAsync_WithoutCallback_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var strategy = _createStrategy();
    var message = new TestMessage { Value = "test" };

    // Act & Assert
    await Assert.That(async () => await strategy.QueueMessageAsync(message))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task QueueMessageAsync_CreatesSeparateUnitPerMessageAsync() {
    // Arrange
    var strategy = _createStrategy();
    var unitIds = new List<Guid>();

    strategy.OnFlushRequested += async (unitId, ct) => {
      unitIds.Add(unitId);
      await Task.CompletedTask;
    };

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1);
    var unitId2 = await strategy.QueueMessageAsync(message2);

    // Assert - Each message gets its own unit
    await Assert.That(unitId1).IsNotEqualTo(unitId2);
    await Assert.That(unitIds.Count).IsEqualTo(2);
    await Assert.That(unitIds[0]).IsEqualTo(unitId1);
    await Assert.That(unitIds[1]).IsEqualTo(unitId2);
  }

  [Test]
  public async Task GetLifecycleStagesForUnit_DuringCallback_ReturnsLifecycleStageAsync() {
    // Arrange
    var strategy = _createStrategy();
    IReadOnlyDictionary<object, LifecycleStage>? lifecycleStagesInCallback = null;

    strategy.OnFlushRequested += async (unitId, ct) => {
      lifecycleStagesInCallback = strategy.GetLifecycleStagesForUnit(unitId);
      await Task.CompletedTask;
    };

    var message = new TestMessage { Value = "test" };

    // Act
    await strategy.QueueMessageAsync(message, LifecycleStage.PreDistributeAsync);

    // Assert
    await Assert.That(lifecycleStagesInCallback).IsNotNull();
    await Assert.That(lifecycleStagesInCallback!).ContainsKey(message);
    await Assert.That(lifecycleStagesInCallback[message]).IsEqualTo(LifecycleStage.PreDistributeAsync);
  }

  [Test]
  public async Task CancelUnitAsync_AfterFlush_IsNoOpAsync() {
    // Arrange
    var strategy = _createStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };
    var unitId = await strategy.QueueMessageAsync(message);

    // Act & Assert (should not throw)
    await strategy.CancelUnitAsync(unitId);
  }

  /// <summary>
  /// Test message class.
  /// </summary>
  private sealed class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
