// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="ScopedUnitOfWorkStrategy.CancelUnitAsync"/> when no unit is
/// accumulating (nothing queued yet, or the unit was already canceled): the cancel is a no-op,
/// and the next queued message opens a fresh unit that flushes when the scope is disposed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ScopedUnitOfWorkStrategy.cs</code-under-test>
public class ScopedUnitOfWorkStrategyBranchCoverageTests {

  [Test]
  public async Task CancelUnitAsync_NoUnitAccumulating_IsANoOpAndTheNextMessageOpensAFreshUnitAsync() {
    var flushed = new List<Guid>();
    await using var strategy = new ScopedUnitOfWorkStrategy();
    strategy.OnFlushRequested += (unitId, _) => {
      flushed.Add(unitId);
      return Task.CompletedTask;
    };

    // Nothing queued yet: there is no current unit to compare against.
    await strategy.CancelUnitAsync(Guid.NewGuid());
    var canceledUnitId = await strategy.QueueMessageAsync("discarded");
    await strategy.CancelUnitAsync(canceledUnitId);
    // The unit is gone, so this second cancel again finds no current unit.
    await strategy.CancelUnitAsync(canceledUnitId);
    var freshUnitId = await strategy.QueueMessageAsync("kept");

    await Assert.That(freshUnitId).IsNotEqualTo(canceledUnitId)
      .Because("a canceled unit is never reopened; the next message starts a new one");
    await Assert.That(strategy.GetMessagesForUnit(freshUnitId)).IsEquivalentTo(new object[] { "kept" });

    // Disposal is the flush; the await using above makes the second dispose a guarded no-op.
    await strategy.DisposeAsync();

    await Assert.That(flushed).IsEquivalentTo([freshUnitId])
      .Because("only the fresh unit is flushed when the scope ends; the canceled one is gone for good");
  }
}
