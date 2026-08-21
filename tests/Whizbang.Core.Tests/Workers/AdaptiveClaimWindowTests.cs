using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the control loop that stops a worker claiming more than it can dispatch inside the lease
/// window. A claim charges an attempt per row, so rows claimed and never reached are re-claimed at
/// another attempt each cycle and eventually dead-lettered as MaxAttemptsExceeded having never
/// reached a receptor. Re-claims are the feedback signal; AIMD is the response.
/// </summary>
[Category("Workers")]
public class AdaptiveClaimWindowTests {

  [Test]
  public async Task StartsAtTheCeilingAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000);

    await Assert.That(window.Current).IsEqualTo(1000)
      .Because("an unloaded service should not pay a warm-up penalty — the window only costs "
             + "something once churn actually appears");
  }

  [Test]
  public async Task ChurnHalvesTheWindowAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000);

    window.Observe(claimedRows: 100, reclaimedRows: 80);

    await Assert.That(window.Current).IsEqualTo(500)
      .Because("80% re-claims means the batch is far larger than throughput; multiplicative "
             + "decrease sheds that quickly instead of bleeding attempts for several more cycles");
  }

  [Test]
  public async Task SustainedChurnConvergesTowardTheFloorAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25);

    for (var cycle = 0; cycle < 20; cycle++) {
      window.Observe(claimedRows: 100, reclaimedRows: 100);
    }

    await Assert.That(window.Current).IsEqualTo(25)
      .Because("a worker that cannot keep up must converge to a batch it CAN finish, rather than "
             + "burning the retry budget of everything it over-claims");
  }

  [Test]
  public async Task NeverShrinksBelowTheFloorAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25);

    for (var cycle = 0; cycle < 50; cycle++) {
      window.Observe(claimedRows: 10, reclaimedRows: 10);
    }

    await Assert.That(window.Current).IsGreaterThanOrEqualTo(25)
      .Because("a window that collapsed to zero would stall the worker entirely — worse than the "
             + "problem it is solving");
  }

  [Test]
  public async Task CleanCyclesGrowTheWindowAdditivelyAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25, additiveStep: 25);
    window.Observe(claimedRows: 100, reclaimedRows: 100);   // 500
    window.Observe(claimedRows: 100, reclaimedRows: 100);   // 250

    window.Observe(claimedRows: 100, reclaimedRows: 0);

    await Assert.That(window.Current).IsEqualTo(275)
      .Because("recovery must be additive — doubling back up would re-enter the overload that "
             + "caused the shrink, and the loop would oscillate instead of settling");
  }

  [Test]
  public async Task GrowthStopsAtTheCeilingAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 100, floor: 10, additiveStep: 25);

    for (var cycle = 0; cycle < 20; cycle++) {
      window.Observe(claimedRows: 50, reclaimedRows: 0);
    }

    await Assert.That(window.Current).IsEqualTo(100)
      .Because("the configured batch size is still the operator's ceiling; the window may only "
             + "reduce it, never exceed it");
  }

  /// <summary>
  /// The subtle one. An idle queue returns nothing, which is not evidence of spare capacity — but
  /// counting it as a clean cycle would inflate the window during quiet periods and guarantee an
  /// overshoot the moment work arrived.
  /// </summary>
  [Test]
  public async Task EmptyClaimsDoNotInflateTheWindowAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25, additiveStep: 25);
    window.Observe(claimedRows: 100, reclaimedRows: 100);   // 500

    for (var cycle = 0; cycle < 10; cycle++) {
      window.Observe(claimedRows: 0, reclaimedRows: 0);
    }

    await Assert.That(window.Current).IsEqualTo(500)
      .Because("an empty queue says nothing about capacity; treating idleness as success is how a "
             + "control loop walks straight back into the overload it just escaped");
  }

  /// <summary>
  /// Partial churn is deliberately neither punished nor rewarded: some re-claims are ordinary
  /// retries of genuinely failing messages, and shrinking on those would starve a healthy worker.
  /// </summary>
  [Test]
  public async Task ModestChurnHoldsTheWindowSteadyAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25, additiveStep: 25, churnThreshold: 0.5);

    window.Observe(claimedRows: 100, reclaimedRows: 10);

    await Assert.That(window.Current).IsEqualTo(1000)
      .Because("a few re-claims are normal retries, not evidence of over-claiming — reacting to "
             + "them would confuse a failing handler with an oversized batch");
  }

  [Test]
  public async Task FloorAboveCeilingClampsInsteadOfThrowingAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 50, floor: 500);

    for (var cycle = 0; cycle < 10; cycle++) {
      window.Observe(claimedRows: 100, reclaimedRows: 100);
    }

    await Assert.That(window.Current).IsEqualTo(50)
      .Because("a careless configuration should degrade to a fixed-size batch, not refuse to start");
  }
}
