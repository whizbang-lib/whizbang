using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The claim window also listens to how long a claim TAKES. Acquisition cost grows with the pending
/// backlog (#714), and a window that only watches re-claim churn kept asking for the same batch while
/// each claim took seconds; more, slower cycles is the wrong direction for a system trying to catch up.
/// A claim far slower than the recent norm halves the window; a normal one never does; and the norm is
/// learned from the claims themselves, not configured.
/// </summary>
[Category("Unit")]
public class AdaptiveClaimWindowLatencyTests {

  private static AdaptiveClaimWindow _warmWindow(int ceiling, int start) {
    var window = new AdaptiveClaimWindow(ceiling, floor: 25, additiveStep: 25);
    // Grow to the requested width with clean cycles first.
    while (window.Current < start) {
      window.Observe(claimedRows: 100, reclaimedRows: 0, drainMeasured: true);
    }
    return window;
  }

  [Test]
  public async Task ObserveLatency_NormalClaims_LeaveTheWindowAloneAsync() {
    var window = _warmWindow(ceiling: 1000, start: 200);
    var before = window.Current;

    for (var i = 0; i < 20; i++) {
      window.ObserveLatency(TimeSpan.FromMilliseconds(40 + (i % 3)));
    }

    await Assert.That(window.Current).IsEqualTo(before)
      .Because("claims that take what claims usually take are not a signal");
  }

  [Test]
  public async Task ObserveLatency_AClaimFarSlowerThanTheLearnedNorm_HalvesTheWindowAsync() {
    var window = _warmWindow(ceiling: 1000, start: 400);
    for (var i = 0; i < 10; i++) {
      window.ObserveLatency(TimeSpan.FromMilliseconds(50));
    }
    var before = window.Current;

    window.ObserveLatency(TimeSpan.FromMilliseconds(2500));

    await Assert.That(window.Current).IsEqualTo(Math.Max(25, before / 2))
      .Because("a claim that took fifty times the norm means the acquisition is paying for the backlog; asking for less next time is the only lever the loop has");
  }

  [Test]
  public async Task ObserveLatency_BeforeANormExists_DoesNotReactAsync() {
    var window = _warmWindow(ceiling: 1000, start: 200);
    var before = window.Current;

    // The very first observations define the norm; nothing can be "slow" relative to nothing.
    window.ObserveLatency(TimeSpan.FromMilliseconds(3000));
    window.ObserveLatency(TimeSpan.FromMilliseconds(3000));

    await Assert.That(window.Current).IsEqualTo(before)
      .Because("with no learned norm a slow first claim is just the cold cost of a claim; shrinking on it would pin every deployment at the floor after a restart");
  }

  [Test]
  public async Task ObserveLatency_SlowButStillFast_DoesNotReactAsync() {
    var window = _warmWindow(ceiling: 1000, start: 400);
    for (var i = 0; i < 10; i++) {
      window.ObserveLatency(TimeSpan.FromMilliseconds(5));
    }
    var before = window.Current;

    // Twenty times the norm, yet 100 ms: a claim that fast is not a backlog cost worth shrinking for.
    window.ObserveLatency(TimeSpan.FromMilliseconds(100));

    await Assert.That(window.Current).IsEqualTo(before)
      .Because("the ratio alone is not enough; a claim must be slow in absolute terms before the window gives up width");
  }

  [Test]
  public async Task ObserveLatency_NeverBelowTheFloorAsync() {
    var window = new AdaptiveClaimWindow(ceiling: 1000, floor: 25, additiveStep: 25);
    for (var i = 0; i < 10; i++) {
      window.ObserveLatency(TimeSpan.FromMilliseconds(50));
    }

    for (var i = 0; i < 10; i++) {
      window.ObserveLatency(TimeSpan.FromSeconds(5));
    }

    await Assert.That(window.Current).IsEqualTo(25)
      .Because("the floor is the smallest claim that still makes progress; latency can never take it away");
  }
}
