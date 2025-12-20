using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

[InheritsTests]

/// <summary>
/// Tests for ScopedUnitOfWorkStrategy.
/// Extends contract tests and adds scoped strategy-specific behavior tests.
/// Key behavior: Accumulates messages in single unit, flushes on DisposeAsync.
/// </summary>
public class ScopedUnitOfWorkStrategyTests : IUnitOfWorkStrategyContractTests {
  protected override IUnitOfWorkStrategy CreateStrategy() {
    return new ScopedUnitOfWorkStrategy();
  }

  [Test]
  public async Task QueueMessageAsync_GeneratesUuid7UnitId_OnFirstMessage() {
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
  public async Task QueueMessageAsync_AccumulatesMessages_InSameUnit() {
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
  public async Task QueueMessageAsync_ReturnsImmediately() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
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

    // Assert - QueueMessageAsync should return immediately (callback not triggered yet)
    await Assert.That(callbackStarted).IsFalse();
    await Assert.That(callbackCompleted).IsFalse();
  }

  [Test]
  public async Task DisposeAsync_TriggersOnFlushRequested() {
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
  public async Task DisposeAsync_WithNoMessages_DoesNotTriggerCallback() {
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
  public async Task GetMessagesForUnit_DuringCallback_ReturnsAllQueuedMessages() {
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
  public async Task GetLifecycleStagesForUnit_ReturnsAllStages() {
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
  public async Task MultipleMessages_DifferentLifecycleStages_ShareSameUnit() {
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
  public async Task DisposeAsync_ClearsUnit_AfterFlush() {
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
  public async Task QueueMessageAsync_WithoutCallback_DoesNotThrow() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    var message = new TestMessage { Value = "test" };

    // Act & Assert (should NOT throw - callback only required on flush)
    var unitId = await strategy.QueueMessageAsync(message);
    await Assert.That(unitId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task DisposeAsync_WithoutCallback_DoesNotThrow() {
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
  private class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
