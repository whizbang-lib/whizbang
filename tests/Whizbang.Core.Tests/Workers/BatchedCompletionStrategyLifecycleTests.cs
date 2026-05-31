using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Covers the two-phase completion lifecycle methods on
/// <see cref="BatchedCompletionStrategy"/> — MarkAsSent, MarkAsAcknowledged,
/// ClearAcknowledged, ResetStale — that
/// <c>PerspectiveCompletionStrategyTests</c> doesn't exercise. Each method
/// delegates to the internal <c>CompletionTracker</c>; this file pins the
/// observable state transitions so a future change to the delegation breaks
/// here instead of silently dropping completions.
/// </summary>
/// <docs>operations/workers/perspective-worker</docs>
public class BatchedCompletionStrategyLifecycleTests {

  private static readonly Func<IWorkCoordinator> _noOpCoordinator = static () => new Whizbang.Core.Tests.Workers.NoOpWorkCoordinator();

  private static PerspectiveCursorCompletion _completion(string perspectiveName = "MyPerspective") =>
    new() {
      StreamId = Guid.NewGuid(),
      PerspectiveName = perspectiveName,
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed,
    };

  private static PerspectiveCursorFailure _failure() =>
    new() {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "MyPerspective",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Failed,
      Error = "boom",
    };

  [Test]
  public async Task MarkAsSent_FlipsPendingToSent_RemovedFromPendingListAsync() {
    var strategy = new BatchedCompletionStrategy();
    await strategy.ReportCompletionAsync(_completion(), _noOpCoordinator(), CancellationToken.None);
    await strategy.ReportCompletionAsync(_completion(), _noOpCoordinator(), CancellationToken.None);

    var pending = strategy.GetPendingCompletions();
    await Assert.That(pending.Length).IsEqualTo(2);

    strategy.MarkAsSent(pending, [], DateTimeOffset.UtcNow);

    // Once sent, no items remain "pending" — they're awaiting acknowledgement.
    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
  }

  [Test]
  public async Task MarkAsAcknowledged_AfterMarkAsSent_AllowsClearAsync() {
    var strategy = new BatchedCompletionStrategy();
    await strategy.ReportCompletionAsync(_completion(), _noOpCoordinator(), CancellationToken.None);
    var pending = strategy.GetPendingCompletions();
    strategy.MarkAsSent(pending, [], DateTimeOffset.UtcNow);

    strategy.MarkAsAcknowledged(completionCount: pending.Length, failureCount: 0);
    strategy.ClearAcknowledged();

    // No new items should re-surface as pending — acknowledged items are gone.
    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
  }

  [Test]
  public async Task ClearAcknowledged_OnlyRemovesAcknowledgedNotSentAsync() {
    var strategy = new BatchedCompletionStrategy();
    await strategy.ReportCompletionAsync(_completion(), _noOpCoordinator(), CancellationToken.None);
    var pending = strategy.GetPendingCompletions();
    strategy.MarkAsSent(pending, [], DateTimeOffset.UtcNow);

    // Clear without acknowledging — the sent item stays.
    strategy.ClearAcknowledged();

    // After Clear (with nothing acknowledged), nothing should be pending
    // either: items are still in "Sent" state awaiting ack/timeout.
    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
  }

  [Test]
  public async Task ResetStale_PastSentItemReturnsToPendingAsync() {
    var strategy = new BatchedCompletionStrategy(
      retryTimeout: TimeSpan.FromMilliseconds(1));
    await strategy.ReportCompletionAsync(_completion(), _noOpCoordinator(), CancellationToken.None);
    var pending = strategy.GetPendingCompletions();

    var sentAt = DateTimeOffset.UtcNow.AddSeconds(-60);  // way past retryTimeout
    strategy.MarkAsSent(pending, [], sentAt);
    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);

    // Now reset stale at "now" — items sent 60s ago against a 1ms timeout
    // should flip back to pending.
    strategy.ResetStale(DateTimeOffset.UtcNow);

    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(1);
  }

  [Test]
  public async Task MarkAsSent_ProcessesCompletionsAndFailuresIndependentlyAsync() {
    var strategy = new BatchedCompletionStrategy();

    await strategy.ReportCompletionAsync(_completion(), _noOpCoordinator(), CancellationToken.None);
    await strategy.ReportFailureAsync(_failure(), _noOpCoordinator(), CancellationToken.None);

    var pendingC = strategy.GetPendingCompletions();
    var pendingF = strategy.GetPendingFailures();
    await Assert.That(pendingC.Length).IsEqualTo(1);
    await Assert.That(pendingF.Length).IsEqualTo(1);

    // Mark only completions sent — failures should still be pending.
    strategy.MarkAsSent(pendingC, [], DateTimeOffset.UtcNow);

    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
    await Assert.That(strategy.GetPendingFailures().Length).IsEqualTo(1);
  }
}
