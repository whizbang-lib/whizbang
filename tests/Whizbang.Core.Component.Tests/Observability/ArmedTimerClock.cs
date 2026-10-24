// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// A fake clock that reports each timer armed on it, so a test can wait for a worker to start waiting and then advance
/// the clock past the wait, instead of sleeping.
/// </summary>
internal sealed class ArmedTimerClock() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) {
  private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    var timer = base.CreateTimer(callback, state, dueTime, period);
    _armed.Writer.TryWrite(dueTime);
    return timer;
  }

  /// <summary>Completes once a timer with <paramref name="due"/> has been armed.</summary>
  public async Task WaitForArmedAsync(TimeSpan due, TimeSpan timeout) {
    using var cts = new CancellationTokenSource(timeout);
    TimeSpan armed;
    do {
      armed = await _armed.Reader.ReadAsync(cts.Token);
    } while (armed != due);
  }
}
