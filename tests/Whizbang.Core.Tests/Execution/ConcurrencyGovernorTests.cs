using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// The governor's contract: widen only when work is waiting AND the resource is quiet, back off
/// hard the instant it is not, and never leave the band.
/// </summary>
/// <remarks>
/// <para>
/// These tests are the specification. The asymmetry between growth and decay is deliberate:
/// running too narrow costs latency, which is recoverable; running too wide exhausts a shared
/// resource and harms work that has nothing to do with this worker, which is not.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Execution/ConcurrencyGovernors.cs</code-under-test>
[Category("Execution")]
public class ConcurrencyGovernorTests {

  private static GovernorSignal _backlogged(int queued = 500)
    => new(queued, Contended: false, Elapsed: TimeSpan.FromMilliseconds(100));

  private static GovernorSignal _contended(int queued = 500)
    => new(queued, Contended: true, Elapsed: TimeSpan.FromMilliseconds(100));

  private static GovernorSignal _idle()
    => new(QueuedItems: 0, Contended: false, Elapsed: TimeSpan.FromMilliseconds(100));

  // ---------- FixedWidthGovernor: the constant, unchanged ----------

  [Test]
  public async Task Fixed_NeverMoves_WhateverItObservesAsync() {
    var g = new FixedWidthGovernor(16);

    g.Observe(_backlogged());
    g.Observe(_contended());
    g.Observe(_idle());

    await Assert.That(g.CurrentWidth).IsEqualTo(16)
      .Because("adopting the seam must be a pure refactor — if the default implementation drifted, "
             + "every worker that moved onto it would silently change behavior in the same commit "
             + "that was supposed to change nothing");
  }

  // ---------- AdaptiveConcurrencyGovernor: the band ----------

  [Test]
  public async Task Adaptive_StartsAtItsFloorAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 64);

    await Assert.That(g.CurrentWidth).IsEqualTo(4)
      .Because("a governor that started wide would apply peak pressure to a resource it has not "
             + "yet observed — it must earn width, not assume it");
  }

  [Test]
  public async Task Adaptive_WhenWorkIsWaitingAndNothingPushesBack_WidensAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 64);

    g.Observe(_backlogged());

    await Assert.That(g.CurrentWidth).IsGreaterThan(4)
      .Because("a backlog with a quiet resource is the whole case this exists for: hundreds of "
             + "items waiting on a handful of slots while the host sits far below its limits");
  }

  [Test]
  public async Task Adaptive_NeverExceedsItsCeilingAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 12);

    for (var i = 0; i < 200; i++) {
      g.Observe(_backlogged());
    }

    await Assert.That(g.CurrentWidth).IsLessThanOrEqualTo(12)
      .Because("the ceiling is derived from the governed resource's budget — each unit costs a "
             + "connection, and growing past the pool converts a slow drain into exhaustion that "
             + "takes out unrelated work");
  }

  [Test]
  public async Task Adaptive_OnContention_BacksOffAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 64);
    for (var i = 0; i < 20; i++) {
      g.Observe(_backlogged());
    }
    var widened = g.CurrentWidth;

    g.Observe(_contended());

    await Assert.That(widened).IsGreaterThan(4).Because("the arrange step must actually widen, or "
                                                     + "this test proves nothing about decay");
    await Assert.That(g.CurrentWidth).IsLessThan(widened)
      .Because("pushback is the one signal that must always be obeyed");
  }

  [Test]
  public async Task Adaptive_BacksOffFasterThanItGrewAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 1, ceiling: 1024);
    for (var i = 0; i < 8; i++) {
      g.Observe(_backlogged());
    }
    var beforeDecay = g.CurrentWidth;

    g.Observe(_contended());
    var afterOneDecay = g.CurrentWidth;

    var grownPerCycle = (beforeDecay - 1) / 8.0;
    var lostInOneCycle = beforeDecay - afterOneDecay;

    await Assert.That((double)lostInOneCycle).IsGreaterThan(grownPerCycle)
      .Because("additive increase with multiplicative decrease — recovering from too-narrow costs "
             + "latency, recovering from too-wide costs a shared resource, so the two directions "
             + "must not move at the same rate");
  }

  [Test]
  public async Task Adaptive_WhenBackloggedAndContended_StillBacksOffAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 64);
    for (var i = 0; i < 20; i++) {
      g.Observe(_backlogged());
    }
    var widened = g.CurrentWidth;

    g.Observe(_contended(queued: 10_000));

    await Assert.That(g.CurrentWidth).IsLessThan(widened)
      .Because("a huge backlog is exactly when the temptation to grow through pushback is "
             + "strongest, and doing so is how this class of controller causes the outage it was "
             + "added to prevent — contention must dominate backlog, never the reverse");
  }

  [Test]
  public async Task Adaptive_NeverDropsBelowItsFloorAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 64);

    for (var i = 0; i < 200; i++) {
      g.Observe(_contended());
    }

    await Assert.That(g.CurrentWidth).IsGreaterThanOrEqualTo(4)
      .Because("a governor that decayed to zero under sustained pressure would stop draining "
             + "entirely and never recover — the floor is what guarantees forward progress");
  }

  [Test]
  public async Task Adaptive_WhenNothingIsWaiting_DoesNotGrowAsync() {
    var g = new AdaptiveConcurrencyGovernor(floor: 4, ceiling: 64);

    for (var i = 0; i < 50; i++) {
      g.Observe(_idle());
    }

    await Assert.That(g.CurrentWidth).IsEqualTo(4)
      .Because("width is a response to demand; growing while idle would hold resources against "
             + "a queue that does not exist");
  }
}
