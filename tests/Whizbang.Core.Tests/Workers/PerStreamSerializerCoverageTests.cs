using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Round-23 coverage for <see cref="PerStreamSerializer{T}"/>'s drain-window loop-back path
/// (<c>_drainStreamAsync</c>'s nested <c>while (true)</c>, the statement after a mid-window
/// arrival wins the race against the deadline). The sibling suite's own drain-window test
/// (<c>SortComparer_ShuffledEnqueueWithinDrainWindow_ProcessesInComparerOrderAsync</c>) enqueues
/// all three items back to back, fast enough that they are normally captured by the FIRST
/// synchronous read burst before the wait ever starts — so the wait-then-arrive loop-back never
/// actually runs in that test. This file forces a second arrival to land strictly inside an
/// already-open drain window.
/// </summary>
public class PerStreamSerializerCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  private sealed record StreamItem(Guid? StreamId, Guid MessageId);

  // Target: src/Whizbang.Core/Workers/PerStreamSerializer.cs:200 — the closing brace of the
  // drain-window's inner `while (true)` body reached after `arrivalTask` wins the race with real
  // data (not a timeout, not a cancellation) and the loop goes around again. If a mid-window
  // arrival stopped looping back to collect it, near-simultaneous same-stream items would split
  // across separate flushes purely by timing luck — defeating the reason the drain window exists.
  [Test]
  [Timeout(15000)]
  public async Task DrainWindow_ItemArrivingMidWait_JoinsTheOpenBatchAsync(
      CancellationToken cancellationToken) {
    var streamId = _idProvider.NewGuid();
    var seen = new List<Guid>();
    var lockObj = new object();
    var bothProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var item1 = new StreamItem(streamId, _idProvider.NewGuid());
    var item2 = new StreamItem(streamId, _idProvider.NewGuid());

    await using var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: x => x.StreamId,
      processor: (item, ct) => {
        lock (lockObj) {
          seen.Add(item.MessageId);
          if (seen.Count == 2) {
            bothProcessed.TrySetResult();
          }
        }
        return Task.CompletedTask;
      },
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = TimeSpan.FromMilliseconds(500),
      });

    await sut.EnqueueAsync(item1, cancellationToken);
    // Real, short delay so the drain worker has certainly consumed item1 and entered its
    // Task.WhenAny wait before item2 shows up -- otherwise both items land in the initial
    // synchronous read burst and the wait-then-arrive loop-back this test targets never runs.
    // 50ms against a 500ms window leaves a wide, non-flaky margin.
    await Task.Delay(50, cancellationToken);
    await sut.EnqueueAsync(item2, cancellationToken);

    await bothProcessed.Task.WaitAsync(cancellationToken);

    await Assert.That(seen).IsEquivalentTo([item1.MessageId, item2.MessageId])
      .Because("both items belong to the same stream and arrived inside one open drain window, "
             + "so they must be coalesced into a single batch rather than flushed separately");
  }
}
