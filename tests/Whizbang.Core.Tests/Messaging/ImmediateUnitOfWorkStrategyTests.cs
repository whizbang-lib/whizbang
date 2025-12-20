using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

[InheritsTests]

/// <summary>
/// Tests for ImmediateUnitOfWorkStrategy.
/// Extends contract tests and adds immediate strategy-specific behavior tests.
/// Key behavior: Flushes immediately on each QueueMessageAsync call.
/// </summary>
public class ImmediateUnitOfWorkStrategyTests : IUnitOfWorkStrategyContractTests {
  protected override IUnitOfWorkStrategy CreateStrategy() {
    return new ImmediateUnitOfWorkStrategy();
  }

  [Test]
  public async Task QueueMessageAsync_GeneratesUuid7UnitId() {
    // Arrange
    var strategy = CreateStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1);
    // Small delay to ensure time ordering
    await Task.Delay(10);
    var unitId2 = await strategy.QueueMessageAsync(message2);

    // Assert - Uuid7 should be time-ordered
    await Assert.That(unitId2).IsGreaterThan(unitId1);
  }

  [Test]
  public async Task QueueMessageAsync_ImmediatelyTriggersOnFlushRequested() {
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

    // Assert - Callback should be invoked immediately (no delay needed)
    await Assert.That(callbackInvoked).IsTrue();
    await Assert.That(callbackUnitId).IsEqualTo(unitId);
  }

  [Test]
  public async Task QueueMessageAsync_AwaitsCallbackCompletion() {
    // Arrange
    var strategy = CreateStrategy();
    var callbackStarted = false;
    var callbackCompleted = false;

    strategy.OnFlushRequested += async (unitId, ct) => {
      callbackStarted = true;
      await Task.Delay(50); // Simulate async work
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
  public async Task QueueMessageAsync_PassesCorrectUnitIdToCallback() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task GetMessagesForUnit_AfterCallback_ReturnsQueuedMessage() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task QueueMessageAsync_WithoutCallback_ThrowsInvalidOperationException() {
    // Arrange
    var strategy = CreateStrategy();
    var message = new TestMessage { Value = "test" };

    // Act & Assert
    await Assert.That(async () => await strategy.QueueMessageAsync(message))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task QueueMessageAsync_CreatesSeparateUnitPerMessage() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task GetLifecycleStagesForUnit_DuringCallback_ReturnsLifecycleStage() {
    // Arrange
    var strategy = CreateStrategy();
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
  public async Task CancelUnitAsync_AfterFlush_IsNoOp() {
    // Arrange
    var strategy = CreateStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };
    var unitId = await strategy.QueueMessageAsync(message);

    // Act & Assert (should not throw)
    await strategy.CancelUnitAsync(unitId);
  }

  /// <summary>
  /// Test message class.
  /// </summary>
  private class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
