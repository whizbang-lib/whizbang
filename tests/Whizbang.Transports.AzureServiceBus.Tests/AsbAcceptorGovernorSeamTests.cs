using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The acceptor governor speaks the shared seam without changing what it computes.
/// </summary>
/// <remarks>
/// <para>
/// This type already implemented the adaptive policy the framework needs elsewhere — floor,
/// ceiling, grow under sustained pressure, decay when quiet — but it was a bare class, so the one
/// working implementation was private to this transport. Implementing
/// <see cref="IConcurrencyGovernor"/> makes it reusable; these tests pin that the adoption is
/// additive rather than a rewrite.
/// </para>
/// <para>
/// The one genuinely new behavior is <see cref="IConcurrencyGovernor.Observe"/> honoring an
/// explicit contention report immediately. The evaluation window exists to avoid reacting to
/// noise; a caller reporting pushback is not noise, and making it wait out the window would mean
/// holding a width the caller has already said is too wide.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbAcceptorGovernor.cs</code-under-test>
[Category("Transports")]
public class AsbAcceptorGovernorSeamTests {

  private static AsbAcceptorGovernor _build(FakeTimeProvider clock, int floor = 4, int ceiling = 64)
    => new(floor, ceiling, TimeSpan.FromSeconds(10), clock);

  [Test]
  public async Task CurrentWidth_MirrorsTheConcurrencyItAlreadyComputedAsync() {
    var clock = new FakeTimeProvider();
    var g = _build(clock);

    await Assert.That(g.CurrentWidth).IsEqualTo(g.CurrentConcurrency)
      .Because("the seam must be a rename over the existing value, not a second source of truth "
             + "that could disagree with the number actually applied to the processor");
    await Assert.That(g.CurrentWidth).IsEqualTo(g.Floor)
      .Because("it starts at its floor — width is earned by observed pressure, never assumed");
  }

  [Test]
  public async Task Observe_WithContention_DecaysImmediatelyWithoutWaitingTheWindowAsync() {
    var clock = new FakeTimeProvider();
    var g = _build(clock, floor: 2, ceiling: 64);

    // Earn width the normal way. Growth needs TWO evaluations: the first records that pressure
    // has STARTED, and only a later one, once the window has elapsed, may act on it. Advancing
    // the clock before the first evaluation proves nothing — the elapsed comparison is against
    // the moment pressure was first observed, which would be "now".
    for (var i = 0; i < 64; i++) { g.OnSessionInitializing(); }
    _ = g.Evaluate();                       // pressure starts here
    clock.Advance(TimeSpan.FromSeconds(11));
    _ = g.Evaluate();                       // window elapsed — this one grows
    var widened = g.CurrentWidth;

    g.Observe(new GovernorSignal(QueuedItems: 0, Contended: true, Elapsed: TimeSpan.FromMilliseconds(5)));

    await Assert.That(widened).IsGreaterThan(2)
      .Because("the arrange step must actually widen or the decay assertion proves nothing");
    await Assert.That(g.CurrentWidth).IsLessThan(widened)
      .Because("an explicit contention report is the caller saying the resource is already "
             + "refusing work — waiting out the evaluation window before backing off would hold "
             + "a width that is known to be too wide");
  }

  [Test]
  public async Task Observe_NeverDecaysBelowTheFloorAsync() {
    var clock = new FakeTimeProvider();
    var g = _build(clock, floor: 4, ceiling: 64);

    for (var i = 0; i < 50; i++) {
      g.Observe(new GovernorSignal(QueuedItems: 0, Contended: true, Elapsed: TimeSpan.FromMilliseconds(5)));
    }

    await Assert.That(g.CurrentWidth).IsGreaterThanOrEqualTo(4)
      .Because("sustained pushback must not decay the acceptor pool to nothing — a transport "
             + "that stops accepting sessions entirely never recovers on its own");
  }

  [Test]
  public async Task Observe_WithoutContention_LeavesThePolicyInChargeAsync() {
    var clock = new FakeTimeProvider();
    var g = _build(clock, floor: 4, ceiling: 64);
    var before = g.CurrentWidth;

    // Quiet cycles, no pressure recorded, window not elapsed: nothing should move.
    for (var i = 0; i < 10; i++) {
      g.Observe(new GovernorSignal(QueuedItems: 0, Contended: false, Elapsed: TimeSpan.FromMilliseconds(5)));
    }

    await Assert.That(g.CurrentWidth).IsEqualTo(before)
      .Because("this governor derives pressure from its own session accounting, so an uneventful "
             + "observation must not override that with the caller's queue depth — two competing "
             + "notions of pressure in one controller is how oscillation starts");
  }
}
