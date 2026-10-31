// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A fake clock for tests of the sliding-window outbox batcher: it signals each time a drain worker arms the timer
/// of a batch window, so a test can close the window by advancing the clock only once the timer exists.
/// </summary>
/// <remarks>
/// <para>
/// The batcher arms its window as a delay on its injected clock after it drains the items it was handed. On the
/// real clock a test cannot know when that happens, and a loaded runner can leave the window open far past any short
/// wait. On a frozen clock the window cannot close by itself, but advancing before the worker has armed its timer
/// moves time past nothing, and the timer armed afterward then waits for an advance that never comes. Waiting for
/// <see cref="Armed"/> removes that race: the timer is registered with the fake clock before the signal is raised.
/// </para>
/// <para>
/// A window timer is told apart from the strategy's idle-sweep timer by its due time. The clock is frozen between
/// advances, so every window the batcher arms is due exactly one <paramref name="window"/> after it is created.
/// </para>
/// </remarks>
/// <param name="window">The sliding window the batcher under test is configured with.</param>
internal sealed class BatchWindowClock(TimeSpan window)
    : FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero)) {
  private readonly Lock _gate = new();
  private readonly List<TaskCompletionSource> _arms = [];
  private int _armedCount;

  /// <summary>
  /// Completes once the <paramref name="count"/>-th window timer (counting from one, across every stream) has been
  /// registered with this clock.
  /// </summary>
  public Task Armed(int count) {
    lock (_gate) {
      return _armSignal(count).Task;
    }
  }

  /// <summary>
  /// Closes every window the batcher arms, one advance per armed timer, until <paramref name="done"/> completes.
  /// </summary>
  /// <remarks>
  /// Each armed timer is followed by exactly one advance made after it was registered, so no window can be left
  /// waiting: a timer that a fresh arrival canceled is advanced past harmlessly, and the timer armed in its place
  /// gets an advance of its own. Use it where the assertions do not depend on how the items were grouped into
  /// batches; a test of the grouping itself arms each window explicitly.
  /// </remarks>
  public async Task CloseWindowsUntilAsync(Task done, CancellationToken cancellationToken) {
    var count = 0;
    while (!done.IsCompleted) {
      count++;
      await Task.WhenAny(Armed(count), done).WaitAsync(cancellationToken);
      if (!done.IsCompleted) {
        Advance(window);
      }
    }
  }

  /// <inheritdoc />
  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
    // Register first, signal second: a test released by the signal must find the timer already due on advance.
    var timer = base.CreateTimer(callback, state, dueTime, period);
    if (dueTime == window) {
      TaskCompletionSource signal;
      lock (_gate) {
        signal = _armSignal(++_armedCount);
      }
      signal.TrySetResult();
    }
    return timer;
  }

  private TaskCompletionSource _armSignal(int count) {
    while (_arms.Count < count) {
      _arms.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
    return _arms[count - 1];
  }
}
