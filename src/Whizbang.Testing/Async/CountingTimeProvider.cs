// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Testing.Async;

/// <summary>
/// A clock that counts the timers made on it and not yet disposed, over another clock that keeps the time. A delay or
/// a deadline on this clock is one such timer until it fires or is canceled, so the count says exactly how many waits
/// on the clock are still pending.
/// </summary>
/// <remarks>
/// <para>
/// <c>Task.Delay(due, timeProvider, token)</c> makes one timer on the provider and disposes it once the delay fires or
/// its token is canceled; a <see cref="CancellationTokenSource"/> made with a provider holds one until it is disposed.
/// A race whose losing delay is left running shows up here as a live timer until that delay fires; one whose losing
/// delay is canceled does not. Wrap a fake clock (for instance <c>FakeTimeProvider</c>) and move time on the fake:
/// every call but <see cref="CreateTimer"/> goes straight to it.
/// </para>
/// <para>
/// <see cref="Created(int)"/> completes once a given number of timers have been made, so a test can step a loop one
/// armed wait at a time without sleeping.
/// </para>
/// </remarks>
/// <param name="inner">The clock that keeps the time and runs the timers.</param>
/// <docs>contributors/testing/loop-waits</docs>
/// <tests>tests/Whizbang.Testing.Tests/Async/LoopWaitProbeTests.cs</tests>
public sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider {
  private readonly Lock _lock = new();
  private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
  private int _live;
  private int _created;

  /// <summary>The clock that keeps the time.</summary>
  public TimeProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

  /// <summary>Timers made and not yet disposed.</summary>
  public int LiveTimers => Volatile.Read(ref _live);

  /// <summary>Timers made in all.</summary>
  public int CreatedTimers {
    get {
      lock (_lock) {
        return _created;
      }
    }
  }

  /// <summary>Completes once <paramref name="count"/> timers have been made in all.</summary>
  /// <param name="count">How many timers.</param>
  public Task Created(int count) {
    lock (_lock) {
      if (_created >= count) {
        return Task.CompletedTask;
      }
      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _waiters.Add((count, signal));
      return signal.Task;
    }
  }

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
    // Counted only once the inner clock holds the timer: a test that moves the clock the moment it hears of the timer
    // must find it there to fire.
    var timer = new CountedTimer(Inner.CreateTimer(callback, state, dueTime, period), this);
    Interlocked.Increment(ref _live);
    List<TaskCompletionSource> reached = [];
    lock (_lock) {
      _created++;
      for (var i = _waiters.Count - 1; i >= 0; i--) {
        if (_created >= _waiters[i].Count) {
          reached.Add(_waiters[i].Signal);
          _waiters.RemoveAt(i);
        }
      }
    }
    foreach (var signal in reached) {
      signal.TrySetResult();
    }
    return timer;
  }

  private sealed class CountedTimer(ITimer inner, CountingTimeProvider owner) : ITimer {
    private int _disposed;

    public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

    public void Dispose() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        Interlocked.Decrement(ref owner._live);
      }
      inner.Dispose();
    }

    public ValueTask DisposeAsync() {
      Dispose();
      return ValueTask.CompletedTask;
    }
  }
}
