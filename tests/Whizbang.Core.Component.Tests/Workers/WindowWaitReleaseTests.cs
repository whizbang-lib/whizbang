// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A batching window that closes on its deadline releases the channel wait it raced against it (#2613).
/// </summary>
/// <remarks>
/// Each window races a wait for the next item against the window's deadline. When the deadline wins, the wait is left
/// unfinished, and the channel keeps it until its token is canceled. The window gives that wait a token of its own and
/// cancels it once the race is decided; disposing the source alone would not release it
/// (<see cref="ChannelWaitReleaseTests"/>). Each test closes many windows on a fake clock with nothing arriving and
/// counts the waits the channel still holds after each one.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowBatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class WindowWaitReleaseTests {
  private const int WINDOWS = 20;
  private static readonly TimeSpan _window = TimeSpan.FromMilliseconds(50);

  private static readonly SlidingWindowBatcherOptions _options = new() {
    MaxSize = 100,
    SlidingWindow = _window,
    MaxWait = TimeSpan.FromSeconds(1),
  };

  [Test]
  [Timeout(30000)]
  public async Task SlidingWindowBatcher_WindowsClosedByTheDeadline_LeaveNoWaitOnTheChannelAsync(CancellationToken cancellationToken) {
    var channel = Channel.CreateUnbounded<int>();
    var clock = new FakeTimeProvider();
    // Each batch makes two waits: the one that finds its first item (already written, so it completes at once) and the
    // window's wait for a second. The window's deadline is armed before its wait begins, so advancing as that wait
    // begins closes the window with nothing arrived.
    var reader = new CountingChannelReader<int>(channel.Reader, wait => {
      if (wait % 2 == 0) {
        clock.Advance(_window);
      }
    });
    var batcher = new SlidingWindowBatcher<int>(reader, _options, clock);

    var held = new List<int>();
    var batches = 0;
    channel.Writer.TryWrite(0);
    await foreach (var batch in batcher.ReadBatchesAsync(cancellationToken)) {
      held.Add(reader.OutstandingWaits);
      if (++batches == WINDOWS) {
        break;
      }
      channel.Writer.TryWrite(batches);
    }

    await Assert.That(reader.WaitCalls).IsEqualTo(WINDOWS * 2)
      .Because("every batch waited once for its first item and once inside its window");
    await Assert.That(held.Max()).IsEqualTo(0)
      .Because("a window that closed on its deadline must release the wait it raced against the deadline");
  }

  [Test]
  [Timeout(30000)]
  public async Task PerspectiveWorkerDrainWindow_WindowsClosedByTheDeadline_LeaveNoWaitOnTheChannelAsync(CancellationToken cancellationToken) {
    var channel = Channel.CreateUnbounded<Guid>();
    var clock = new FakeTimeProvider();
    // The window's only wait is for a second stream; advancing as it begins closes the window with nothing arrived.
    var reader = new CountingChannelReader<Guid>(channel.Reader, _ => clock.Advance(_window));
    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var held = new List<int>();
    for (var window = 0; window < WINDOWS; window++) {
      List<Guid> streams = [Guid.CreateVersion7()];
      await PerspectiveWorker.AccumulateDrainSignalsWithinWindowAsync(reader, streams, _options, clock, stopping.Token);
      held.Add(reader.OutstandingWaits);
    }

    await Assert.That(reader.WaitCalls).IsEqualTo(WINDOWS)
      .Because("every window waited once for a further stream");
    await Assert.That(held.Max()).IsEqualTo(0)
      .Because("a drain window that closed on its deadline must release the wait it raced against the deadline");
  }
}
