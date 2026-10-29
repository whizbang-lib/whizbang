// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A fake clock that signals each timer created on it, so a test can let every wait it schedules elapse, one advance
/// per timer, without reading the real clock (#1871: the outbox batcher's retry backoff).
/// </summary>
/// <remarks>
/// A wait on a frozen clock never ends by itself, and advancing before the wait has created its timer moves time past
/// nothing. <see cref="AdvanceTimersUntilAsync"/> waits for each timer to be registered and then advances by that
/// timer's due time, so every timer is followed by an advance made after it exists and none is left waiting. Timers
/// due after <paramref name="ignoredDueTime"/> (the batcher's periodic idle sweep) are not counted.
/// </remarks>
/// <param name="ignoredDueTime">A due time the test does not drive, such as the idle sweep's interval.</param>
internal sealed class TimerAdvancingClock(TimeSpan ignoredDueTime)
    : FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)) {
  private readonly Lock _gate = new();
  private readonly List<TaskCompletionSource<TimeSpan>> _created = [];
  private int _createdCount;

  /// <summary>Completes with the due time of the <paramref name="count"/>-th timer (from one) created on this clock.</summary>
  public Task<TimeSpan> Created(int count) {
    lock (_gate) {
      return _signal(count).Task;
    }
  }

  /// <summary>Advances by each created timer's due time, one timer at a time, until <paramref name="done"/> completes.</summary>
  public async Task AdvanceTimersUntilAsync(Task done, CancellationToken cancellationToken) {
    var count = 0;
    while (!done.IsCompleted) {
      count++;
      var created = Created(count);
      await Task.WhenAny(created, done).WaitAsync(cancellationToken);
      if (created.IsCompleted) {
        Advance(await created);
      }
    }
    await done;
  }

  /// <inheritdoc />
  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    // Register first, signal second: a test released by the signal must find the timer already due on advance.
    var timer = base.CreateTimer(callback, state, dueTime, period);
    if (dueTime != Timeout.InfiniteTimeSpan && dueTime < ignoredDueTime) {
      TaskCompletionSource<TimeSpan> signal;
      lock (_gate) {
        signal = _signal(++_createdCount);
      }
      signal.TrySetResult(dueTime);
    }
    return timer;
  }

  private TaskCompletionSource<TimeSpan> _signal(int count) {
    while (_created.Count < count) {
      _created.Add(new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
    return _created[count - 1];
  }
}
