using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Medo;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Scoped unit of work strategy - accumulates messages in single unit, flushes on DisposeAsync.
/// Provides good balance of latency and coordination overhead.
/// Best for: Web APIs, message handlers, transactional operations, scoped lifetimes.
/// All messages in scope share single unit with time-ordered Uuid7 ID.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
public class ScopedUnitOfWorkStrategy : IUnitOfWorkStrategy, IAsyncDisposable {
  private DispatchUnitOfWork? _currentUnit;
  private bool _disposed;

  /// <inheritdoc />
  public event Func<Guid, CancellationToken, Task>? OnFlushRequested;

  /// <inheritdoc />
  public Task<Guid> QueueMessageAsync(
    object message,
    LifecycleStage lifecycleStage = LifecycleStage.ImmediateAsync,
    CancellationToken ct = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    // Create unit on first message
    if (_currentUnit == null) {
      _currentUnit = new DispatchUnitOfWork {
        UnitId = Uuid7.NewUuid7().ToGuid(),
        Messages = [],
        CreatedAt = DateTimeOffset.UtcNow,
        LifecycleStages = []
      };
    }

    // Add message to current unit
    _currentUnit.Messages.Add(message);
    _currentUnit.LifecycleStages[message] = lifecycleStage;

    // Return immediately (flush happens on DisposeAsync)
    return Task.FromResult(_currentUnit.UnitId);
  }

  /// <inheritdoc />
  public Task CancelUnitAsync(Guid unitId, CancellationToken ct = default) {
    // If unit matches current unit, clear it
    if (_currentUnit?.UnitId == unitId) {
      _currentUnit = null;
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public IReadOnlyList<object> GetMessagesForUnit(Guid unitId) {
    if (_currentUnit?.UnitId == unitId) {
      return _currentUnit.Messages.AsReadOnly();
    }
    return Array.Empty<object>();
  }

  /// <inheritdoc />
  public IReadOnlyDictionary<object, LifecycleStage> GetLifecycleStagesForUnit(Guid unitId) {
    if (_currentUnit?.UnitId == unitId) {
      return _currentUnit.LifecycleStages;
    }
    return new Dictionary<object, LifecycleStage>();
  }

  /// <summary>
  /// Flushes current unit (if any) by triggering OnFlushRequested callback.
  /// Clears unit after flush.
  /// </summary>
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    // Flush current unit if it has messages
    if (_currentUnit != null && _currentUnit.Messages.Count > 0) {
      if (OnFlushRequested != null) {
        try {
          await OnFlushRequested.Invoke(_currentUnit.UnitId, CancellationToken.None);
        } finally {
          // Clear unit after flush
          _currentUnit = null;
        }
      } else {
        // No callback wired - just clear the unit (silent skip)
        _currentUnit = null;
      }
    }

    _disposed = true;
    GC.SuppressFinalize(this);
  }
}
