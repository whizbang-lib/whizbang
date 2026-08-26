using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// The default governor must actually govern.
/// </summary>
/// <remarks>
/// <para>
/// The seam and the self-tuning strategy shipped, but every worker still defaulted to
/// <see cref="FixedWidthGovernor"/> — whose <c>Observe</c> is a no-op. So the mechanism was present,
/// wired, and exporting telemetry, while the number it reported never moved. A controller that
/// cannot change its output is not a controller; it is a constant with instrumentation.
/// </para>
/// <para>
/// These tests assert on the governor the worker ACTUALLY holds, not on a fallback expression
/// re-created in the test. That distinction is the whole point: the previous adoption tests
/// constructed their own <see cref="FixedWidthGovernor"/> and asserted on that, so they would have
/// passed unchanged no matter what the workers defaulted to.
/// </para>
/// <para>
/// Two properties matter together. It must adapt — or nothing has changed. And it must start at the
/// configured width — or shipping it silently narrows every deployment on the first restart, which
/// is a throughput regression disguised as a feature.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[Category("Execution")]
public class GovernorDefaultIsAdaptiveTests {


  /// <summary>Runs a healthy stretch so the governor has a baseline, then collapses throughput.</summary>
  /// <remarks>
  /// Decline is measured RELATIVE to the best rate seen. A governor handed a low rate as its very
  /// first observation adopts it as the baseline and correctly plateaus — it has no evidence a
  /// different width would do better. Contention only means something once there is a better rate
  /// to have fallen from, so the arrange has to establish one.
  /// </remarks>
  private static void _healthyThenContended(IConcurrencyGovernor g) {
    for (var i = 0; i < 6; i++) {
      g.Observe(new GovernorSignal(5000, false, TimeSpan.FromMilliseconds(100), CompletedItems: 1000));
    }
    for (var i = 0; i < 8; i++) {
      g.Observe(new GovernorSignal(5000, false, TimeSpan.FromMilliseconds(100), CompletedItems: 40));
    }
  }

  // ---------- outbox drain ----------

  [Test]
  public async Task OutboxDrain_DefaultGovernorAdaptsRatherThanHoldingAConstantAsync() {
    var governor = OutboxDrainWorker.CreateDefaultGovernor(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });

    var start = governor.CurrentWidth;
    _healthyThenContended(governor);

