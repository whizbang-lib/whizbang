using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ScopedUnitOfWorkStrategy.
/// Inherits the contract tests (IUnitOfWorkStrategyContractTests) and adds the scoped strategy's own behavior tests.
/// Key behavior: Accumulates messages in single unit, flushes on DisposeAsync.
/// </summary>
[InheritsTests]
public class ScopedUnitOfWorkStrategyTests : IUnitOfWorkStrategyContractTests {
  /// <inheritdoc />
  protected override IUnitOfWorkStrategy CreateStrategy() => _createStrategy();

  private static ScopedUnitOfWorkStrategy _createStrategy() {
    return new ScopedUnitOfWorkStrategy();
  }

  [Test]
  public async Task CancelUnitAsync_NonExistentUnit_DoesNotThrowAsync() {
    // Arrange - a LIVE unit sits alongside the id being canceled. Cancel matches on unit id, so
    // the id that does not match is the one that proves the match is actually consulted.
    await using var strategy = _createStrategy();
    strategy.OnFlushRequested += async (_, _) => await Task.CompletedTask;

    var message = new TestMessage { Value = "test" };
    var liveUnitId = await strategy.QueueMessageAsync(message);
    var nonExistentUnitId = Guid.NewGuid();

    // Act
    await strategy.CancelUnitAsync(nonExistentUnitId);

    // Assert - not throwing is the easy half. The scope holds exactly one unit, so a cancel that
    // skipped the id comparison would clear it for a stranger's unit id: every message queued in
    // this scope silently discarded, with the caller told nothing and nothing left to flush.
    var messages = strategy.GetMessagesForUnit(liveUnitId);
    await Assert.That(messages.Count).IsEqualTo(1)
      .Because("canceling an id this scope never issued must not discard the work it did issue");
    await Assert.That(messages).Contains(message);
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

    await strategy.QueueMessageAsync(message1, LifecycleStage.PreDistributeDetached);
    await strategy.QueueMessageAsync(message2, LifecycleStage.PostDistributeDetached);

    // Act
    await strategy.DisposeAsync();

    // Assert
    await Assert.That(lifecycleStagesInCallback).IsNotNull();
    await Assert.That(lifecycleStagesInCallback).ContainsKey(message1);
    await Assert.That(lifecycleStagesInCallback).ContainsKey(message2);
    await Assert.That(lifecycleStagesInCallback[message1]).IsEqualTo(LifecycleStage.PreDistributeDetached);
    await Assert.That(lifecycleStagesInCallback[message2]).IsEqualTo(LifecycleStage.PostDistributeDetached);
  }

  [Test]
  public async Task MultipleMessages_DifferentLifecycleStages_ShareSameUnitAsync() {
    // Arrange
    await using var strategy = new ScopedUnitOfWorkStrategy();
    strategy.OnFlushRequested += async (unitId, ct) => await Task.CompletedTask;

    var message1 = new TestMessage { Value = "test1" };
    var message2 = new TestMessage { Value = "test2" };

    // Act
    var unitId1 = await strategy.QueueMessageAsync(message1, LifecycleStage.ImmediateDetached);
    var unitId2 = await strategy.QueueMessageAsync(message2, LifecycleStage.PreDistributeDetached);

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
    var unitId = await strategy.QueueMessageAsync(message);

    // Act (should NOT throw - just skip flush if no callback)
    await strategy.DisposeAsync();

    // Assert - the silent-skip branch has to run all the way to disposed, not bail at the missing
    // callback. Leaving the unit in place would hand the next `await using` scope a strategy that
    // reports the previous scope's messages; leaving `_disposed` false would let it keep accepting
    // work that no callback will ever flush.
    await Assert.That(strategy.GetMessagesForUnit(unitId).Count).IsEqualTo(0)
      .Because("no callback means the unit is abandoned here — it must not survive into the next "
             + "scope as phantom queued work");
    await Assert.That(async () => await strategy.QueueMessageAsync(new TestMessage { Value = "after" }))
      .ThrowsExactly<ObjectDisposedException>()
      .Because("a missing callback skips the flush, not the disposal — a strategy that still "
             + "accepts messages after DisposeAsync buffers them into an object nothing will drain");
  }

  /// <summary>
  /// Test message class.
  /// </summary>
  private sealed class TestMessage {
    public string Value { get; set; } = string.Empty;
  }
}
