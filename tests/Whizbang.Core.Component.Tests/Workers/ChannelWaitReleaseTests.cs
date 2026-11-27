// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The runtime behavior every channel-wait fix in #2613 rests on, held against the garbage collector: a channel keeps
/// a wait nobody awaits until the wait's token is canceled, and disposing the token's source does not cancel it.
/// </summary>
/// <remarks>
/// <see cref="Whizbang.Testing.Async.CountingChannelReader{T}"/> counts a wait as released once it completes or its token is canceled. These
/// tests are what make that count truthful: if a runtime ever stops releasing a canceled wait, or starts releasing an
/// abandoned one, they fail here first and the counting reader's premise is reopened. Each wait is made and dropped in
/// a method of its own, so nothing on the test's stack keeps it alive; whatever is still alive after a full collection
/// is kept by the channel.
/// </remarks>
[NotInParallel("ChannelWaitReleaseTests")]
public class ChannelWaitReleaseTests {
  [Test]
  public async Task AbandonedWait_OnAToken_NeverCanceled_IsKeptByTheChannelAsync() {
    var channel = _channel();
    using var stopping = new CancellationTokenSource();

    var released = ReleaseCheck.IsReleased(() => channel.Reader.WaitToReadAsync(stopping.Token).AsTask());

    await Assert.That(released).IsFalse()
      .Because("an unbounded channel queues a waiting reader until an item is written or its token is canceled");
    GC.KeepAlive(channel);
  }

  [Test]
  public async Task AbandonedWait_WhoseTokenIsCanceled_IsReleasedByTheChannelAsync() {
    var channel = _channel();
    using var stopping = new CancellationTokenSource();

    var released = ReleaseCheck.IsReleased(() => _abandonThenCancel(channel.Reader, stopping.Token));

    await Assert.That(released).IsTrue()
      .Because("canceling the wait's token takes the waiter off the channel's queue");
    GC.KeepAlive(channel);
  }

  [Test]
  public async Task AbandonedWait_WhoseTokenSourceIsOnlyDisposed_IsKeptByTheChannelAsync() {
    var channel = _channel();
    using var stopping = new CancellationTokenSource();

    var released = ReleaseCheck.IsReleased(() => _abandonThenDisposeOnly(channel.Reader, stopping.Token));

    await Assert.That(released).IsFalse()
      .Because("disposing a linked source does not cancel its token, so the waiter stays queued: cancel, then dispose");
    GC.KeepAlive(channel);
  }

  private static Task<bool> _abandonThenCancel(ChannelReader<int> reader, CancellationToken token) {
    var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
    var wait = reader.WaitToReadAsync(linked.Token).AsTask();
    linked.Cancel();
    linked.Dispose();
    return wait;
  }

  private static Task<bool> _abandonThenDisposeOnly(ChannelReader<int> reader, CancellationToken token) {
    var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
    var wait = reader.WaitToReadAsync(linked.Token).AsTask();
    linked.Dispose();
    return wait;
  }

  /// <summary>
  /// An unbounded channel, the kind the perspective consumer loop waits on, that runs a completed wait's continuations
  /// inline. With the default, a canceled wait finishes on the thread pool, and a work item still queued or just run
  /// can keep it reachable for a moment after the channel let it go, which a collection would read as kept. Inline,
  /// the cancel has finished with the wait before it returns, so the released case is also the positive control: the
  /// same helpers, the same collection, and the only difference left between kept and released is the channel.
  /// </summary>
  private static Channel<int> _channel() =>
    Channel.CreateUnbounded<int>(new UnboundedChannelOptions { AllowSynchronousContinuations = true });
}
