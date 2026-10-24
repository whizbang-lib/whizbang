// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// A fake clock that reports every timer armed on it, so a test can wait for a loop to arm its
/// next wait and then fire exactly that timer. Nothing here reads or waits on real time.
/// </summary>
internal sealed class ProbeClock : FakeTimeProvider {
  private readonly Channel<DateTimeOffset> _armed = Channel.CreateUnbounded<DateTimeOffset>();

  /// <summary>Where every probe clock starts.</summary>
  public static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  public ProbeClock() : base(Origin) {
  }

  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    var timer = base.CreateTimer(callback, state, dueTime, period);
    _armed.Writer.TryWrite(GetUtcNow() + dueTime);
    return timer;
  }

  /// <summary>Completes with the due time of the next timer armed, in arming order.</summary>
  public ValueTask<DateTimeOffset> NextArmedAsync(CancellationToken cancellationToken) =>
    _armed.Reader.ReadAsync(cancellationToken);

  /// <summary>
  /// Fires timers in the order they are armed until <paramref name="done"/> completes: waits for
  /// the next timer to be armed, then moves the clock to its due time. A timer already due is
  /// skipped over without moving the clock back.
  /// </summary>
  public async Task DriveUntilAsync(Task done, CancellationToken cancellationToken) {
    while (true) {
      var next = NextArmedAsync(cancellationToken).AsTask();
      if (await Task.WhenAny(done, next) == done) {
        return;
      }
      var due = await next;
      if (due > GetUtcNow()) {
        SetUtcNow(due);
      }
    }
  }
}
