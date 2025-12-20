using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Medo;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Immediate unit of work strategy - flushes immediately on each QueueMessageAsync call.
/// Provides lowest latency but highest coordination overhead.
/// Best for: Real-time scenarios, critical messages, low-throughput services.
/// Each message gets its own unit with time-ordered Uuid7 ID.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
public class ImmediateUnitOfWorkStrategy : IUnitOfWorkStrategy {
  private readonly ConcurrentDictionary<Guid, DispatchUnitOfWork> _units = new();

  /// <inheritdoc />
  public event Func<Guid, CancellationToken, Task>? OnFlushRequested;

  /// <inheritdoc />
  public async Task<Guid> QueueMessageAsync(
    object message,
    LifecycleStage lifecycleStage = LifecycleStage.ImmediateAsync,
    CancellationToken ct = default
  ) {
    if (OnFlushRequested == null) {
      throw new InvalidOperationException(
        "OnFlushRequested callback must be wired before calling QueueMessageAsync. " +
        "Wire the callback: strategy.OnFlushRequested += async (unitId, ct) => { ... }"
      );
    }

    // Create new unit with time-ordered Uuid7 ID
    var unitId = Uuid7.NewUuid7().ToGuid();
    var unit = new DispatchUnitOfWork {
      UnitId = unitId,
      Messages = [message],
      CreatedAt = DateTimeOffset.UtcNow,
      LifecycleStages = new Dictionary<object, LifecycleStage> {
        [message] = lifecycleStage
      }
    };

    // Store unit (callback will read it via GetMessagesForUnit)
    _units[unitId] = unit;

    // Trigger callback immediately and await completion
    await OnFlushRequested.Invoke(unitId, ct);

    return unitId;
  }

  /// <inheritdoc />
  public Task CancelUnitAsync(Guid unitId, CancellationToken ct = default) {
    // Remove unit if it exists (no-op if already flushed)
    _units.TryRemove(unitId, out _);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public IReadOnlyList<object> GetMessagesForUnit(Guid unitId) {
    if (_units.TryGetValue(unitId, out var unit)) {
      return unit.Messages.AsReadOnly();
    }
    return Array.Empty<object>();
  }

  /// <inheritdoc />
  public IReadOnlyDictionary<object, LifecycleStage> GetLifecycleStagesForUnit(Guid unitId) {
    if (_units.TryGetValue(unitId, out var unit)) {
      return unit.LifecycleStages;
    }
    return new Dictionary<object, LifecycleStage>();
  }

  /// <summary>
  /// Disposes the strategy (no-op for immediate strategy).
  /// </summary>
  public ValueTask DisposeAsync() {
    // No background tasks or resources to clean up
    _units.Clear();
    return ValueTask.CompletedTask;
  }
}
