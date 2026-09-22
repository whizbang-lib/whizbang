namespace Whizbang.Core.Workers;

/// <summary>
/// Carries observed re-claim churn from the worker that FETCHES rows to the worker that SIZES the
/// claim.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AdaptiveClaimWindow"/> narrows the claim batch on re-claim churn — rows arriving with
/// more than one attempt. On the stream-id claim path that signal is unavailable where it is
/// needed: the claim returns stream ids and never sees a row, while the drain worker fetches the
/// rows and sees every attempt count. The window therefore observed zero churn forever and, because
/// <c>Observe</c> short-circuits on a zero claimed-count, never adapted at all for the life of the
/// process.
/// </para>
/// <para>
/// Observed across every service in a deployment using stream parallelism: not one window resize
/// logged, while rows in the same inboxes reached attempt twenty-one. Services either landed on a
/// workable batch size by luck or spiralled, with no mechanism to correct either way.
/// </para>
/// <para>
/// This is a deliberately tiny seam — a rolling counter written by the fetch path and drained by
/// the claim path — rather than a shared queue or an event. The claim loop needs one number per
/// cycle, the fetch path produces it incidentally, and anything richer would couple two workers
/// that otherwise share nothing.
/// </para>
/// <para>
/// Reads are destructive: the window consumes a cycle's worth of evidence and starts the next cycle
/// from zero. Leaving it cumulative would let a single early burst of churn hold the window narrow
/// long after the condition cleared.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimChurnFeedbackTests.cs</tests>
public sealed class ClaimChurnFeedback {

  private int _observed;
  private int _reclaimed;

  /// <summary>
  /// Records the attempt counts of rows just fetched.
  /// </summary>
  /// <param name="attempts">Attempt count of each fetched row.</param>
  public void Report(IReadOnlyList<int> attempts) {
    ArgumentNullException.ThrowIfNull(attempts);
    var seen = 0;
    var churned = 0;
    for (var i = 0; i < attempts.Count; i++) {
      seen++;
      // Attempt 1 is a first delivery. Only a SECOND attempt means this instance held the row once
      // and did not finish it; counting >= 1 would read every healthy row as churn.
      if (attempts[i] > 1) {
        churned++;
      }
    }
    if (seen == 0) {
      return;
    }
    Interlocked.Add(ref _observed, seen);
    Interlocked.Add(ref _reclaimed, churned);
  }

  /// <summary>
  /// Takes and clears the churn observed since the last call.
  /// </summary>
  /// <returns>
  /// Rows seen and how many were re-claims. <c>Observed</c> of zero means no evidence was gathered
  /// this cycle — which callers must treat as UNMEASURED rather than as a clean cycle.
  /// </returns>
  public (int Observed, int Reclaimed) Take() {
    var observed = Interlocked.Exchange(ref _observed, 0);
    var reclaimed = Interlocked.Exchange(ref _reclaimed, 0);
    return (observed, reclaimed);
  }
}
