// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="PerStreamSerializer{T}.WaitForIdleAsync"/> giving up at its deadline. The sibling suite
/// covers the wait that ends because the channels emptied; this one covers the wait that ends
/// because its time ran out while work was still queued.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerStreamSerializer.cs</code-under-test>
[Category("Workers")]
public class PerStreamSerializerBranchCoverageTests {

  private sealed record StreamItem(Guid? StreamId, Guid MessageId);

  [Test]
  public async Task WaitForIdleAsync_DeadlineAlreadyReached_ReturnsWithoutWaitingForQueuedWorkAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var processorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseProcessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var processed = new List<Guid>();
    var lockObj = new object();
    // A frozen clock: the deadline is computed from it and nothing advances it, so a zero timeout is
    // reached the instant the wait begins.
    var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: i => i.StreamId,
      processor: async (item, _) => {
        processorStarted.TrySetResult();
        await releaseProcessor.Task;
        lock (lockObj) { processed.Add(item.MessageId); }
      },
      logger: NullLogger.Instance,
      options: new PerStreamSerializerOptions { DrainBatchWindow = TimeSpan.Zero },
      timeProvider: fakeTime);

    var first = new StreamItem(streamId, (Guid)TrackedGuid.New());
    var second = new StreamItem(streamId, (Guid)TrackedGuid.New());
    await sut.EnqueueAsync(first);
    await processorStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await sut.EnqueueAsync(second);   // queued behind the blocked first item

    await sut.WaitForIdleAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(10));

    int processedAtReturn;
    lock (lockObj) { processedAtReturn = processed.Count; }
    await Assert.That(processedAtReturn).IsEqualTo(0)
      .Because("the wait is bounded by its timeout: past the deadline it returns even though an item is still queued");

    releaseProcessor.TrySetResult();
  }
}
