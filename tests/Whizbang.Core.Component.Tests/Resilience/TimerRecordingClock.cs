// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// A fake clock that reports the due time of every timer armed on it, in the order they were armed. A delay taken on
/// this clock arms one timer for exactly the delay, so a test reads what a wait was by reading its timer, and only then
/// advances the clock past it: the advance can never land before the timer it means to fire is armed.
/// </summary>
internal sealed class TimerRecordingClock() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) {
  private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

  /// <summary>
  /// Completes with the due time of the next timer armed on this clock that <paramref name="isOfInterest"/> accepts
  /// (every timer when it is omitted), skipping the others.
  /// </summary>
  public async Task<TimeSpan> NextTimerAsync(Func<TimeSpan, bool>? isOfInterest = null) {
    while (true) {
      var dueTime = await _armed.Reader.ReadAsync();
      if (isOfInterest is null || isOfInterest(dueTime)) {
        return dueTime;
      }
    }
  }

  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    var timer = base.CreateTimer(callback, state, dueTime, period);
    _armed.Writer.TryWrite(dueTime);
    return timer;
  }
}
