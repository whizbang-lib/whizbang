// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Testing.Async;

/// <summary>
/// A clock that counts the timers made on it and how many are armed, over another clock that keeps the time. A delay or
/// a deadline on this clock is an armed timer until it fires or is stopped, so the counts say exactly how many waits on
/// the clock are still pending.
/// </summary>
/// <remarks>
/// <para>
/// <c>Task.Delay(due, timeProvider, token)</c> makes one timer on the provider and disposes it once the delay fires or
/// its token is canceled; a <see cref="CancellationTokenSource"/> made with a provider holds one until it is disposed;
/// a <c>WakeSet</c> makes one timer for its loop and re-arms it each pass. A race whose losing delay is left running
/// shows up as a live, armed timer until that delay fires; one whose losing delay is canceled or stopped does not.
/// Wrap a fake clock (for instance <c>FakeTimeProvider</c>) and move time on the fake: every call but
/// <see cref="CreateTimer"/> goes straight to it.
/// </para>
/// <para>
/// <see cref="Created(int)"/> and <see cref="Armed(int)"/> complete once a given number of timers have been made, or
/// armed (made with a due time, or re-armed with one), so a test can step a loop one armed wait at a time without
/// sleeping. A loop that re-arms one timer is stepped by <see cref="Armed(int)"/>.
/// </para>
/// </remarks>
/// <param name="inner">The clock that keeps the time and runs the timers.</param>
/// <docs>contributors/testing/loop-waits</docs>
/// <tests>tests/Whizbang.Testing.Tests/Async/LoopWaitProbeTests.cs</tests>
public sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider {
  private readonly Lock _lock = new();
  private readonly List<(Counted What, int Count, TaskCompletionSource Signal)> _waiters = [];
  private int _live;
  private int _armedNow;
  private int _created;
  private int _arms;
  private int _disarms;

  /// <summary>The clock that keeps the time.</summary>
  public TimeProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

  /// <summary>Timers made and not yet disposed.</summary>
  public int LiveTimers => Volatile.Read(ref _live);

  /// <summary>Timers armed now: given a due time and neither fired (once), stopped, nor disposed since.</summary>
  public int ArmedTimers => Volatile.Read(ref _armedNow);

  /// <summary>Timers made in all.</summary>
  public int CreatedTimers {
    get {
      lock (_lock) {
        return _created;
      }
    }
  }

  /// <summary>Times a timer was armed in all: made with a due time, or re-armed with one.</summary>
  public int TimesArmed {
    get {
      lock (_lock) {
        return _arms;
      }
    }
  }

  /// <summary>Completes once <paramref name="count"/> timers have been made in all.</summary>
  /// <param name="count">How many timers.</param>
  public Task Created(int count) => _when(Counted.Created, count);

  /// <summary>Completes once timers have been armed <paramref name="count"/> times in all.</summary>
  /// <param name="count">How many armings.</param>
  public Task Armed(int count) => _when(Counted.Armed, count);

  /// <summary>
  /// Completes once armed timers have been disarmed <paramref name="count"/> times in all: fired once, stopped, or
  /// disposed while armed. The signal that a deadline is gone, for code that stops it just after its own work completes.
  /// </summary>
  /// <param name="count">How many disarmings.</param>
  public Task Disarmed(int count) => _when(Counted.Disarmed, count);

  /// <inheritdoc />
  public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();

  /// <inheritdoc />
  public override long GetTimestamp() => Inner.GetTimestamp();

  /// <inheritdoc />
  public override long TimestampFrequency => Inner.TimestampFrequency;

  /// <inheritdoc />
  public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;

  /// <inheritdoc />
  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    var timer = new CountedTimer(this, callback, state, period);
    var arms = timer.MarkArmed(dueTime);
    // Counted only once the inner clock holds the timer: a test that moves the clock the moment it hears of the timer
    // must find it there to fire.
    timer.Inner = Inner.CreateTimer(static s => ((CountedTimer)s!).Fire(), timer, dueTime, period);
    Interlocked.Increment(ref _live);
    _counted(created: true, arms: arms);
    return timer;
  }

  /// <summary>What a signal counts.</summary>
  private enum Counted {
    Created,
    Armed,
    Disarmed,
  }

  private int _countOf(Counted what) => what switch {
    Counted.Created => _created,
    Counted.Armed => _arms,
    _ => _disarms,
  };

  private Task _when(Counted what, int count) {
    lock (_lock) {
      if (_countOf(what) >= count) {
        return Task.CompletedTask;
      }
      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiters.Add((what, count, signal));
      return signal.Task;
    }
  }

  private void _counted(bool created = false, bool arms = false, bool disarms = false) {
    List<TaskCompletionSource> reached = [];
    lock (_lock) {
      if (created) {
        _created++;
      }
      if (arms) {
        _arms++;
      }
      if (disarms) {
        _disarms++;
      }
      for (var i = _waiters.Count - 1; i >= 0; i--) {
        if (_countOf(_waiters[i].What) >= _waiters[i].Count) {
          reached.Add(_waiters[i].Signal);
          _waiters.RemoveAt(i);
        }
      }
    }
    foreach (var signal in reached) {
      signal.TrySetResult();
    }
  }

  private sealed class CountedTimer(CountingTimeProvider owner, TimerCallback callback, object? state, TimeSpan period) : ITimer {
    private readonly Lock _gate = new();
    private TimeSpan _period = period;
    private bool _armed;
    private bool _disposed;

    public ITimer Inner { get; set; } = null!;

    /// <summary>Records the timer armed or stopped by a due time; true when it was armed.</summary>
    public bool MarkArmed(TimeSpan dueTime) {
      var arms = dueTime != Timeout.InfiniteTimeSpan;
      lock (_gate) {
        if (_disposed || arms == _armed) {
          return arms && !_disposed;
        }
        _armed = arms;
      }
      Interlocked.Add(ref owner._armedNow, arms ? 1 : -1);
      if (!arms) {
        owner._counted(disarms: true);
      }
      return arms;
    }

    public void Fire() {
      if (_period == Timeout.InfiniteTimeSpan) {
        MarkArmed(Timeout.InfiniteTimeSpan);
      }
      callback(state);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period) {
      _period = period;
      var arms = MarkArmed(dueTime);
      var changed = Inner.Change(dueTime, period);
      owner._counted(arms: arms);
      return changed;
    }

    public void Dispose() {
      MarkArmed(Timeout.InfiniteTimeSpan);
      bool first;
      lock (_gate) {
        first = !_disposed;
        _disposed = true;
      }
      if (first) {
        Interlocked.Decrement(ref owner._live);
      }
      Inner.Dispose();
    }

    public ValueTask DisposeAsync() {
      Dispose();
      return ValueTask.CompletedTask;
    }
  }
}
