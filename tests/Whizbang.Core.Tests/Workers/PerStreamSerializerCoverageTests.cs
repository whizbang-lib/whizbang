using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
      processor: (item, _) => {
        lock (lockObj) {
          seen.Add(item.MessageId);
          if (seen.Count == 2) {
            bothProcessed.TrySetResult();
          }
        }
        return Task.CompletedTask;
      },
      logger: NullLogger.Instance,
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

  // ============================================================
  // The drain loop's outer catch, and the batch window's stop answer
  // ============================================================

  // The per-item error path is not itself infallible: a logging sink bound to the host's shutdown
  // token throws OperationCanceledException as the host stops, and that escapes the per-item
  // handler. The loop's outer catch is what turns it into a quiet end. Without it the stream's
  // worker faults, and nothing in this type ever observes a worker task — FlushAndStopAsync folds
  // it into the same catch it uses for a caller-canceled shutdown — so it would surface only as
  // an unobserved exception long after the shutdown that caused it.
  [Test]
  [Timeout(30000)]
  public async Task DrainWorker_CancellationEscapingTheProcessorErrorHandler_EndsTheLoopQuietlyAsync(
      CancellationToken cancellationToken) {
    var sut = new PerStreamSerializer<StreamItem>(
      streamIdSelector: item => item.StreamId,
      processor: (_, _) => Task.FromException(new InvalidOperationException("handler blew up")),
      logger: new ThrowingSink(() => new OperationCanceledException("log sink shutting down")),
      options: new PerStreamSerializerOptions {
        // No batch window: the processor runs on the first item with no clock involved at all.
        DrainBatchWindow = TimeSpan.Zero,
        IdleSweepInterval = TimeSpan.FromMinutes(5),
        IdleEvictionWindow = TimeSpan.FromSeconds(30),
      },
      timeProvider: new FakeTimeProvider());

    await sut.EnqueueAsync(new StreamItem(_idProvider.NewGuid(), _idProvider.NewGuid()), cancellationToken);

    await Assert.That(async () => await sut.WhenWorkersStoppedForTests()).ThrowsNothing()
      .Because("the drain loop absorbs a cancellation rather than faulting; a faulted worker here "
             + "is an unobserved exception that nothing in this type ever reports");
  }

  // The batch window races the next arrival against its own deadline on one linked token, so a
  // shutdown cancels both at once and which one wins is not something a test can pin. What the
  // canceled answer decides is whether the window closes or the exception escapes the stream's
  // worker — so it is asserted here directly rather than through a race.
  [Test]
  public async Task ContinueBatching_ArrivalCanceled_ClosesTheWindowAsync() {
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    var keepBatching = await StreamBatchWindow.ContinueBatchingAsync(
      Task.FromCanceled<bool>(canceled.Token));

    await Assert.That(keepBatching).IsFalse()
      .Because("a shutdown observed while waiting for the next arrival closes the window; letting "
             + "it escape would fault the stream's worker instead of ending it");
  }

  [Test]
  public async Task ContinueBatching_ChannelCompleted_ClosesTheWindowAsync() {
    var keepBatching = await StreamBatchWindow.ContinueBatchingAsync(Task.FromResult(false));

    await Assert.That(keepBatching).IsFalse()
      .Because("a completed channel has no further arrivals, so the window has nothing left to "
             + "wait for");
  }

  [Test]
  public async Task ContinueBatching_ArrivalAvailable_KeepsTheWindowOpenAsync() {
    var keepBatching = await StreamBatchWindow.ContinueBatchingAsync(Task.FromResult(true));

    await Assert.That(keepBatching).IsTrue()
      .Because("a real arrival is the whole point of the window — near-simultaneous same-stream "
             + "items have to be coalesced into one batch so the sort comparer can order them");
  }

  /// <summary>A logging sink that throws whatever it is told to throw.</summary>
  private sealed class ThrowingSink(Func<Exception> failure) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
      => throw failure();
  }
}
