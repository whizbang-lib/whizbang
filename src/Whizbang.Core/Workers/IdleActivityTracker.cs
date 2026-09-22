namespace Whizbang.Core.Workers;

/// <summary>
/// In-process singleton implementation of <see cref="IIdleActivityTracker"/>.
/// Pure in-memory state — no I/O, no DB calls. Slice 4 of zero-idle-polling.
/// </summary>
/// <remarks>
/// Thread-safe via a single <see cref="Lock"/>; the critical section is
/// trivially short (two field assignments on write, one subtraction on read),
/// so contention from concurrent hook callers is negligible.
/// </remarks>
/// <docs>fundamentals/work-coordinator/idle-activity-tracking</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IdleActivityTrackerTests.cs</tests>
public sealed class IdleActivityTracker : IIdleActivityTracker {
  private readonly TimeProvider _timeProvider;
  private readonly Lock _lock = new();
  private DateTimeOffset _lastActivityAt;
  private string _lastActivitySource;

  /// <summary>
  /// Initializes a new tracker. The construction time itself counts as
  /// "activity" for the source <c>"startup"</c> so a freshly-started pod
  /// doesn't immediately appear idle to the
  /// <see cref="BackupTickCoordinator"/>.
  /// </summary>
  public IdleActivityTracker(TimeProvider? timeProvider = null) {
    _timeProvider = timeProvider ?? TimeProvider.System;
    _lastActivityAt = _timeProvider.GetUtcNow();
    _lastActivitySource = "startup";
  }

  /// <inheritdoc />
  public void Touch(string source) {
    ArgumentNullException.ThrowIfNull(source);
    lock (_lock) {
      _lastActivityAt = _timeProvider.GetUtcNow();
      _lastActivitySource = source;
    }
  }

  /// <inheritdoc />
  public TimeSpan TimeSinceLastActivity {
    get {
      lock (_lock) {
        return _timeProvider.GetUtcNow() - _lastActivityAt;
      }
    }
  }

  /// <inheritdoc />
  public DateTimeOffset LastActivityAt {
    get {
      lock (_lock) {
        return _lastActivityAt;
      }
    }
  }

  /// <inheritdoc />
  public string LastActivitySource {
    get {
      lock (_lock) {
        return _lastActivitySource;
      }
    }
  }
}
