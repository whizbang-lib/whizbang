using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Round-23 coverage for <see cref="BatchFlusher{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two of the five requested target lines were investigated and are reported here rather than
/// driven by a flaky or impossible test:
/// </para>
/// <para>
/// <b>Line 70</b> (<c>break;</c> in the <c>catch (OperationCanceledException)</c> around
/// <c>await _channel.Reader.ReadAsync(ct)</c>) needs the loop's internal stop token to be
/// canceled while a read is genuinely still pending. The only place that token is ever canceled
/// is inside <c>DisposeAsync</c>, which ALWAYS completes the channel writer first, synchronously,
/// before any cancellation. An idle pending read normally resolves via
/// <c>ChannelClosedException</c> (the other catch, one line down) essentially immediately once
/// the writer completes; forcing the cancellation branch instead would require the
/// writer-completion continuation to lose a race against <c>DisposeAsync</c>'s own drain-timeout
/// cancellation by a wide margin (the default drain path only cancels the token AFTER the loop
/// has already finished, and the timeout path only fires after <c>DrainTimeoutMs</c> — which is
/// long enough that a merely-idle read would have already unblocked via channel completion).
/// There is no seam to force that ordering deterministically.
/// </para>
/// <para>
/// <b>Line 142</b> (the empty body of the <c>catch (OperationCanceledException)</c> around the
/// FIRST <c>await _loop.WaitAsync(TimeSpan, CancellationToken.None)</c> in <c>DisposeAsync</c>)
/// is unreachable as written: the <c>CancellationToken.None</c> passed to that
/// <c>WaitAsync</c> can never fire, so the only way this catch triggers is if <c>_loop</c> itself
/// completes in the Canceled task status. <c>_runAsync</c> catches every
/// <see cref="OperationCanceledException"/> it can produce internally (both around the item read
/// and around the flush call) and always converts them to a plain <c>break</c>, so the task it
/// returns can only ever end RanToCompletion (normal exit) or Faulted (a truly unexpected
/// exception type neither inner catch matches) — never Canceled. This is a defensive catch
/// around a status the current implementation cannot produce.
/// </para>
/// </remarks>
public class BatchFlusherCoverageTests {

  // Target: src/Whizbang.Core/Workers/BatchFlusher.cs:97-98 (the `catch (Exception ex)` arm
  // logging a failed flush) and :100 (the closing brace of the outer `while` body, reached only
  // when the loop survives a batch and goes around for the next one). A batch that throws must
  // not stop the flusher -- five different workers (lease renewals, inbox commits, perspective
  // and outbox completions, message failures) share this type, so a single bad flush killing the
  // loop would silently stop ALL of that worker's completions for the rest of the process.
  [Test]
  [Timeout(15000)]
  public async Task FlushThrows_LogsAndTheLoopKeepsProcessingLaterBatchesAsync(
      CancellationToken cancellationToken) {
    var attempts = 0;
    var firstFlushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondBatch = new TaskCompletionSource<IReadOnlyList<int>>(
      TaskCreationOptions.RunContinuationsAsynchronously);

    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        if (Interlocked.Increment(ref attempts) == 1) {
          firstFlushStarted.TrySetResult();
          throw new InvalidOperationException("simulated flush failure");
        }
        secondBatch.TrySetResult(items);
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 10,
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 1,
      },
      logger: NullLogger.Instance);

    await flusher.Writer.WriteAsync(1, cancellationToken);
    // item 2 is only written once the FIRST flush call has already been entered (and thrown),
    // which means batch 1 was already closed over just [1] -- item 2 can only ever land in a
    // brand new second batch, so this is not a race against the first batch's coalesce window.
    await firstFlushStarted.Task.WaitAsync(cancellationToken);
    await flusher.Writer.WriteAsync(2, cancellationToken);

    var batch = await secondBatch.Task.WaitAsync(cancellationToken);

    await Assert.That(batch).Contains(2)
      .Because("the second item must still reach a flush call after the first one threw");
    await Assert.That(flusher.FlushCallCount).IsEqualTo(1)
      .Because("FlushCallCount only increments on a SUCCESSFUL flush -- the throwing call must "
             + "not be counted as one");
    await Assert.That(flusher.ItemsFlushed).IsEqualTo(batch.Count)
      .Because("ItemsFlushed must reflect only the successful batch's items, not the lost ones "
             + "from the failed flush");
  }
}
