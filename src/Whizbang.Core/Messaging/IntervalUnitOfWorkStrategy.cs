using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Medo;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Interval unit of work strategy - accumulates messages, flushes on timer tick.
/// Uses flush-then-wait pattern: accumulate → flush → wait for callback → cleanup → repeat.
/// Best for: Background workers, batch processing, high-throughput scenarios.
/// Interval starts AFTER previous flush completes (natural backpressure).
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
public class IntervalUnitOfWorkStrategy : IUnitOfWorkStrategy, IAsyncDisposable {
  private readonly TimeSpan _interval;
  private readonly Task _flushTask;
  private readonly CancellationTokenSource _disposeCts = new();
  private readonly SemaphoreSlim _unitLock = new(1, 1);
  private readonly ConcurrentDictionary<Guid, DispatchUnitOfWork> _allUnits = new();
  private volatile DispatchUnitOfWork? _currentUnit;
  private bool _disposed;

  /// <inheritdoc />
  public event Func<Guid, CancellationToken, Task>? OnFlushRequested;

  /// <summary>
  /// Creates interval strategy with specified flush interval.
  /// Background flush task starts immediately.
  /// Interval begins AFTER each flush completes (not fixed periodic timing).
  /// </summary>
  /// <param name="interval">Interval between flush completion and next flush</param>
  public IntervalUnitOfWorkStrategy(TimeSpan interval) {
    _interval = interval;
    _flushTask = _runFlushLoopAsync(_disposeCts.Token);
  }

  /// <inheritdoc />
  public async Task<Guid> QueueMessageAsync(
    object message,
    LifecycleStage lifecycleStage = LifecycleStage.ImmediateAsync,
    CancellationToken ct = default
  ) {
    ObjectDisposedException.ThrowIf(_disposed, this);

    await _unitLock.WaitAsync(ct);
    try {
      // Create new accumulating unit if none exists
      if (_currentUnit == null) {
        _currentUnit = new DispatchUnitOfWork {
          UnitId = Uuid7.NewUuid7().ToGuid(),
          Messages = [],
          CreatedAt = DateTimeOffset.UtcNow,
          LifecycleStages = []
        };
        // Track in dictionary so GetMessagesForUnit always works
        _allUnits[_currentUnit.UnitId] = _currentUnit;
      }

      // Add message to current unit
      _currentUnit.Messages.Add(message);
      _currentUnit.LifecycleStages[message] = lifecycleStage;

      // Return immediately (flush happens on timer tick)
      return _currentUnit.UnitId;
    } finally {
      _unitLock.Release();
    }
  }

  /// <inheritdoc />
  public async Task CancelUnitAsync(Guid unitId, CancellationToken ct = default) {
    await _unitLock.WaitAsync(ct);
    try {
      // Clear current unit if it matches
      if (_currentUnit?.UnitId == unitId) {
        _currentUnit = null;
      }
      // Remove from tracking dictionary
      _allUnits.TryRemove(unitId, out _);
    } finally {
      _unitLock.Release();
    }
  }

  /// <inheritdoc />
  public IReadOnlyList<object> GetMessagesForUnit(Guid unitId) {
    // Volatile read ensures we see the latest value
    var currentUnit = _currentUnit;

    // Check current unit first (common case before flush)
    if (currentUnit?.UnitId == unitId) {
      return currentUnit.Messages.AsReadOnly();
    }

    // Fallback to dictionary lookup (for units being flushed)
    // ConcurrentDictionary is thread-safe for reads
    if (_allUnits.TryGetValue(unitId, out var unit)) {
      return unit.Messages.AsReadOnly();
    }

    return Array.Empty<object>();
  }

  /// <inheritdoc />
  public IReadOnlyDictionary<object, LifecycleStage> GetLifecycleStagesForUnit(Guid unitId) {
    // Volatile read ensures we see the latest value
    var currentUnit = _currentUnit;

    // Check current unit first (common case before flush)
    if (currentUnit?.UnitId == unitId) {
      return currentUnit.LifecycleStages;
    }

    // Fallback to dictionary lookup (for units being flushed)
    if (_allUnits.TryGetValue(unitId, out var unit)) {
      return unit.LifecycleStages;
    }

    return new Dictionary<object, LifecycleStage>();
  }

  /// <summary>
  /// Background flush loop: wait → flush → callback → cleanup → repeat.
  /// Interval starts AFTER previous flush completes (natural backpressure).
  /// </summary>
  private async Task _runFlushLoopAsync(CancellationToken ct) {
    try {
      while (!ct.IsCancellationRequested) {
        // Wait for interval (next flush starts AFTER previous completes)
        await Task.Delay(_interval, ct);

        // Flush current unit (if any)
        await _flushCurrentUnitAsync(ct);
      }
    } catch (OperationCanceledException) {
      // Expected on disposal - exit gracefully
    }
  }

  /// <summary>
  /// Flushes current unit: rotate → invoke callback → cleanup.
  /// Waits for callback to complete before cleaning up.
  /// </summary>
  private async Task _flushCurrentUnitAsync(CancellationToken ct) {
    DispatchUnitOfWork? unitToFlush = null;

    // Rotate: capture current unit and create new one for next batch
    await _unitLock.WaitAsync(ct);
    try {
      if (_currentUnit != null && _currentUnit.Messages.Count > 0) {
        unitToFlush = _currentUnit;
        _currentUnit = null;  // New unit created on next QueueMessageAsync
      }
    } finally {
      _unitLock.Release();
    }

    // Invoke callback (WAIT for it to complete)
    if (unitToFlush != null && OnFlushRequested != null) {
      await OnFlushRequested.Invoke(unitToFlush.UnitId, ct);
    }

    // Cleanup: remove flushed unit from tracking dictionary (callback is done)
    if (unitToFlush != null) {
      _allUnits.TryRemove(unitToFlush.UnitId, out _);
    }
  }

  /// <summary>
  /// Stops flush loop and flushes any remaining units.
  /// </summary>
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    // Stop flush loop
    await _disposeCts.CancelAsync();

    // Wait for flush task to complete
    try {
      await _flushTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Flush remaining unit (if any)
    await _unitLock.WaitAsync();
    try {
      if (_currentUnit != null && _currentUnit.Messages.Count > 0) {
        if (OnFlushRequested != null) {
          await OnFlushRequested.Invoke(_currentUnit.UnitId, CancellationToken.None);
        }
        _allUnits.TryRemove(_currentUnit.UnitId, out _);
        _currentUnit = null;
      }
    } finally {
      _unitLock.Release();
    }

    // Cleanup
    _disposeCts.Dispose();
    _unitLock.Dispose();
    _allUnits.Clear();
    _disposed = true;
    GC.SuppressFinalize(this);
  }
}
