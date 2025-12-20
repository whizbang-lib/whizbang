using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Strategy for managing units of work with lifecycle-aware message batching.
/// Controls WHEN to flush units to IWorkBatchCoordinator via async callback pattern.
/// Implementations: Immediate (flush per message), Scoped (flush on dispose), Interval (flush on timer).
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs</tests>
public interface IUnitOfWorkStrategy {
  /// <summary>
  /// Called by strategy when it autonomously decides to flush a unit.
  /// Dispatcher/Worker wires this to IWorkBatchCoordinator.ProcessAndDistributeAsync.
  ///
  /// Handler signature: async (Guid unitId, CancellationToken ct) => { ... }
  ///
  /// The callback receives:
  /// - unitId: Unit to flush
  /// - ct: Cancellation token
  ///
  /// The callback should:
  /// 1. Call GetMessagesForUnit(unitId)
  /// 2. Call IWorkBatchCoordinator.ProcessAndDistributeAsync with messages
  /// 3. Process returned work (distribute to channels)
  /// 4. Report completions/failures back
  /// </summary>
  event Func<Guid, CancellationToken, Task>? OnFlushRequested;

  /// <summary>
  /// Queue a message asynchronously.
  /// Returns the unit ID (time-ordered Uuid7).
  ///
  /// Behavior by strategy:
  /// - Immediate: Creates new unit, triggers OnFlushRequested, awaits completion, returns unit ID
  /// - Scoped: Adds to current unit, returns immediately (flush on DisposeAsync)
  /// - Interval: Adds to current unit, returns immediately (flush on timer)
  /// </summary>
  /// <param name="message">Message to queue (Command or Event)</param>
  /// <param name="lifecycleStage">Lifecycle stage for this message (default: ImmediateAsync)</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>Unit ID (Uuid7-based GUID)</returns>
  Task<Guid> QueueMessageAsync(
    object message,
    LifecycleStage lifecycleStage = LifecycleStage.ImmediateAsync,
    CancellationToken ct = default
  );

  /// <summary>
  /// Cancel a pending unit (before flush).
  /// Removes unit from queue.
  /// If unit doesn't exist or already flushed, this is a no-op.
  /// </summary>
  /// <param name="unitId">Unit ID to cancel</param>
  /// <param name="ct">Cancellation token</param>
  Task CancelUnitAsync(Guid unitId, CancellationToken ct = default);

  /// <summary>
  /// Get all messages for a unit (called by OnFlushRequested callback).
  /// Returns messages in order they were queued.
  /// </summary>
  /// <param name="unitId">Unit ID to query</param>
  /// <returns>Read-only list of messages, or empty list if unit doesn't exist</returns>
  IReadOnlyList<object> GetMessagesForUnit(Guid unitId);

  /// <summary>
  /// Get lifecycle stage assignments for a unit.
  /// Key = message instance, Value = lifecycle stage.
  /// </summary>
  /// <param name="unitId">Unit ID to query</param>
  /// <returns>Read-only dictionary of lifecycle stages, or empty dictionary if unit doesn't exist</returns>
  IReadOnlyDictionary<object, LifecycleStage> GetLifecycleStagesForUnit(Guid unitId);
}
