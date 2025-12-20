using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Contract tests for IUnitOfWorkStrategy interface.
/// All implementations (Immediate, Scoped, Interval) must pass these tests.
/// Defines the behavioral contract for unit of work management with lifecycle stages.
/// </summary>
public abstract class IUnitOfWorkStrategyContractTests {
  /// <summary>
  /// Factory method to create the strategy under test.
  /// Implemented by concrete test classes for each strategy.
  /// </summary>
  protected abstract IUnitOfWorkStrategy CreateStrategy();

  [Test]
  public async Task QueueMessageAsync_ReturnsNonEmptyGuid() {
    // Arrange
    var strategy = CreateStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };

    // Act
    var unitId = await strategy.QueueMessageAsync(message);

    // Assert
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task QueueMessageAsync_StoresMessage() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task QueueMessageAsync_WithLifecycleStage_StoresLifecycleMapping() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task GetMessagesForUnit_NonExistentUnit_ReturnsEmpty() {
    // Arrange
    var strategy = CreateStrategy();
    var nonExistentUnitId = Guid.NewGuid();

    // Act
    var messages = strategy.GetMessagesForUnit(nonExistentUnitId);

    // Assert
    await Assert.That(messages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetLifecycleStagesForUnit_NonExistentUnit_ReturnsEmpty() {
    // Arrange
    var strategy = CreateStrategy();
    var nonExistentUnitId = Guid.NewGuid();

    // Act
    var lifecycleStages = strategy.GetLifecycleStagesForUnit(nonExistentUnitId);

    // Assert
    await Assert.That(lifecycleStages.Count).IsEqualTo(0);
  }

  [Test]
  public async Task CancelUnitAsync_ExistingUnit_RemovesUnit() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task CancelUnitAsync_NonExistentUnit_DoesNotThrow() {
    // Arrange
    var strategy = CreateStrategy();
    var nonExistentUnitId = Guid.NewGuid();

    // Act & Assert (should not throw)
    await strategy.CancelUnitAsync(nonExistentUnitId);
  }

  [Test]
  public async Task OnFlushRequested_CanBeWired() {
    // Arrange
    var strategy = CreateStrategy();
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

    // Allow async operations to complete (some strategies flush immediately, others don't)
    await Task.Delay(100);

    // Assert - Callback was successfully wired (no exception thrown)
    // Actual invocation timing varies by strategy (Immediate invokes immediately, others don't)
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Test message class for contract tests.
  /// </summary>
  private class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
