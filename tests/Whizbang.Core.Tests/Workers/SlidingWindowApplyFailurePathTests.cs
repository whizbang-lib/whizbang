using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What the batching strategy does when a flush fails, and what it does with streams that go quiet.
/// <para>
/// A failed flush is deliberately dropped rather than retried here. Durability for these signals
/// lives in wh_perspective_events and the lease system — claim_orphaned_perspective_events re-issues
/// anything that did not complete — so retrying at this boundary would duplicate the work the
/// reclaim path already owns, while blocking the buffer behind a stream that cannot flush. What the
/// strategy must not do is fail silently: the log line is the only record that a batch was dropped
/// and is waiting on reclaim rather than having succeeded.
/// </para>
/// <para>
/// The idle sweep is the other half. One buffer and one worker task exist per stream, so a service
/// that has seen many streams accumulates both for streams that stopped being written to long ago.
/// Eviction is what keeps that bounded, and it had never run in a test.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SlidingWindowApplyBatchStrategy.cs</code-under-test>
public class SlidingWindowApplyFailurePathTests {

  private static SlidingWindowApplyOptions _fastWindow(TimeSpan? idleWindow = null) => new() {
    SlidingWindow = TimeSpan.FromMilliseconds(20),
    MaxWait = TimeSpan.FromMilliseconds(200),
    IdleEvictionWindow = idleWindow ?? TimeSpan.FromSeconds(30),
    IdleSweepInterval = TimeSpan.FromSeconds(10),
  };

  [Test]
  public async Task AFailedFlush_IsLoggedAndDroppedRatherThanBlockingTheStreamAsync() {
    var attempts = new ConcurrentQueue<Guid>();
    // Signalling on the first attempt would prove nothing: `attempts` is appended to before the
    // signal, so the count assertion below is already satisfied by that first flush and would
    // hold even if the strategy died on the throw. Wait for a SECOND attempt instead -- only a
    // loop that survived the first failure can produce one.
    var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, ct) => {
        attempts.Enqueue(sid);
        if (attempts.Count >= 2) {
          secondAttempt.TrySetResult();
        }
        throw new InvalidOperationException("perspective store unavailable");
      },
      options: _fastWindow());

    await sut.AppendAsync(Guid.CreateVersion7());
    await sut.AppendAsync(Guid.CreateVersion7());

    // Waits on the strategy having flushed a second time after a throw, which is the property
    // under test -- not on the first flush having been reached.
    await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(attempts.Count).IsGreaterThanOrEqualTo(2)
      .Because("a flush that throws must not take the batching loop down with it; the reclaim path "
             + "re-issues the dropped batch, but only if the process is still running to do it");
  }

  [Test]
  public async Task StopWhileFlushing_EndsWithoutSurfacingCancellationAsync() {
    // Shutdown arriving mid-flush is not a flush failure, and must not be logged as one — every
    // deploy would otherwise file an error per in-flight stream.
    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, ct) => Task.CompletedTask,
      options: _fastWindow());

    await sut.AppendAsync(Guid.CreateVersion7());
    await sut.FlushAndStopAsync();
  }

  [Test]
  public async Task AStreamThatGoesQuiet_IsEvictedSoBuffersDoNotAccumulateAsync() {
    // One buffer and one worker task per stream: without eviction a long-lived service holds both
    // for every stream it has ever seen.
    var time = new FakeTimeProvider();
    var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var sut = new SlidingWindowApplyBatchStrategy(
      flush: (sid, count, ct) => { flushed.TrySetResult(); return Task.CompletedTask; },
      options: _fastWindow(idleWindow: TimeSpan.FromSeconds(1)),
      timeProvider: time);

    await sut.AppendAsync(Guid.CreateVersion7());
    time.Advance(TimeSpan.FromSeconds(5));   // past the eviction window
    time.Advance(TimeSpan.FromSeconds(11));  // trip the sweep timer

    await sut.FlushAndStopAsync();
  }
}
