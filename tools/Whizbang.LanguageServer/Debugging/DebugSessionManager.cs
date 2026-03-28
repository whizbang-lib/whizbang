namespace Whizbang.LanguageServer.Debugging;

/// <summary>
/// Tracks when a developer is paused at a breakpoint and manages state for keepalive services.
/// Thread-safe: all state mutations are protected by a lock.
/// </summary>
public sealed class DebugSessionManager : IDisposable {
  private bool _isPaused;
  private DateTimeOffset? _pausedAt;
  private TimeSpan _totalPausedTime = TimeSpan.Zero;
  private int _pauseCount;
  private bool _disposed;
  private readonly object _lock = new();

  /// <summary>Gets whether the session is currently paused at a breakpoint.</summary>
  public bool IsPaused {
    get {
      lock (_lock) {
        return _isPaused;
      }
    }
  }

  /// <summary>Gets the duration of the current pause, or <see cref="TimeSpan.Zero"/> if not paused.</summary>
  public TimeSpan CurrentPauseDuration {
    get {
      lock (_lock) {
        return _isPaused && _pausedAt.HasValue
            ? DateTimeOffset.UtcNow - _pausedAt.Value
            : TimeSpan.Zero;
      }
    }
  }

  /// <summary>Gets the total accumulated time spent paused across all pause/resume cycles.</summary>
  public TimeSpan TotalPausedTime {
    get {
      lock (_lock) {
        var total = _totalPausedTime;
        if (_isPaused && _pausedAt.HasValue) {
          total += DateTimeOffset.UtcNow - _pausedAt.Value;
        }
        return total;
      }
    }
  }

  /// <summary>Gets the number of times the session has been paused.</summary>
  public int PauseCount {
    get {
      lock (_lock) {
        return _pauseCount;
      }
    }
  }

  /// <summary>Raised when the session transitions to a paused state.</summary>
  public event Action? OnPaused;

  /// <summary>Raised when the session transitions from paused to resumed.</summary>
  public event Action? OnResumed;

  /// <summary>Notifies the manager that the debugger has paused. Idempotent — multiple calls while already paused are no-ops.</summary>
  public void NotifyPaused() {
    Action? handler = null;

    lock (_lock) {
      if (_disposed || _isPaused) {
        return;
      }

      _isPaused = true;
      _pausedAt = DateTimeOffset.UtcNow;
      _pauseCount++;
      handler = OnPaused;
    }

    // Fire event outside lock to avoid deadlocks
    handler?.Invoke();
  }

  /// <summary>Notifies the manager that the debugger has resumed. Safe to call when not paused — it is a no-op.</summary>
  public void NotifyResumed() {
    Action? handler = null;

    lock (_lock) {
      if (_disposed || !_isPaused) {
        return;
      }

      if (_pausedAt.HasValue) {
        _totalPausedTime += DateTimeOffset.UtcNow - _pausedAt.Value;
      }

      _isPaused = false;
      _pausedAt = null;
      handler = OnResumed;
    }

    // Fire event outside lock to avoid deadlocks
    handler?.Invoke();
  }

  /// <summary>Returns a snapshot of the current debug session statistics.</summary>
  public DebugSessionStats GetStats() {
    lock (_lock) {
      var currentDuration = _isPaused && _pausedAt.HasValue
          ? DateTimeOffset.UtcNow - _pausedAt.Value
          : TimeSpan.Zero;

      var totalPaused = _totalPausedTime;
      if (_isPaused && _pausedAt.HasValue) {
        totalPaused += DateTimeOffset.UtcNow - _pausedAt.Value;
      }

      return new DebugSessionStats {
        IsPaused = _isPaused,
        PauseCount = _pauseCount,
        TotalPausedTime = totalPaused,
        CurrentPauseDuration = currentDuration
      };
    }
  }

  /// <summary>Clears event handlers to prevent further notifications.</summary>
  public void Dispose() {
    lock (_lock) {
      _disposed = true;
      OnPaused = null;
      OnResumed = null;
    }
  }
}

/// <summary>A snapshot of debug session statistics.</summary>
public sealed record DebugSessionStats {
  /// <summary>Gets whether the session is currently paused.</summary>
  public required bool IsPaused { get; init; }

  /// <summary>Gets the number of times the session has been paused.</summary>
  public required int PauseCount { get; init; }

  /// <summary>Gets the total accumulated paused time.</summary>
  public required TimeSpan TotalPausedTime { get; init; }

  /// <summary>Gets the duration of the current pause.</summary>
  public required TimeSpan CurrentPauseDuration { get; init; }
}
