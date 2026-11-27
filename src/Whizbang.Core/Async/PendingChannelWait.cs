// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;

namespace Whizbang.Core.Async;

/// <summary>
/// One pending wait for an item on a channel, kept across the passes of a loop that races it against other wake sources
/// and asked for again only once it has completed (#2613).
/// </summary>
/// <remarks>
/// <para>
/// A loop that asks <see cref="ChannelReader{T}.WaitToReadAsync"/> for a fresh wait on every pass, and races it in
/// <see cref="Task.WhenAny(Task[])"/> against a timeout or a signal, abandons the wait each time something else wins.
/// The channel keeps every abandoned wait queued until an item is written or the wait's token is canceled. On the
/// host's stopping token that is never, so an idle instance grew by one queued waiter per pass on each channel for its
/// whole life, about two passes a second for hours.
/// </para>
/// <para>
/// Here the loop holds at most one wait per channel. <see cref="WaitAsync"/> returns the same pending task while it is
/// outstanding, the way <see cref="WakeSignal.WaitAsync"/> does for the wake signal (#728), and a new one only after it
/// completed. The token of the call that created the wait governs it. The analyzer WHIZ611 reports a channel wait
/// raced in <c>Task.WhenAny</c> that neither this type nor a canceled per-race token releases.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerInjectedClockTests.ChannelWaiters.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Async/PendingChannelWaitTests.cs</tests>
internal sealed class PendingChannelWait<T>(ChannelReader<T> reader) {
  private Task<bool>? _pending;

  /// <summary>
  /// The pending wait, or a new one when there is none or the last has completed: true once an item is available,
  /// false once the channel is completed.
  /// </summary>
  public Task<bool> WaitAsync(CancellationToken cancellationToken) {
    if (_pending is not { IsCompleted: false }) {
      _pending = reader.WaitToReadAsync(cancellationToken).AsTask();
    }
    return _pending;
  }
}
