// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Async;

namespace Whizbang.Core.Tests.Async;

/// <summary>
/// Locks the contract that keeps a channel's waiting readers bounded in a loop that races its wait against other wake
/// sources (#2613): while a wait is pending every call returns it, and a new one is made only after it completed.
/// </summary>
[Category("Unit")]
public class PendingChannelWaitTests {
  [Test]
  public async Task WaitAsync_RepeatedWhilePending_ReturnsTheSameTaskAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var wait = new PendingChannelWait<int>(channel.Reader);

    var first = wait.WaitAsync(CancellationToken.None);
    var second = wait.WaitAsync(CancellationToken.None);
    var third = wait.WaitAsync(CancellationToken.None);

    await Assert.That(ReferenceEquals(first, second)).IsTrue()
      .Because("a pass that abandoned the wait for another wake source must get the same wait back, not a second one");
    await Assert.That(ReferenceEquals(second, third)).IsTrue();
    await Assert.That(first.IsCompleted).IsFalse();
  }

  [Test]
  public async Task WaitAsync_AfterThePendingWaitCompleted_MakesANewWaitAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var wait = new PendingChannelWait<int>(channel.Reader);
    var first = wait.WaitAsync(CancellationToken.None);

    channel.Writer.TryWrite(1);
    await Assert.That(await first).IsTrue();
    channel.Reader.TryRead(out _);
    var second = wait.WaitAsync(CancellationToken.None);

    await Assert.That(ReferenceEquals(first, second)).IsFalse()
      .Because("a completed wait has said all it can; the next pass needs a wait of its own");
    await Assert.That(second.IsCompleted).IsFalse()
      .Because("the item was taken, so the new wait waits for the next one");
  }

  [Test]
  public async Task WaitAsync_OnACompletedChannel_ReturnsFalseAsync() {
    var channel = Channel.CreateUnbounded<int>();
    var wait = new PendingChannelWait<int>(channel.Reader);

    channel.Writer.Complete();

    await Assert.That(await wait.WaitAsync(CancellationToken.None)).IsFalse();
  }
}
