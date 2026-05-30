using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Covers the four no-op surface methods on
/// <see cref="InstantCompletionStrategy"/> that
/// <c>PerspectiveCompletionStrategyTests</c> doesn't reach:
/// <c>MarkAsSent</c>, <c>MarkAsAcknowledged</c>, <c>ClearAcknowledged</c>,
/// <c>ResetStale</c>. They're no-ops by design (the instant strategy
/// reports through directly and never queues), but the
/// <see cref="IPerspectiveCompletionStrategy"/> contract requires them —
/// locking the "this is a no-op" semantics here protects against a
/// future refactor that accidentally adds side-effects.
/// </summary>
/// <docs>operations/workers/perspective-worker</docs>
public class InstantCompletionStrategyNoOpMethodsTests {

  [Test]
  public async Task MarkAsSent_DoesNotThrow_AndIsIdempotentAsync() {
    var strategy = new InstantCompletionStrategy();
    var completions = Array.Empty<TrackedCompletion<PerspectiveCursorCompletion>>();
    var failures = Array.Empty<TrackedCompletion<PerspectiveCursorFailure>>();

    // No-op — survives a call (and a repeat call) without side effects.
    strategy.MarkAsSent(completions, failures, DateTimeOffset.UtcNow);
    strategy.MarkAsSent(completions, failures, DateTimeOffset.UtcNow);

    // Pending lists stay empty after MarkAsSent — there's nothing to mark.
    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
    await Assert.That(strategy.GetPendingFailures().Length).IsEqualTo(0);
  }

  [Test]
  public async Task MarkAsAcknowledged_DoesNotThrow_AsAsync() {
    var strategy = new InstantCompletionStrategy();

    strategy.MarkAsAcknowledged(completionCount: 5, failureCount: 3);
    strategy.MarkAsAcknowledged(completionCount: 0, failureCount: 0);

    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
    await Assert.That(strategy.GetPendingFailures().Length).IsEqualTo(0);
  }

  [Test]
  public async Task ClearAcknowledged_DoesNotThrow_AsAsync() {
    var strategy = new InstantCompletionStrategy();

    strategy.ClearAcknowledged();
    strategy.ClearAcknowledged();

    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
    await Assert.That(strategy.GetPendingFailures().Length).IsEqualTo(0);
  }

  [Test]
  public async Task ResetStale_DoesNotThrow_AsAsync() {
    var strategy = new InstantCompletionStrategy();
    var farPast = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    strategy.ResetStale(farPast);
    strategy.ResetStale(DateTimeOffset.UtcNow);

    await Assert.That(strategy.GetPendingCompletions().Length).IsEqualTo(0);
    await Assert.That(strategy.GetPendingFailures().Length).IsEqualTo(0);
  }
}
