// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A fake clock that reports every time a timer on it is armed: created with a due time, or moved to one with
/// <see cref="ITimer.Change"/>. A test waits for the wait it means to end to be armed, and only then advances the clock
/// past it, so the advance can never land before the timer it is meant to fire. Nothing here reads or waits on real time.
/// </summary>
/// <remarks>
/// <para>
/// A timer from <see cref="FakeTimeProvider"/> runs its callback on the thread that advances the clock, in that thread's
/// context. The system clock does not: its timers capture the <see cref="ExecutionContext"/> of the code
/// that created them and run every callback in it. A clock built with <c>flowsExecutionContext: true</c> does the same,
/// so a test can see what a timer created inside a caller's ambient context would carry into its ticks.
/// </para>
/// </remarks>
/// <param name="flowsExecutionContext">Whether a timer runs its callback in the context it was created in, as the system clock's timers do.</param>
internal sealed class ArmingClock(bool flowsExecutionContext = false)
    : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) {
  private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

  /// <inheritdoc />
  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    var run = callback;
    // Capture returns null when the creator suppressed the flow, and then the callback runs with no captured context.
    if (flowsExecutionContext && ExecutionContext.Capture() is { } captured) {
      run = s => ExecutionContext.Run(captured, callbackState => callback(callbackState), s);
    }
    var timer = new ArmedTimer(base.CreateTimer(run, state, dueTime, period), this);
    _record(dueTime);
    return timer;
  }

  /// <summary>
  /// Completes once a timer is armed for <paramref name="due"/>, skipping every other arming before it. Bounded only by
  /// <paramref name="cancellationToken"/> (the test's own timeout).
  /// </summary>
  public async Task WaitForArmedAsync(TimeSpan due, CancellationToken cancellationToken) {
    TimeSpan armed;
    do {
      armed = await _armed.Reader.ReadAsync(cancellationToken);
    } while (armed != due);
  }

  /// <summary>Every due time armed so far and not yet consumed by <see cref="WaitForArmedAsync"/>.</summary>
  public IReadOnlyList<TimeSpan> DrainArmed() {
    var armed = new List<TimeSpan>();
    while (_armed.Reader.TryRead(out var due)) {
      armed.Add(due);
    }
    return armed;
  }

  private void _record(TimeSpan dueTime) {
    if (dueTime != Timeout.InfiniteTimeSpan) {
      _armed.Writer.TryWrite(dueTime);
    }
  }

  private sealed class ArmedTimer(ITimer inner, ArmingClock clock) : ITimer {
    public bool Change(TimeSpan dueTime, TimeSpan period) {
      var changed = inner.Change(dueTime, period);
      if (changed) {
        clock._record(dueTime);
      }
      return changed;
    }

    public void Dispose() => inner.Dispose();

    public ValueTask DisposeAsync() => inner.DisposeAsync();
  }
}
