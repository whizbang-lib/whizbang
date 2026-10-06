// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="SlidingWindowApplyBatchStrategy"/> the sibling suites leave untaken: a failed
/// flush on a strategy built without a logger, and the writer-completion seam asked about a stream it
/// has never mapped.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowApplyBatchStrategy.cs</code-under-test>
[Category("Workers")]
public class SlidingWindowApplyBatchStrategyBranchCoverageTests {

  private static SlidingWindowApplyOptions _fastWindow() => new() {
    SlidingWindow = TimeSpan.FromMilliseconds(20),
    MaxWait = TimeSpan.FromMilliseconds(200),
    IdleEvictionWindow = TimeSpan.FromSeconds(30),
    IdleSweepInterval = TimeSpan.FromSeconds(10),
  };

  [Test]
  public async Task FailedFlush_WithoutALogger_IsDroppedAndTheStreamKeepsFlushingAsync() {
    // The failure report falls back to nothing when no logger was given. If reporting it threw
    // instead, the stream's drain loop would die on the first failed flush and every later signal
    // for that stream would sit in a buffer nobody reads.
    var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var attempts = 0;
    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => {
        var n = Interlocked.Increment(ref attempts);
        (n == 1 ? firstAttempt : secondAttempt).TrySetResult();
        throw new InvalidOperationException("perspective store unavailable");
      },
      logger: null!,
      options: _fastWindow());
    var streamId = Guid.CreateVersion7();

    await sut.AppendAsync(streamId);
    await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await sut.AppendAsync(streamId);
    await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(Volatile.Read(ref attempts)).IsGreaterThanOrEqualTo(2)
      .Because("the same stream flushed again after a failed flush with no logger to report it to");
  }

  [Test]
  public async Task CompleteMappedWriterForTest_UnmappedStream_ReturnsFalseAsync() {
    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (_, _, _) => Task.CompletedTask,
      logger: null!,
      options: _fastWindow());

    await Assert.That(sut.CompleteMappedWriterForTest(Guid.CreateVersion7())).IsFalse()
      .Because("there is no buffer to complete for a stream that was never appended to");
    await Assert.That(sut.ActiveStreamCount).IsEqualTo(0);
  }
}