    await Assert.That(governor.CurrentWidth).IsLessThan(start)
      .Because("a default that cannot move is the FixedWidthGovernor problem again — the whole "
             + "point of the seam was to stop running a constant nobody measured");
  }

  [Test]
  public async Task OutboxDrain_StartsAtTheConfiguredWidthAsync() {
    var governor = OutboxDrainWorker.CreateDefaultGovernor(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });

    await Assert.That(governor.CurrentWidth).IsEqualTo(24)
      .Because("adopting an adaptive default must not narrow a running deployment on restart — "
             + "day one has to behave exactly like the constant it replaces, and earn any change");
  }

  [Test]
  public async Task OutboxDrain_NeverExceedsTheConfiguredMaximumAsync() {
    var governor = OutboxDrainWorker.CreateDefaultGovernor(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });

    // Throughput improving every cycle — every reason to grow.
    var per = 100;
    for (var i = 0; i < 200; i++) {
      governor.Observe(new GovernorSignal(5000, false, TimeSpan.FromMilliseconds(100), CompletedItems: per));
      per += 50;
    }

    await Assert.That(governor.CurrentWidth).IsLessThanOrEqualTo(24)
      .Because("MaxConcurrentStreams means MAXIMUM. Growing past an explicit operator bound is how "
             + "a governor turns a slow path into someone else's connection-pool outage");
  }

  [Test]
  public async Task OutboxDrain_RecoversAfterContentionClearsAsync() {
    var governor = OutboxDrainWorker.CreateDefaultGovernor(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });

    _healthyThenContended(governor);
    var afterDip = governor.CurrentWidth;

    var per = 200;
    for (var i = 0; i < 20; i++) {
      governor.Observe(new GovernorSignal(5000, false, TimeSpan.FromMilliseconds(100), CompletedItems: per));
      per += 80;
    }

    await Assert.That(governor.CurrentWidth).IsGreaterThan(afterDip)
      .Because("a governor that only ever shrinks is a ratchet — one bad minute would cost the "
             + "deployment its width until someone restarts the process");
  }

  [Test]
  public async Task OutboxDrain_FloorLeavesRoomToBackOffButNotToStallAsync() {
    var governor = OutboxDrainWorker.CreateDefaultGovernor(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });

    await Assert.That(governor.Floor).IsGreaterThanOrEqualTo(1);
    await Assert.That(governor.Floor).IsLessThan(24)
      .Because("a floor equal to the ceiling is a fixed width wearing a different type name");
  }

  // ---------- perspective worker ----------

  [Test]
  public async Task PerspectiveWorker_DefaultGovernorAdaptsRatherThanHoldingAConstantAsync() {
    var governor = PerspectiveWorker.CreateDefaultGovernor(new PerspectiveWorkerOptions { MaxConcurrentPerspectives = 30 });

    var start = governor.CurrentWidth;
    _healthyThenContended(governor);

    await Assert.That(governor.CurrentWidth).IsLessThan(start)
      .Because("the perspective path shares the same database budget as every other worker; if it "
             + "cannot yield under pressure it is the one that starves the others");
  }

  [Test]
  public async Task PerspectiveWorker_StartsAtTheConfiguredWidthAsync() {
    var governor = PerspectiveWorker.CreateDefaultGovernor(new PerspectiveWorkerOptions { MaxConcurrentPerspectives = 30 });

    await Assert.That(governor.CurrentWidth).IsEqualTo(30)
      .Because("same contract as the drain: no silent narrowing of a deployment that upgrades");
  }

  [Test]
  public async Task PerspectiveWorker_NeverExceedsTheConfiguredMaximumAsync() {
    var governor = PerspectiveWorker.CreateDefaultGovernor(new PerspectiveWorkerOptions { MaxConcurrentPerspectives = 30 });

    var per = 100;
    for (var i = 0; i < 200; i++) {
      governor.Observe(new GovernorSignal(5000, false, TimeSpan.FromMilliseconds(100), CompletedItems: per));
      per += 50;
    }

    await Assert.That(governor.CurrentWidth).IsLessThanOrEqualTo(30);
  }

  // ---------- the guard that makes the whole thing safe ----------

  [Test]
  public async Task AnIdleQueueNeverNarrowsTheDefaultAsync() {
    var governor = OutboxDrainWorker.CreateDefaultGovernor(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });
    var start = governor.CurrentWidth;

    // Quiet period: nothing queued, nothing completed, for a long stretch.
    for (var i = 0; i < 100; i++) {
      governor.Observe(new GovernorSignal(QueuedItems: 0, Contended: false,
                                          Elapsed: TimeSpan.FromMilliseconds(100), CompletedItems: 0));
    }

    await Assert.That(governor.CurrentWidth).IsEqualTo(start)
      .Because("this is the failure mode that would make an adaptive default worse than a constant: "
             + "decaying through every quiet spell and meeting the next burst already narrowed");
  }

  [Test]
  public async Task AHostSuppliedGovernorStillWinsAsync() {
    var supplied = new FixedWidthGovernor(7);
    var options = Options.Create(new OutboxDrainWorkerOptions { MaxConcurrentStreams = 24 });

    await Assert.That(OutboxDrainWorker.ResolveGovernor(supplied, options.Value).CurrentWidth).IsEqualTo(7)
      .Because("an operator who wired a specific strategy has context the framework does not; a "
             + "new default must never silently override an explicit choice");
  }
}
