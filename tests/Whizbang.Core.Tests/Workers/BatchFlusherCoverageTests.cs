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

  // A batch that throws must not stop the flusher -- five different workers (lease renewals,
  // inbox commits, perspective and outbox completions, message failures) share this type, so one
  // bad flush killing the loop would silently stop ALL of that worker's completions for the rest
  // of the process.
  //
  // Develop's _flushWithRetryAsync changed HOW that is achieved: a failed flush is now retried in
  // place rather than discarded, because losing these items leaves rows leased until expiry and
  // re-claimed after, which is how one transient timeout became lease churn under a bulk import.
  // So the guarantee is now strictly stronger and this test asserts both halves: the loop survives
  // AND the failed batch's item is not lost. An earlier version of this test asserted only that a
  // LATER batch arrived, which the retry behaviour legitimately breaks -- the second flush call is
  // now a retry of the first batch, not a new one.
  [Test]
  [Timeout(15000)]
  public async Task FlushThrowsOnce_RetriesWithoutLosingTheItemAndKeepsProcessingLaterOnesAsync(
      CancellationToken cancellationToken) {
    var attempts = 0;
    var flushed = new List<int>();
    var gate = new Lock();
    var firstFlushFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sawBothItems = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var flusher = new BatchFlusher<int>(
      flush: (items, _) => {
        if (Interlocked.Increment(ref attempts) == 1) {
          firstFlushFailed.TrySetResult();
          throw new InvalidOperationException("simulated flush failure");
        }
        lock (gate) {
          flushed.AddRange(items);
          if (flushed.Contains(1) && flushed.Contains(2)) {
            sawBothItems.TrySetResult();
          }
        }
        return Task.CompletedTask;
      },
      options: new BatchFlusherOptions {
        CoalesceWindowMs = 10,
        MaxBatchSize = 100,
        ImmediateFlushThreshold = 1,
      },
      logger: NullLogger.Instance);

    await flusher.Writer.WriteAsync(1, cancellationToken);
    await firstFlushFailed.Task.WaitAsync(cancellationToken);
    await flusher.Writer.WriteAsync(2, cancellationToken);

    await sawBothItems.Task.WaitAsync(cancellationToken);

    List<int> seen;
    lock (gate) { seen = [.. flushed]; }

    await Assert.That(seen).Contains(1)
      .Because("the retry is the whole point: an item in a failed flush must not be dropped, or "
             + "its row stays leased until expiry and is re-claimed afterwards");
    await Assert.That(seen).Contains(2)
      .Because("the loop must still reach work queued after a failure -- one bad flush cannot be "
             + "allowed to stop every completion this flusher handles for the rest of the process");
    await Assert.That(attempts).IsGreaterThanOrEqualTo(2)
      .Because("a single attempt would mean the failure was never retried at all");
  }
}
